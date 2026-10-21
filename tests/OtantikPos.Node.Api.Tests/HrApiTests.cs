using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OtantikPos.Node.Api.Tests;

// HR & Payroll through the API: employees as a manager makes them and an accountant asks for them,
// advances out of the safe, the takings and the daily batch, the attendance device's punches, and
// the month's pay.
public class HrApiTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
    }

    private static async Task<string> Problem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<DateOnly> TodayAsync(HttpClient client) =>
        DateOnly.Parse((await Json(await client.GetAsync("/api/financials/today", Cancel))).GetProperty("today").GetString()!, CultureInfo.InvariantCulture);

    private static object Employee(string name, string job, decimal salary, DateOnly hiredOn, string? device = null, string shift = "10:00:00", string[]? daysOff = null) =>
        new { requestId = Guid.NewGuid(), name, jobTitle = job, monthlySalary = salary, shiftStartsAt = shift, daysOff = daysOff ?? [], hiredOn = Iso(hiredOn), deviceUserId = device };

    private static async Task<Guid> HireAsync(HttpClient manager, string name, DateOnly hiredOn, string? device = null)
    {
        var id = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}", Employee(name, "Chef", 5_400, hiredOn, device), Cancel));
        return id;
    }

    private static Task<HttpResponseMessage> AdvanceAsync(HttpClient client, Guid employee, decimal amount, DateOnly date, DateOnly? fromTakingsOf = null) =>
        client.PostAsJsonAsync("/api/financials/vouchers", new
        {
            requestId = Guid.NewGuid(), transactionId = Guid.NewGuid(), kind = "EmployeeAdvance", amount, date = Iso(date), employeeId = employee,
            revenueDate = fromTakingsOf is { } day ? Iso(day) : null,
        }, Cancel);

    private static async Task ApproveAsync(HttpClient manager, JsonElement asked) =>
        await Json(await manager.PostAsJsonAsync($"/api/inventory/approval-requests/{asked.GetProperty("request").GetProperty("id").GetGuid()}/approve", new { }, Cancel));

    [Fact]
    public async Task Employees_are_a_managers_at_once_and_an_accountants_once_approved()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var cashier = await api.CashierAsync(manager);
        var start = new DateOnly(2026, 9, 1);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/hr/employees", Cancel)).StatusCode);

        var hassanId = Guid.NewGuid();
        var hassan = await Json(await manager.PutAsJsonAsync(
            $"/api/hr/employees/{hassanId}", Employee(" Hassan ", "Chef", 5_400, start, "7", daysOff: ["Friday"]), Cancel));
        Assert.Equal(("Hassan", "10:00:00", "19:00:00", 180m, 20m), (hassan.GetProperty("name").GetString(), hassan.GetProperty("shiftStartsAt").GetString(),
            hassan.GetProperty("shiftEndsAt").GetString(), hassan.GetProperty("dailyRate").GetDecimal(), hassan.GetProperty("hourlyRate").GetDecimal()));
        Assert.Equal(["Friday"], hassan.GetProperty("daysOff").EnumerateArray().Select(d => d.GetString()));

        // Another with the same number on the device is refused.
        Assert.Contains("Hassan has number 7", await Problem(await manager.PutAsJsonAsync($"/api/hr/employees/{Guid.NewGuid()}", Employee("Karim", "Waiter", 4_000, start, "7"), Cancel)));

        // An accountant's new employee waits: not on the list until approved.
        var monaId = Guid.NewGuid();
        var asked = await Json(await accountant.PutAsJsonAsync($"/api/hr/employees/{monaId}", Employee("Mona", "Waiter", 4_000, start, shift: "12:00:00"), Cancel));
        Assert.Equal("Requested", asked.GetProperty("outcome").GetString());
        Assert.Equal("New employee · Mona · Waiter · L.E 4,000.00 a month, from 12:00", asked.GetProperty("request").GetProperty("summary").GetString());
        Assert.Single((await Json(await manager.GetAsync("/api/hr/employees", Cancel))).EnumerateArray());
        await ApproveAsync(manager, asked);
        Assert.Equal(["Hassan", "Mona"], (await Json(await manager.GetAsync("/api/hr/employees", Cancel))).EnumerateArray().Select(e => e.GetProperty("name").GetString()));

        // A change asked for says what it changes, and locks the employee until it is decided.
        var raise = await Json(await accountant.PutAsJsonAsync($"/api/hr/employees/{monaId}", Employee("Mona", "Waiter", 4_500, start, shift: "12:00:00"), Cancel));
        Assert.Equal(
            [("Monthly salary", "L.E 4,000.00", "L.E 4,500.00")],
            raise.GetProperty("request").GetProperty("changes").EnumerateArray()
                .Select(c => (c.GetProperty("label").GetString(), c.GetProperty("before").GetString(), c.GetProperty("after").GetString())));
        Assert.Contains("can be changed once a manager has decided", await Problem(await manager.PostAsJsonAsync(
            $"/api/hr/employees/{monaId}/leave", new { requestId = Guid.NewGuid(), lastDay = "2026-10-15" }, Cancel)));
        await ApproveAsync(manager, raise);

        // Leaving, asked for and approved; and back, at once for a manager.
        var leaves = await Json(await accountant.PostAsJsonAsync($"/api/hr/employees/{monaId}/leave", new { requestId = Guid.NewGuid(), lastDay = "2026-10-15" }, Cancel));
        Assert.Equal("Mona leaves · last day 15 Oct 2026", leaves.GetProperty("request").GetProperty("summary").GetString());
        await ApproveAsync(manager, leaves);
        var mona = (await Json(await manager.GetAsync("/api/hr/employees", Cancel))).EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == monaId);
        Assert.Equal((4_500m, false, "2026-10-15"), (mona.GetProperty("monthlySalary").GetDecimal(), mona.GetProperty("isActive").GetBoolean(), mona.GetProperty("leftOn").GetString()));
        var back = await Json(await manager.PostAsJsonAsync($"/api/hr/employees/{monaId}/return", new { requestId = Guid.NewGuid(), returnsOn = "2026-11-01" }, Cancel));
        Assert.Equal((true, "2026-11-01"), (back.GetProperty("isActive").GetBoolean(), back.GetProperty("hiredOn").GetString()));
    }

    [Fact]
    public async Task An_advance_leaves_the_takings_at_once_and_is_owed_until_the_months_pay()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var cashier = await api.CashierAsync(manager);
        var today = await TodayAsync(manager);
        var month = new DateOnly(today.Year, today.Month, 1);
        var hassan = await HireAsync(manager, "Hassan", month.AddMonths(-1));

        // From today's takings: out of the drawer at once, before the takings are entered.
        var advance = await Json(await AdvanceAsync(manager, hassan, 300, today, fromTakingsOf: today));
        Assert.Equal(("EmployeeAdvance", "DayRevenue", -300m), (advance.GetProperty("kind").GetString(), advance.GetProperty("fund").GetString(), advance.GetProperty("amount").GetDecimal()));

        // And in the daily batch, paid as the batch is.
        var batch = await Json(await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", new
        {
            invoiceDate = Iso(today), paymentMethod = "DayRevenue", revenueDate = Iso(today), invoices = Array.Empty<object>(),
            advances = new[] { new { transactionId = Guid.NewGuid(), employeeId = hassan, amount = 200m, notes = "Rent" } },
        }, Cancel));
        Assert.Equal(("Hassan", 200m, "Applied"), (batch.GetProperty("advances")[0].GetProperty("employee").GetString(),
            batch.GetProperty("advances")[0].GetProperty("amount").GetDecimal(), batch.GetProperty("advances")[0].GetProperty("outcome").GetString()));
        Assert.Contains("An advance is cash", await Problem(await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", new
        {
            invoiceDate = Iso(today), paymentMethod = "Credit", invoices = Array.Empty<object>(),
            advances = new[] { new { transactionId = Guid.NewGuid(), employeeId = hassan, amount = 50m } },
        }, Cancel)));

        // An accountant's waits for a manager: no cash moves.
        Assert.Equal(HttpStatusCode.Accepted, (await AdvanceAsync(accountant, hassan, 1_000, today, fromTakingsOf: today)).StatusCode);

        var preview = await Json(await manager.GetAsync($"/api/financials/takings?day={Iso(today)}", Cancel));
        Assert.Equal((500m, 2), (preview.GetProperty("paidOut").GetDecimal(), preview.GetProperty("employeePayments").GetInt32()));
        var day = (await Json(await manager.GetAsync($"/api/financials/daily-cash?from={Iso(today)}&to={Iso(today)}", Cancel))).GetProperty("days")[0];
        Assert.Equal(500m, day.GetProperty("deductions").GetDecimal());
        Assert.Equal(["Advance to Hassan", "Advance to Hassan"], day.GetProperty("paid").EnumerateArray().Select(l => l.GetProperty("description").GetString()));

        var advances = await Json(await accountant.GetAsync($"/api/financials/advances?from={Iso(month)}&to={Iso(today)}", Cancel));
        Assert.Equal([(200m, Iso(month)), (300m, Iso(month))], advances.EnumerateArray().Select(a => (a.GetProperty("amount").GetDecimal(), a.GetProperty("payrollMonth").GetString())));
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/financials/advances?from={Iso(month)}&to={Iso(today)}", Cancel)).StatusCode);

        // On this month's slip, deducted.
        var slip = (await Json(await accountant.GetAsync($"/api/hr/payroll?month={Iso(month)}", Cancel))).GetProperty("slips")[0];
        Assert.Equal(500m, slip.GetProperty("figures").GetProperty("advances").GetDecimal());
        Assert.Equal(2, slip.GetProperty("advances").GetArrayLength());
        // The month is not over: its pay is not finalized yet.
        Assert.Contains("The month is not over", await Problem(await manager.PostAsync($"/api/hr/payroll/{hassan}/finalize?month={Iso(month)}", null, Cancel)));
    }

    [Fact]
    public async Task A_months_pay_is_finalized_with_its_advances_and_paid_from_the_safe()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var today = await TodayAsync(manager);
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastDay = lastMonth.AddMonths(1).AddDays(-1);
        var days = DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month);
        // Started on the month's last day, which was leave: a day's share of the salary, nothing absent.
        var hassan = await HireAsync(manager, "Hassan", lastDay);
        var first = await Json(await AdvanceAsync(manager, hassan, 50, lastDay));
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/hr/employees/{hassan}/days/{Iso(lastDay)}", new { kind = "Leave" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/days/{Iso(lastDay)}", new { kind = "Leave", notes = "Wedding" }, Cancel)).StatusCode);
        await Json(await manager.PostAsJsonAsync("/api/hr/payroll/adjustments", new
        {
            adjustmentId = Guid.NewGuid(), employeeId = hassan, month = Iso(lastMonth), kind = "Incentive", amount = 100, reason = "First day",
        }, Cancel));

        var share = Math.Round(5_400m / days, 2, MidpointRounding.AwayFromZero);
        var draft = (await Json(await manager.PostAsync($"/api/hr/payroll/calculate?month={Iso(lastMonth)}", null, Cancel))).GetProperty("slips")[0];
        var figures = draft.GetProperty("figures");
        Assert.Equal(("Draft", 1, share, 100m, 50m, 0, 0), (draft.GetProperty("status").GetString(), figures.GetProperty("daysEmployed").GetInt32(),
            figures.GetProperty("baseSalary").GetDecimal(), figures.GetProperty("incentives").GetDecimal(), figures.GetProperty("advances").GetDecimal(),
            figures.GetProperty("absentDays").GetInt32(), figures.GetProperty("openDays").GetInt32()));

        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsync($"/api/hr/payroll/{hassan}/finalize?month={Iso(lastMonth)}", null, Cancel)).StatusCode);
        var slip = await Json(await manager.PostAsync($"/api/hr/payroll/{hassan}/finalize?month={Iso(lastMonth)}", null, Cancel));
        var net = share + 100 - 50;
        Assert.Equal(("Finalized", net, net), (slip.GetProperty("status").GetString(), slip.GetProperty("figures").GetProperty("netPayable").GetDecimal(),
            slip.GetProperty("salaryOwed").GetDecimal()));

        // Finalized: its advance stays, its days and adjustments stay, and what is paid later counts next month.
        Assert.Contains("finalized pay", await Problem(await manager.PostAsJsonAsync("/api/inventory/deletion-requests", new
        {
            requestId = Guid.NewGuid(), module = "Treasury", recordId = first.GetProperty("id").GetGuid(),
        }, Cancel)));
        Assert.Contains("is finalized", await Problem(await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/days/{Iso(lastDay)}", new { kind = (string?)null }, Cancel)));
        var later = await Json(await AdvanceAsync(manager, hassan, 70, lastDay));
        Assert.Equal(Iso(lastMonth.AddMonths(1)), later.GetProperty("payrollMonth").GetString());

        // Its net paid from the safe: no more than is owed, and once.
        object Salary(decimal amount) => new
        {
            requestId = Guid.NewGuid(), transactionId = Guid.NewGuid(), kind = "SalaryPayment", amount, date = Iso(today), employeeId = hassan,
            payrollMonth = Iso(lastMonth),
        };
        Assert.Contains("pay that much or less", await Problem(await manager.PostAsJsonAsync("/api/financials/vouchers", Salary(net + 1), Cancel)));
        await Json(await manager.PostAsJsonAsync("/api/financials/vouchers", Salary(net), Cancel));
        Assert.Contains("is paid", await Problem(await manager.PostAsJsonAsync("/api/financials/vouchers", Salary(1), Cancel)));
        var paid = (await Json(await manager.GetAsync($"/api/hr/payroll?month={Iso(lastMonth)}", Cancel))).GetProperty("slips")[0];
        Assert.Equal((net, 0m), (paid.GetProperty("salaryPaid").GetDecimal(), paid.GetProperty("salaryOwed").GetDecimal()));

        // The month's wages in the KPIs: what the work cost, the advance not counted again.
        var kpis = await Json(await manager.GetAsync($"/api/costing/kpis?year={lastMonth.Year}&month={lastMonth.Month}", Cancel));
        Assert.Equal(share + 100, kpis.GetProperty("statement").GetProperty("loggedWages").GetDecimal());
    }

    [Fact]
    public async Task A_suspended_day_waits_for_the_managers_punch_out_and_a_penalty_comes_off_the_pay()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var today = await TodayAsync(manager);
        var day = today.AddDays(-3);
        var month = new DateOnly(day.Year, day.Month, 1);
        var hassan = await HireAsync(manager, "Hassan", today.AddMonths(-2));
        await Json(await manager.PostAsJsonAsync("/api/hr/punches", new
        {
            punchId = Guid.NewGuid(), employeeId = hassan, punchedAt = $"{Iso(day)}T10:00:00", reason = "The device was down",
        }, Cancel));

        // In at 10:00, never out: three days on, suspended, and listed for a manager.
        string Suspended = $"/api/hr/attendance/suspended?from={Iso(day.AddDays(-1))}&to={Iso(today)}";
        var waiting = (await Json(await accountant.GetAsync(Suspended, Cancel))).EnumerateArray().Single();
        Assert.Equal(("Hassan", Iso(day), "Suspended"), (waiting.GetProperty("employee").GetProperty("name").GetString(),
            waiting.GetProperty("day").GetProperty("date").GetString(), waiting.GetProperty("day").GetProperty("status").GetString()));

        // Resolved by the punch-out: after the punch-in, by a manager.
        Task<HttpResponseMessage> Resolve(HttpClient client, string time) => client.PostAsJsonAsync("/api/hr/attendance/resolve", new
        {
            punchId = Guid.NewGuid(), employeeId = hassan, date = Iso(day), punchedOutAt = $"{Iso(day)}T{time}", reason = "Forgot to scan out",
        }, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, (await Resolve(accountant, "19:00:00")).StatusCode);
        Assert.Contains("comes after the punch-in at 10:00", await Problem(await Resolve(manager, "09:30:00")));
        var resolved = await Json(await Resolve(manager, "19:00:00"));
        Assert.Equal(("Worked", 540), (resolved.GetProperty("status").GetString(), resolved.GetProperty("workedMinutes").GetInt32()));
        Assert.Contains("is not suspended", await Problem(await Resolve(manager, "20:00:00")));
        Assert.Empty((await Json(await accountant.GetAsync(Suspended, Cancel))).EnumerateArray());

        // Two hours' administrative penalty, a manager's: on the day, and off the pay at 20 an hour.
        string Penalty = $"/api/hr/employees/{hassan}/days/{Iso(day)}/penalty";
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync(Penalty, new { hours = 2, reason = "Left the walk-in open" }, Cancel)).StatusCode);
        Assert.Contains("Say why", await Problem(await manager.PutAsJsonAsync(Penalty, new { hours = 2 }, Cancel)));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync(Penalty, new { hours = 2, reason = "Left the walk-in open" }, Cancel)).StatusCode);
        var marked = (await Json(await accountant.GetAsync($"/api/hr/attendance?date={Iso(day)}", Cancel))).GetProperty("employees")[0].GetProperty("day");
        Assert.Equal((2m, "Left the walk-in open"), (marked.GetProperty("penaltyHours").GetDecimal(), marked.GetProperty("penaltyReason").GetString()));
        async Task<JsonElement> Figures() => (await Json(await accountant.GetAsync($"/api/hr/payroll?month={Iso(month)}", Cancel))).GetProperty("slips")[0].GetProperty("figures");
        var figures = await Figures();
        Assert.Equal((2m, 40m), (figures.GetProperty("administrativePenaltyHours").GetDecimal(), figures.GetProperty("administrativePenalty").GetDecimal()));

        // And on the month's list of penalties, with what it takes off.
        string Penalties = $"/api/hr/penalties?month={Iso(month)}";
        var listed = (await Json(await accountant.GetAsync(Penalties, Cancel))).EnumerateArray().Single();
        Assert.Equal(("Hassan", Iso(day), 2m, 40m, "Left the walk-in open"), (listed.GetProperty("employeeName").GetString(), listed.GetProperty("date").GetString(),
            listed.GetProperty("hours").GetDecimal(), listed.GetProperty("amount").GetDecimal(), listed.GetProperty("reason").GetString()));

        // Taken off again.
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync(Penalty, new { hours = (decimal?)null }, Cancel)).StatusCode);
        Assert.Equal(0m, (await Figures()).GetProperty("administrativePenalty").GetDecimal());
        Assert.Empty((await Json(await accountant.GetAsync(Penalties, Cancel))).EnumerateArray());
    }

    [Fact]
    public async Task A_public_holiday_is_everyones_paid_day_off_and_worked_earns_a_days_pay()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var today = await TodayAsync(manager);
        var day = today.AddDays(-3);
        var month = new DateOnly(day.Year, day.Month, 1);
        var hassan = await HireAsync(manager, "Hassan", today.AddMonths(-2));
        await HireAsync(manager, "Mona", today.AddMonths(-2));

        // A manager's to enter, one holiday to a day.
        object Holiday(string name, DateOnly first, DateOnly last) => new { name, firstDay = Iso(first), lastDay = Iso(last) };
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/hr/holidays/{id}", Holiday("Armed Forces Day", day, day), Cancel)).StatusCode);
        var added = await Json(await manager.PutAsJsonAsync($"/api/hr/holidays/{id}", Holiday("Armed Forces Day", day, day), Cancel));
        Assert.Equal(("Armed Forces Day", 1), (added.GetProperty("name").GetString(), added.GetProperty("days").GetInt32()));
        Assert.Contains("already", await Problem(await manager.PutAsJsonAsync($"/api/hr/holidays/{Guid.NewGuid()}", Holiday("Eid", day.AddDays(-1), day), Cancel)));
        var listed = (await Json(await accountant.GetAsync($"/api/hr/holidays?year={day.Year}", Cancel))).EnumerateArray().Single();
        Assert.Equal(Iso(day), listed.GetProperty("firstDay").GetString());

        // Hassan works it, half an hour late: measured against his shift, and a day's pay on top.
        foreach (var time in new[] { "10:30:00", "19:00:00" })
        {
            await Json(await manager.PostAsJsonAsync("/api/hr/punches", new
            {
                punchId = Guid.NewGuid(), employeeId = hassan, punchedAt = $"{Iso(day)}T{time}", reason = "Holiday shift",
            }, Cancel));
        }

        async Task<Dictionary<string, JsonElement>> Days() => (await Json(await accountant.GetAsync($"/api/hr/attendance?date={Iso(day)}", Cancel)))
            .GetProperty("employees").EnumerateArray().ToDictionary(e => e.GetProperty("employee").GetProperty("name").GetString()!, e => e.GetProperty("day"));
        var days = await Days();
        Assert.Equal(("WorkedOnHoliday", 30, "Armed Forces Day"), (days["Hassan"].GetProperty("status").GetString(),
            days["Hassan"].GetProperty("lateMinutes").GetInt32(), days["Hassan"].GetProperty("holidayName").GetString()));
        // Mona stays home: a holiday, not an absence.
        Assert.Equal("Holiday", days["Mona"].GetProperty("status").GetString());
        var slip = (await Json(await accountant.GetAsync($"/api/hr/payroll?month={Iso(month)}", Cancel))).GetProperty("slips").EnumerateArray()
            .Single(s => s.GetProperty("employee").GetProperty("name").GetString() == "Hassan").GetProperty("figures");
        Assert.Equal((1, 180m), (slip.GetProperty("holidaysWorked").GetInt32(), slip.GetProperty("holidaysWorkedPay").GetDecimal()));

        // Taken off, a manager's: the day is a working day again.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.DeleteAsync($"/api/hr/holidays/{id}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/hr/holidays/{id}", Cancel)).StatusCode);
        Assert.Equal("Absent", (await Days())["Mona"].GetProperty("status").GetString());

        // A month whose pay is finalized keeps its holidays as they were.
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastDay = lastMonth.AddMonths(1).AddDays(-1);
        var ali = await HireAsync(manager, "Ali", lastDay);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync($"/api/hr/employees/{ali}/days/{Iso(lastDay)}", new { kind = "Leave" }, Cancel)).StatusCode);
        await Json(await manager.PostAsync($"/api/hr/payroll/{ali}/finalize?month={Iso(lastMonth)}", null, Cancel));
        Assert.Contains("is finalized", await Problem(await manager.PutAsJsonAsync($"/api/hr/holidays/{Guid.NewGuid()}", Holiday("Late one", lastDay, lastDay), Cancel)));
    }

    [Fact]
    public async Task Flexible_hours_have_no_shift_to_be_late_for_and_overtime_past_nine_hours()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var today = await TodayAsync(manager);
        var day = today.AddDays(-3);
        var mona = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{mona}", new
        {
            requestId = Guid.NewGuid(), name = "Mona", jobTitle = "Waiter", monthlySalary = 4_050, shiftStartsAt = (string?)null, daysOff = Array.Empty<string>(),
            hiredOn = Iso(today.AddMonths(-2)),
        }, Cancel));
        var employee = (await Json(await manager.GetAsync("/api/hr/employees", Cancel))).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, employee.GetProperty("shiftStartsAt").ValueKind);

        // In at 11:30, out at 01:00 the next morning: one day, the business day's, and four and a half
        // hours past nine; nothing late.
        foreach (var at in new[] { $"{Iso(day)}T11:30:00", $"{Iso(day.AddDays(1))}T01:00:00" })
        {
            await Json(await manager.PostAsJsonAsync("/api/hr/punches", new { punchId = Guid.NewGuid(), employeeId = mona, punchedAt = at, reason = "Device down" }, Cancel));
        }

        var worked = (await Json(await manager.GetAsync($"/api/hr/attendance?date={Iso(day)}", Cancel))).GetProperty("employees")[0].GetProperty("day");
        Assert.Equal(("Worked", 810, 0, 0, 270), (worked.GetProperty("status").GetString(), worked.GetProperty("workedMinutes").GetInt32(),
            worked.GetProperty("lateMinutes").GetInt32(), worked.GetProperty("earlyLeaveMinutes").GetInt32(), worked.GetProperty("overtimeMinutes").GetInt32()));
        Assert.Equal((JsonValueKind.Null, $"{Iso(day.AddDays(1))}T04:00:00"), (worked.GetProperty("shiftStartsAt").ValueKind, worked.GetProperty("dayEndsAt").GetString()));
        // A working day with no punches is an absence still.
        Assert.Equal("Absent", (await Json(await manager.GetAsync($"/api/hr/attendance?date={Iso(day.AddDays(-1))}", Cancel)))
            .GetProperty("employees")[0].GetProperty("day").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_device_is_asked_for_its_users_and_its_queued_punches_come_in_bulk_and_late()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var device = api.Anonymous();
        var today = await TodayAsync(manager);
        const string Serial = "CQZ7232060112";
        await device.GetAsync($"/iclock/cdata?SN={Serial}&options=all", Cancel);
        var deviceId = (await Json(await manager.GetAsync("/api/hr/devices", Cancel))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await Json(await manager.PostAsJsonAsync($"/api/hr/devices/{deviceId}/approve", new { name = "Kitchen door" }, Cancel));
        Task<string> Poll() => device.GetStringAsync($"/iclock/getrequest?SN={Serial}", Cancel);
        Task<HttpResponseMessage> Post(string path, string body) => device.PostAsync(path, new StringContent(body, Encoding.UTF8, "text/plain"), Cancel);
        Assert.Equal("OK", await Poll());

        // A manager's command, handed to the device once when it next asks.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsync($"/api/hr/devices/{deviceId}/read-users", null, Cancel)).StatusCode);
        var asked = await Json(await manager.PostAsync($"/api/hr/devices/{deviceId}/read-users", null, Cancel));
        Assert.Equal((1, "ReadUsers"), (asked.GetProperty("number").GetInt32(), asked.GetProperty("kind").GetString()));
        Assert.Equal(1, (await Json(await manager.PostAsync($"/api/hr/devices/{deviceId}/read-users", null, Cancel))).GetProperty("number").GetInt32());
        Assert.Equal("C:1:DATA QUERY USERINFO", await Poll());
        Assert.Equal("OK", await Poll());

        // Its users: numbers and names kept, never passwords, cards or fingerprints.
        var users = string.Join("\r\n", "PIN=7\tName=Hassan\tPri=0\tPasswd=1234\tCard=99887766\tGrp=1", "PIN=12\tName=منى\tPri=0\tPasswd=", "FP PIN=7\tFID=6\tSize=1024\tTMP=AAAA");
        Assert.Equal("OK:3", await (await Post($"/iclock/cdata?SN={Serial}&table=USERINFO", users)).Content.ReadAsStringAsync(Cancel));
        Assert.Equal("OK", await (await Post($"/iclock/devicecmd?SN={Serial}", "ID=1&Return=0&CMD=DATA")).Content.ReadAsStringAsync(Cancel));
        var command = (await Json(await manager.GetAsync("/api/hr/devices", Cancel))).EnumerateArray().Single().GetProperty("commands")[0];
        Assert.Equal(0, command.GetProperty("returnCode").GetInt32());
        async Task<Dictionary<string, JsonElement>> DeviceUsers() =>
            (await Json(await accountant.GetAsync("/api/hr/device-users", Cancel))).EnumerateArray().ToDictionary(u => u.GetProperty("deviceUserId").GetString()!);
        var listed = await DeviceUsers();
        Assert.Equal(("Hassan", "منى"), (listed["7"].GetProperty("name").GetString(), listed["12"].GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, listed["7"].GetProperty("employeeId").ValueKind);
        Assert.DoesNotContain("1234", listed["7"].GetRawText());

        // Weeks away from the network: six thousand punches at once, all kept, and once when sent again.
        var start = today.AddDays(-30).ToDateTime(new TimeOnly(0, 0));
        var queued = string.Join("\r\n", Enumerable.Range(0, 6_000).Select(i =>
            $"{(i % 2 == 0 ? "7" : "12")}\t{start.AddMinutes(i * 3).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\t0\t1\t0\t0\t0"));
        foreach (var _ in Enumerable.Range(0, 2))
            Assert.Equal("OK:6000", await (await Post($"/iclock/cdata?SN={Serial}&table=ATTLOG&Stamp=900", queued)).Content.ReadAsStringAsync(Cancel));
        listed = await DeviceUsers();
        Assert.Equal((3_000, 3_000), (listed["7"].GetProperty("waitingPunches").GetInt32(), listed["12"].GetProperty("waitingPunches").GetInt32()));

        // Number 7 given to an employee: theirs now, and listed against them.
        await HireAsync(manager, "Hassan", today.AddMonths(-2), "7");
        listed = await DeviceUsers();
        Assert.Equal(("Hassan", 0), (listed["7"].GetProperty("employeeName").GetString(), listed["7"].GetProperty("waitingPunches").GetInt32()));

        // Its attendance read again from a day: the device's own command.
        var again = await Json(await manager.PostAsJsonAsync($"/api/hr/devices/{deviceId}/read-attendance", new { from = Iso(today.AddDays(-10)), to = Iso(today) }, Cancel));
        Assert.Equal(2, again.GetProperty("number").GetInt32());
        Assert.Equal($"C:2:DATA QUERY ATTLOG StartTime={Iso(today.AddDays(-10))} 00:00:00\tEndTime={Iso(today)} 23:59:59", await Poll());

        // A punch for a month whose pay is finalized: kept, and listed for a manager to see to.
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastDay = lastMonth.AddMonths(1).AddDays(-1);
        var ali = await HireAsync(manager, "Ali", lastDay, "21");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync($"/api/hr/employees/{ali}/days/{Iso(lastDay)}", new { kind = "Leave" }, Cancel)).StatusCode);
        await Json(await manager.PostAsync($"/api/hr/payroll/{ali}/finalize?month={Iso(lastMonth)}", null, Cancel));
        Assert.Equal("OK:1", await (await Post($"/iclock/cdata?SN={Serial}&table=ATTLOG&Stamp=901", $"21\t{Iso(lastDay)} 12:00:00\t0\t1")).Content.ReadAsStringAsync(Cancel));
        var late = (await Json(await accountant.GetAsync("/api/hr/attendance/late-punches", Cancel))).EnumerateArray().Single();
        Assert.Equal(("Ali", Iso(lastDay)), (late.GetProperty("employee").GetProperty("name").GetString(), late.GetProperty("day").GetString()));
        // A lone punch-in there can't be put right any more: not listed as suspended, only as late.
        Assert.DoesNotContain((await Json(await accountant.GetAsync($"/api/hr/attendance/suspended?from={Iso(lastMonth)}&to={Iso(today)}", Cancel))).EnumerateArray(),
            d => d.GetProperty("employee").GetProperty("name").GetString() == "Ali");
        object Settle() => new { punchIds = new[] { late.GetProperty("punch").GetProperty("id").GetGuid() } };
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/hr/attendance/late-punches/settle", Settle(), Cancel)).StatusCode);
        Assert.Equal(1, (await Json(await manager.PostAsJsonAsync("/api/hr/attendance/late-punches/settle", Settle(), Cancel))).GetProperty("settled").GetInt32());
        Assert.Empty((await Json(await accountant.GetAsync("/api/hr/attendance/late-punches", Cancel))).EnumerateArray());
    }

    [Fact]
    public async Task The_attendance_device_is_refused_until_approved_and_its_punches_make_the_day()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var device = api.Anonymous();
        var today = await TodayAsync(manager);
        var day = today.AddDays(-7);
        var hassan = await HireAsync(manager, "Hassan", today.AddMonths(-2), "7");
        string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";
        Task<HttpResponseMessage> Upload(string body, string stamp = "55") =>
            device.PostAsync($"/iclock/cdata?SN=CQZ7232060112&table=ATTLOG&Stamp={stamp}", new StringContent(body, Encoding.UTF8, "text/plain"), Cancel);
        var punches = Lines($"7\t{Iso(day)} 10:04:00\t0\t1\t0\t0\t0", $"7\t{Iso(day)} 19:30:00\t1\t1\t0\t0\t0", $"99\t{Iso(day)} 11:00:00\t0\t1", "not a punch");

        // Unknown: kept as waiting, and refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/iclock/cdata?SN=CQZ7232060112&options=all&pushver=2.4.1", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/iclock/getrequest?SN=CQZ7232060112", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(punches)).StatusCode);
        var waiting = (await Json(await manager.GetAsync("/api/hr/devices", Cancel))).EnumerateArray().Single();
        Assert.Equal(("CQZ7232060112", false), (waiting.GetProperty("serialNumber").GetString(), waiting.GetProperty("isApproved").GetBoolean()));
        var deviceId = waiting.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/hr/devices/{deviceId}/approve", new { name = "Door" }, Cancel)).StatusCode);
        await Json(await manager.PostAsJsonAsync($"/api/hr/devices/{deviceId}/approve", new { name = "Kitchen door" }, Cancel));

        var options = await (await device.GetAsync("/iclock/cdata?SN=CQZ7232060112&options=all", Cancel)).Content.ReadAsStringAsync(Cancel);
        Assert.StartsWith("GET OPTION FROM: CQZ7232060112\r\nATTLOGStamp=None\r\n", options);
        Assert.Equal("OK:4", await (await Upload(punches)).Content.ReadAsStringAsync(Cancel));
        // Sent again after a lost answer: taken as read, kept once.
        Assert.Equal("OK:4", await (await Upload(punches)).Content.ReadAsStringAsync(Cancel));
        Assert.Contains("ATTLOGStamp=55", await (await device.GetAsync("/iclock/cdata?SN=CQZ7232060112&options=all", Cancel)).Content.ReadAsStringAsync(Cancel));
        Assert.Equal("OK", await (await device.GetAsync("/iclock/getrequest?SN=CQZ7232060112", Cancel)).Content.ReadAsStringAsync(Cancel));

        async Task<JsonElement> Attendance() => await Json(await accountant.GetAsync($"/api/hr/attendance?date={Iso(day)}", Cancel));
        JsonElement Of(JsonElement report, string name) =>
            report.GetProperty("employees").EnumerateArray().Single(e => e.GetProperty("employee").GetProperty("name").GetString() == name).GetProperty("day");
        var report = await Attendance();
        var hassanDay = Of(report, "Hassan");
        // Four minutes late with no grace; past nine hours worked, 26 minutes over.
        Assert.Equal(("Worked", 566, 4, 26, 2), (hassanDay.GetProperty("status").GetString(), hassanDay.GetProperty("workedMinutes").GetInt32(),
            hassanDay.GetProperty("lateMinutes").GetInt32(), hassanDay.GetProperty("overtimeMinutes").GetInt32(), hassanDay.GetProperty("punches").GetArrayLength()));
        Assert.Equal(["99"], report.GetProperty("unassigned").EnumerateArray().Select(p => p.GetProperty("deviceUserId").GetString()));

        // Number 99 given to someone: their punch is theirs now, one short of a day.
        var karim = await HireAsync(manager, "Karim", today.AddMonths(-2), "99");
        report = await Attendance();
        Assert.Empty(report.GetProperty("unassigned").EnumerateArray());
        Assert.Equal("Suspended", Of(report, "Karim").GetProperty("status").GetString());

        // The missing one entered by a manager, with the reason; an accountant cannot.
        object Punch(string time) => new { punchId = Guid.NewGuid(), employeeId = karim, punchedAt = $"{Iso(day)}T{time}", reason = "Forgot to scan out" };
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/hr/punches", Punch("20:00:00"), Cancel)).StatusCode);
        await Json(await manager.PostAsJsonAsync("/api/hr/punches", Punch("20:00:00"), Cancel));
        var karimDay = Of(await Attendance(), "Karim");
        Assert.Equal(("Worked", 60), (karimDay.GetProperty("status").GetString(), karimDay.GetProperty("lateMinutes").GetInt32()));

        // A punch ignored stops counting, and counts again restored.
        var evening = hassanDay.GetProperty("punches").EnumerateArray().Single(p => p.GetProperty("punchedAt").GetString()!.EndsWith("19:30:00")).GetProperty("id").GetGuid();
        await Json(await manager.PostAsJsonAsync($"/api/hr/punches/{evening}/ignore", new { reason = "Test scan" }, Cancel));
        Assert.Equal("Suspended", Of(await Attendance(), "Hassan").GetProperty("status").GetString());
        await Json(await manager.PostAsync($"/api/hr/punches/{evening}/restore", null, Cancel));

        // A working day (a swap) is only ever a weekly day off; Hassan has none.
        Assert.Contains("only a weekly day off is marked as one", await Problem(await manager.PutAsJsonAsync(
            $"/api/hr/employees/{hassan}/days/{Iso(day)}", new { kind = "WorkingDay" }, Cancel)));

        // Ten minutes' grace: four minutes late is on time.
        await Json(await manager.PutAsJsonAsync("/api/hr/settings", new { graceMinutes = 10, doubleScanMinutes = 3 }, Cancel));
        Assert.Equal(0, Of(await Attendance(), "Hassan").GetProperty("lateMinutes").GetInt32());

        var month = new DateOnly(day.Year, day.Month, 1);
        var sheet = await Json(await accountant.GetAsync($"/api/hr/employees/{hassan}/timesheet?month={Iso(month)}", Cancel));
        Assert.Equal(DateTime.DaysInMonth(month.Year, month.Month), sheet.GetProperty("days").GetArrayLength());
        Assert.Equal(1, sheet.GetProperty("totals").GetProperty("daysWorked").GetInt32());
    }
}
