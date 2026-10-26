using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OtantikPos.Node.Api.Tests;

// Leaves through the API: the three types the till starts with, balances (a new hire's share of
// annual leave, each year afresh), requests asked in the portal or by HR, and a manager's approval,
// rejection and cancellation, the days marked on the timesheet and unpaid ones off the pay.
public class LeavesApiTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
    }

    private static async Task<string> Problem(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<DateOnly> TodayAsync(HttpClient client) =>
        DateOnly.Parse((await Json(await client.GetAsync("/api/hr/today", Cancel))).GetProperty("today").GetString()!, CultureInfo.InvariantCulture);

    // Off on Fridays.
    private static async Task<Guid> HireAsync(HttpClient manager, string name, DateOnly hiredOn, string? mobile = null)
    {
        var id = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}", new
        {
            requestId = Guid.NewGuid(), name, jobTitle = "Chef", monthlySalary = 5_400, shiftStartsAt = "10:00:00", daysOff = new[] { "Friday" },
            hiredOn = Iso(hiredOn), mobileNumber = mobile,
        }, Cancel));
        if (mobile is not null)
            await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}/pin", new { pin = "2468" }, Cancel));
        return id;
    }

    private static async Task<HttpClient> PortalAsync(TestApi api, string mobile)
    {
        var signedIn = await Json(await api.Anonymous().PostAsJsonAsync("/api/auth/employee-login", new { mobileNumber = mobile, pinCode = "2468" }, Cancel));
        var phone = api.Anonymous();
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.GetProperty("token").GetString());
        return phone;
    }

    private static async Task<Dictionary<string, JsonElement>> TypesAsync(HttpClient client) =>
        (await Json(await client.GetAsync("/api/leaves/types", Cancel))).EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

    // A year's first full week from a Sunday: in the past or ahead, never across a year's end.
    private static DateOnly FirstSunday(int year)
    {
        var day = new DateOnly(year, 1, 4);
        while (day.DayOfWeek != DayOfWeek.Sunday)
            day = day.AddDays(1);
        return day;
    }

    [Fact]
    public async Task The_till_starts_with_annual_sick_and_unpaid_leave_and_the_types_are_a_managers()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);

        var types = await TypesAsync(accountant);
        Assert.Equal(["Annual (اعتيادي)", "Sick (مرضي)", "Unpaid (بدون راتب)"], types.Keys.Order(StringComparer.Ordinal));
        var annual = types["Annual (اعتيادي)"];
        Assert.Equal((true, 21, true), (annual.GetProperty("isPaid").GetBoolean(), annual.GetProperty("defaultDaysPerYear").GetInt32(), annual.GetProperty("proratedForNewHires").GetBoolean()));
        Assert.Equal((true, 7, false), (types["Sick (مرضي)"].GetProperty("isPaid").GetBoolean(), types["Sick (مرضي)"].GetProperty("defaultDaysPerYear").GetInt32(),
            types["Sick (مرضي)"].GetProperty("proratedForNewHires").GetBoolean()));
        Assert.Equal((false, 0), (types["Unpaid (بدون راتب)"].GetProperty("isPaid").GetBoolean(), types["Unpaid (بدون راتب)"].GetProperty("defaultDaysPerYear").GetInt32()));

        var casual = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/leaves/types/{casual}", new { name = "Casual", isPaid = true, defaultDaysPerYear = 6 }, Cancel)).StatusCode);
        Assert.Equal(6, (await Json(await manager.PutAsJsonAsync($"/api/leaves/types/{casual}", new { name = "Casual", isPaid = true, defaultDaysPerYear = 6 }, Cancel)))
            .GetProperty("defaultDaysPerYear").GetInt32());
        Assert.Contains("is called Casual already", await Problem(await manager.PutAsJsonAsync($"/api/leaves/types/{Guid.NewGuid()}", new { name = " casual ", isPaid = true, defaultDaysPerYear = 6 }, Cancel)));
        Assert.Contains("no days a year", await Problem(await manager.PutAsJsonAsync($"/api/leaves/types/{casual}", new { name = "Casual", isPaid = false, defaultDaysPerYear = 6 }, Cancel)));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/leaves/types/{casual}", Cancel)).StatusCode);
    }

    [Fact]
    public async Task A_leave_is_asked_in_the_portal_approved_marked_and_cut_short_by_a_manager_and_unpaid_leave_comes_off_the_pay()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var today = await TodayAsync(manager);
        var year = today.Year;
        var hassan = await HireAsync(manager, "Hassan", new DateOnly(year - 1, 3, 1), "01012345678");
        var types = await TypesAsync(manager);
        var annual = types["Annual (اعتيادي)"].GetProperty("id").GetGuid();
        var unpaid = types["Unpaid (بدون راتب)"].GetProperty("id").GetGuid();
        var phone = await PortalAsync(api, "01012345678");

        // Their balances this year: in full, nothing asked yet.
        var mine = await Json(await phone.GetAsync("/api/portal/leaves", Cancel));
        Assert.Equal(year, mine.GetProperty("year").GetInt32());
        Assert.Equal([("Annual (اعتيادي)", 21m), ("Sick (مرضي)", 7m)],
            mine.GetProperty("balances").EnumerateArray().Select(b => (b.GetProperty("leaveTypeName").GetString(), b.GetProperty("remainingDays").GetDecimal())).Order());
        Assert.Empty(mine.GetProperty("requests").EnumerateArray());
        // The types they may ask for, unpaid among them.
        Assert.Equal([("Annual (اعتيادي)", true), ("Sick (مرضي)", true), ("Unpaid (بدون راتب)", false)],
            mine.GetProperty("types").EnumerateArray().Select(t => (t.GetProperty("name").GetString(), t.GetProperty("isPaid").GetBoolean())));

        // Sunday to Thursday, their Friday off left out: five days.
        var sunday = FirstSunday(year);
        Assert.Equal(6, (await Json(await phone.GetAsync($"/api/portal/leaves/days?startDate={Iso(sunday)}&endDate={Iso(sunday.AddDays(6))}", Cancel))).GetProperty("days").GetInt32());
        Task<HttpResponseMessage> Ask(Guid type, DateOnly from, DateOnly to, string? reason = null) =>
            phone.PostAsJsonAsync("/api/portal/leaves", new { requestId = Guid.NewGuid(), leaveTypeId = type, startDate = Iso(from), endDate = Iso(to), reason }, Cancel);
        var asked = await Json(await Ask(annual, sunday, sunday.AddDays(4), "Family wedding"));
        Assert.Equal(("Pending", 5, "Hassan", "Family wedding"), (asked.GetProperty("status").GetString(), asked.GetProperty("days").GetInt32(),
            asked.GetProperty("requestedBy").GetString(), asked.GetProperty("reason").GetString()));
        var annualId = asked.GetProperty("id").GetGuid();
        Assert.Contains("a day is asked for once", await Problem(await Ask(unpaid, sunday.AddDays(4), sunday.AddDays(8))));

        // Eight days given by a manager: five asked already, four more are too many.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/leaves/balances/{hassan}/{annual}/{year}", new { days = 8 }, Cancel)).StatusCode);
        var given = await Json(await manager.PutAsJsonAsync($"/api/leaves/balances/{hassan}/{annual}/{year}", new { days = 8 }, Cancel));
        Assert.Equal((8m, 8m, 5m), (given.GetProperty("allocatedDays").GetDecimal(), given.GetProperty("remainingDays").GetDecimal(), given.GetProperty("pendingDays").GetDecimal()));
        Assert.Contains("Only 8 days of Annual (اعتيادي) are left in", await Problem(await Ask(annual, sunday.AddDays(7), sunday.AddDays(10))));
        // Unpaid: no balance to run out.
        var unpaidId = (await Json(await Ask(unpaid, sunday.AddDays(14), sunday.AddDays(15)))).GetProperty("id").GetGuid();
        Assert.Equal(2, (await Json(await accountant.GetAsync("/api/leaves?status=Pending", Cancel))).EnumerateArray().Count());

        // A manager's to decide: not an accountant's, nor an employee's.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/leaves/{annualId}/approve", new { }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync($"/api/leaves/{annualId}/approve", new { }, Cancel)).StatusCode);
        var approved = await Json(await manager.PostAsJsonAsync($"/api/leaves/{annualId}/approve", new { notes = "Enjoy it" }, Cancel));
        Assert.Equal(("Approved", "Boss", "Enjoy it"), (approved.GetProperty("status").GetString(), approved.GetProperty("decidedBy").GetString(),
            approved.GetProperty("managerNotes").GetString()));
        async Task<JsonElement> Balance() => (await Json(await manager.GetAsync($"/api/leaves/balances?year={year}&employeeId={hassan}", Cancel)))
            .EnumerateArray().Single(b => b.GetProperty("leaveTypeId").GetGuid() == annual);
        Assert.Equal((5m, 3m), ((await Balance()).GetProperty("usedDays").GetDecimal(), (await Balance()).GetProperty("remainingDays").GetDecimal()));
        async Task<string> Status(DateOnly day) => (await Json(await accountant.GetAsync($"/api/hr/attendance?date={Iso(day)}", Cancel)))
            .GetProperty("employees").EnumerateArray().Single(e => e.GetProperty("employee").GetProperty("id").GetGuid() == hassan).GetProperty("day").GetProperty("status").GetString()!;
        Assert.Equal(["Leave", "Leave", "DayOff"], [await Status(sunday), await Status(sunday.AddDays(4)), await Status(sunday.AddDays(5))]);
        // Its days are the leave's: a manager puts them right by cancelling it, not by hand.
        Assert.Contains("cancel the leave", await Problem(await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/days/{Iso(sunday)}", new { kind = "DayOff" }, Cancel)));

        // Unpaid leave approved: two days' pay off, and no absence.
        await Json(await manager.PostAsJsonAsync($"/api/leaves/{unpaidId}/approve", new { }, Cancel));
        Assert.Equal("UnpaidLeave", await Status(sunday.AddDays(14)));
        async Task<JsonElement> Figures() => (await Json(await manager.GetAsync($"/api/hr/payroll?month={Iso(new DateOnly(year, 1, 1))}", Cancel)))
            .GetProperty("slips").EnumerateArray().Single(s => s.GetProperty("employee").GetProperty("id").GetGuid() == hassan).GetProperty("figures");
        var figures = await Figures();
        Assert.Equal((2, 360m), (figures.GetProperty("unpaidLeaveDays").GetInt32(), figures.GetProperty("unpaidLeaveDeduction").GetDecimal()));
        // The slip says which days they were.
        var slip = (await Json(await manager.GetAsync($"/api/hr/payroll?month={Iso(new DateOnly(year, 1, 1))}", Cancel)))
            .GetProperty("slips").EnumerateArray().Single(s => s.GetProperty("employee").GetProperty("id").GetGuid() == hassan);
        Assert.Equal(2, slip.GetProperty("unpaidLeaveDays").GetArrayLength());
        Assert.Equal(Iso(sunday.AddDays(14)), slip.GetProperty("unpaidLeaveDays")[0].GetString());

        // Recalled on the Wednesday: Sunday to Tuesday stay taken, two days given back and unmarked.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/leaves/{annualId}/cancel", new { from = Iso(sunday.AddDays(3)) }, Cancel)).StatusCode);
        var cut = await Json(await manager.PostAsJsonAsync($"/api/leaves/{annualId}/cancel", new { from = Iso(sunday.AddDays(3)), notes = "Recalled" }, Cancel));
        Assert.Equal(("Approved", 2, 3, Iso(sunday.AddDays(3))), (cut.GetProperty("status").GetString(), cut.GetProperty("cancelledDays").GetInt32(),
            cut.GetProperty("daysTaken").GetInt32(), cut.GetProperty("cancelledFrom").GetString()));
        Assert.Equal((3m, 5m), ((await Balance()).GetProperty("usedDays").GetDecimal(), (await Balance()).GetProperty("remainingDays").GetDecimal()));
        Assert.Equal(["Leave", "Absent"], [await Status(sunday.AddDays(2)), await Status(sunday.AddDays(3))]);
        // Its days given back can be asked for again.
        var again = await Json(await Ask(annual, sunday.AddDays(3), sunday.AddDays(3)));

        // The unpaid leave cancelled before it began, all of it: nothing off the pay.
        var off = await Json(await manager.PostAsJsonAsync($"/api/leaves/{unpaidId}/cancel", new { from = Iso(sunday.AddDays(14)) }, Cancel));
        Assert.Equal(("Cancelled", 2, 0), (off.GetProperty("status").GetString(), off.GetProperty("cancelledDays").GetInt32(), off.GetProperty("daysTaken").GetInt32()));
        Assert.Equal(0, (await Figures()).GetProperty("unpaidLeaveDays").GetInt32());

        // Rejected, with why.
        var rejected = await Json(await manager.PostAsJsonAsync($"/api/leaves/{again.GetProperty("id").GetGuid()}/reject", new { notes = "Inventory day" }, Cancel));
        Assert.Equal(("Rejected", "Inventory day"), (rejected.GetProperty("status").GetString(), rejected.GetProperty("managerNotes").GetString()));
        Assert.Contains("rejected already", await Problem(await manager.PostAsJsonAsync($"/api/leaves/{again.GetProperty("id").GetGuid()}/approve", new { }, Cancel)));

        // In the portal, their requests as they stand.
        var theirs = (await Json(await phone.GetAsync("/api/portal/leaves", Cancel))).GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("status").GetString()).Order();
        Assert.Equal(["Approved", "Cancelled", "Rejected"], theirs);
        // And next year opens afresh: nothing carried over.
        var next = (await Json(await manager.GetAsync($"/api/leaves/balances?year={year + 1}&employeeId={hassan}", Cancel))).EnumerateArray().Single(b => b.GetProperty("leaveTypeId").GetGuid() == annual);
        Assert.Equal((21m, 0m, false), (next.GetProperty("allocatedDays").GetDecimal(), next.GetProperty("usedDays").GetDecimal(), next.GetProperty("opened").GetBoolean()));
    }

    [Fact]
    public async Task A_new_hire_has_their_share_of_annual_leave_their_sick_leave_in_full()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var year = (await TodayAsync(manager)).Year;
        var mona = await HireAsync(manager, "Mona", new DateOnly(year, 7, 2));
        var daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        var share = Math.Round(21m * (daysInYear - new DateOnly(year, 7, 2).DayOfYear + 1) / daysInYear * 2, MidpointRounding.AwayFromZero) / 2;

        var balances = (await Json(await manager.GetAsync($"/api/leaves/balances?year={year}", Cancel))).EnumerateArray()
            .Where(b => b.GetProperty("employeeId").GetGuid() == mona)
            .ToDictionary(b => b.GetProperty("leaveTypeName").GetString()!, b => b.GetProperty("allocatedDays").GetDecimal());
        Assert.Equal(10.5m, share);
        Assert.Equal(share, balances["Annual (اعتيادي)"]);
        Assert.Equal(7m, balances["Sick (مرضي)"]);
        Assert.False(balances.ContainsKey("Unpaid (بدون راتب)"));
    }
}
