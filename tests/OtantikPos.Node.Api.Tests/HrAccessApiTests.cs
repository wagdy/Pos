using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Api.Controllers;
using OtantikPos.Node.Infrastructure.Identity;

namespace OtantikPos.Node.Api.Tests;

// Employee permissions (صلاحيات الموظفين) through the API: the HR tabs a manager gives an employee,
// who works them in the portal, and an accountant, on the till; what each tab opens, what it never
// shows, and that a change of tabs signs an employee out.
public class HrAccessApiTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<DateOnly> TodayAsync(HttpClient client) =>
        DateOnly.Parse((await Json(await client.GetAsync("/api/hr/today", Cancel))).GetProperty("today").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<Guid> HireAsync(HttpClient manager, string name, string mobile)
    {
        var id = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}", new
        {
            requestId = Guid.NewGuid(), name, jobTitle = "Supervisor", monthlySalary = 9_000, shiftStartsAt = "10:00:00", daysOff = Array.Empty<string>(),
            hiredOn = "2026-01-05", mobileNumber = mobile, nationalId = "29001010123456",
        }, Cancel));
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}/pin", new { pin = "2468" }, Cancel));
        return id;
    }

    private static Task<HttpResponseMessage> GiveAsync(HttpClient manager, Guid employee, params string[] tabs) =>
        manager.PutAsJsonAsync($"/api/hr/access/employees/{employee}", new { tabs }, Cancel);

    private static async Task<(HttpClient Phone, JsonElement SignedIn)> PortalAsync(TestApi api, string mobile)
    {
        var signedIn = await Json(await api.Anonymous().PostAsJsonAsync("/api/auth/employee-login", new { mobileNumber = mobile, pinCode = "2468" }, Cancel));
        var phone = api.Anonymous();
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.GetProperty("token").GetString());
        return (phone, signedIn);
    }

    private static List<string> Names(JsonElement tabs) => tabs.EnumerateArray().Select(t => t.GetString()!).ToList();

    // What the token says, as the portal reads it: its payload, base64url.
    private static JsonElement Payload(string token)
    {
        var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=')))).RootElement;
    }

    [Fact]
    public async Task An_employee_works_only_the_tabs_a_manager_gave_them_and_as_an_accountant_does()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var hassan = await HireAsync(manager, "Hassan", "01012345678");
        var today = await TodayAsync(manager);
        var month = new DateOnly(today.Year, today.Month, 1);

        // None to start with: their own portal, and nothing of HR.
        var (none, _) = await PortalAsync(api, "01012345678");
        Assert.Empty(Names((await Json(await none.GetAsync("/api/portal/me", Cancel))).GetProperty("hrTabs")));
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/hr/attendance/now", Cancel)).StatusCode);

        // Now and Attendance. The sign-in before is signed out: its tabs are not theirs any more.
        var given = await Json(await GiveAsync(manager, hassan, "Now", "Attendance"));
        Assert.Equal(["Now", "Attendance"], Names(given.GetProperty("tabs")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await none.GetAsync("/api/portal/me", Cancel)).StatusCode);
        var (phone, signedIn) = await PortalAsync(api, "01012345678");
        Assert.Equal(["Now", "Attendance"], Names(signedIn.GetProperty("hrTabs")));
        Assert.Equal(["Now", "Attendance"], Names(Payload(signedIn.GetProperty("token").GetString()!).GetProperty("hr_tabs")));
        Assert.Equal(["Now", "Attendance"], Names((await Json(await phone.GetAsync("/api/portal/me", Cancel))).GetProperty("hrTabs")));

        // Their tabs open; the others do not.
        foreach (var path in new[] { "/api/hr/attendance/now", $"/api/hr/attendance?date={Iso(today)}", $"/api/hr/employees/{hassan}/timesheet?month={Iso(month)}", "/api/hr/today", "/api/hr/departments", "/api/hr/devices" })
            Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync(path, Cancel)).StatusCode);
        foreach (var path in new[] { $"/api/hr/payroll?month={Iso(month)}", $"/api/hr/advances?month={Iso(month)}", $"/api/hr/employees/{hassan}", "/api/hr/attendance/suspended", $"/api/hr/holidays?year={today.Year}", "/api/hr/settings", "/api/hr/access", "/api/hr/employees/export" })
            Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync(path, Cancel)).StatusCode);

        // The Attendance tab has everyone's names and shifts, never what anyone earns or their ID.
        var roster = await (await phone.GetAsync("/api/hr/employees", Cancel)).Content.ReadAsStringAsync(Cancel);
        var listed = JsonDocument.Parse(roster).RootElement.EnumerateArray().Single();
        Assert.Equal(("Hassan", "10:00:00"), (listed.GetProperty("name").GetString(), listed.GetProperty("shiftStartsAt").GetString()));
        foreach (var hidden in new[] { "monthlySalary", "dailyWage", "dailyRate", "hourlyRate", "nationalId", "mobileNumber", "idPhoto" })
            Assert.Equal(JsonValueKind.Null, listed.GetProperty(hidden).ValueKind);
        Assert.DoesNotContain("9000", roster);
        Assert.DoesNotContain("29001010123456", roster);
        var sheet = await (await phone.GetAsync($"/api/hr/employees/{hassan}/timesheet?month={Iso(month)}", Cancel)).Content.ReadAsStringAsync(Cancel);
        Assert.DoesNotContain("29001010123456", sheet);
        // Nor what a department costs.
        var departments = (await Json(await phone.GetAsync("/api/hr/departments", Cancel))).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, departments.GetProperty("monthlyCost").ValueKind);

        // What only a manager does stays a manager's.
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/hr/punches", new
        {
            punchId = Guid.NewGuid(), employeeId = hassan, punchedAt = $"{Iso(today)}T10:00:00", reason = "Forgot",
        }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GiveAsync(phone, hassan, "Payroll")).StatusCode);

        // With Employees: their pay shows, and a change they make is asked of a manager.
        await Json(await GiveAsync(manager, hassan, "Now", "Attendance", "Employees"));
        (phone, _) = await PortalAsync(api, "01012345678");
        var employee = await Json(await phone.GetAsync($"/api/hr/employees/{hassan}", Cancel));
        Assert.Equal(9_000, employee.GetProperty("monthlySalary").GetDecimal());
        var raise = await phone.PutAsJsonAsync($"/api/hr/employees/{hassan}", new
        {
            requestId = Guid.NewGuid(), name = "Hassan", jobTitle = "Supervisor", monthlySalary = 20_000, shiftStartsAt = "10:00:00",
            daysOff = Array.Empty<string>(), hiredOn = "2026-01-05", mobileNumber = "01012345678", reason = "A raise",
        }, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, raise.StatusCode);
        var asked = (await Json(raise)).GetProperty("request");
        Assert.Equal("Hassan", asked.GetProperty("requestedBy").GetString());
        Assert.Equal(9_000, (await Json(await manager.GetAsync($"/api/hr/employees/{hassan}", Cancel))).GetProperty("monthlySalary").GetDecimal());

        // Every tab for a manager; the list of who has which, a manager's alone.
        Assert.Equal(HrTabSet.Each.Select(t => t.ToString()).ToList(), Names((await Json(await manager.GetAsync("/api/auth/me", Cancel))).GetProperty("hrTabs")));
        var access = await Json(await manager.GetAsync("/api/hr/access", Cancel));
        var row = access.GetProperty("employees").EnumerateArray().Single();
        Assert.Equal((hassan, true, true), (row.GetProperty("id").GetGuid(), row.GetProperty("hasMobile").GetBoolean(), row.GetProperty("hasPin").GetBoolean()));
        Assert.Equal(["Now", "Employees", "Attendance"], Names(row.GetProperty("tabs")));

        // Leaving takes them out of every tab (the raise turned down first: it locks their record).
        await Json(await manager.PostAsJsonAsync($"/api/inventory/approval-requests/{asked.GetProperty("id").GetGuid()}/reject", new { note = "Not now" }, Cancel));
        await Json(await manager.PostAsJsonAsync($"/api/hr/employees/{hassan}/leave", new { requestId = Guid.NewGuid(), lastDay = Iso(today) }, Cancel));
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/hr/attendance/now", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await GiveAsync(manager, hassan, "Now")).StatusCode);
    }

    [Fact]
    public async Task An_accountant_sees_every_tab_until_a_manager_ticks_theirs()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var cashier = await api.CashierAsync(manager);
        var today = await TodayAsync(manager);
        var month = new DateOnly(today.Year, today.Month, 1);

        var all = HrTabSet.Each.Select(t => t.ToString()).ToList();
        Assert.Equal(all, Names((await Json(await accountant.GetAsync("/api/auth/me", Cancel))).GetProperty("hrTabs")));
        var mona = (await Json(await manager.GetAsync("/api/hr/access", Cancel))).GetProperty("staff").EnumerateArray().Single();
        Assert.Equal((TestApi.AccountantId, false), (mona.GetProperty("id").GetString(), mona.GetProperty("restricted").GetBoolean()));
        Assert.Equal(all, Names(mona.GetProperty("tabs")));

        var ticked = await Json(await manager.PutAsJsonAsync($"/api/hr/access/staff/{TestApi.AccountantId}", new { tabs = new[] { "Payroll", "Advances" } }, Cancel));
        Assert.True(ticked.GetProperty("restricted").GetBoolean());
        Assert.Equal(["Payroll", "Advances"], Names((await Json(await accountant.GetAsync("/api/auth/me", Cancel))).GetProperty("hrTabs")));
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/hr/attendance/now", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync($"/api/hr/attendance?date={Iso(today)}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/hr/payroll?month={Iso(month)}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/hr/advances?month={Iso(month)}", Cancel)).StatusCode);

        // A manager's to give, and only to the accountants.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/hr/access", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.PutAsJsonAsync($"/api/hr/access/staff/{TestApi.AccountantId}", new { tabs = HrTabSet.Each.Select(t => t.ToString()) }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync($"/api/hr/access/staff/{TestApi.CashierId}", new { tabs = new[] { "Now" } }, Cancel)).StatusCode);

        // A cashier sees no HR at all, as before.
        Assert.Empty(Names((await Json(await cashier.GetAsync("/api/auth/me", Cancel))).GetProperty("hrTabs")));
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/hr/attendance/now", Cancel)).StatusCode);
    }

    [Fact]
    public async Task On_the_portals_port_only_an_employee_holds_a_tab()
    {
        // The test server takes every request on port 0: as if each came in on the portal's port.
        await using var api = await TestApi.StartAsync(new Dictionary<string, string> { ["Portal:Port"] = "0" });
        using var scope = api.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var hassan = Guid.NewGuid();
        await sender.Send(new ChangeEmployeeCommand(hassan, Guid.NewGuid(), new EmployeeRequest(new EmployeeDetails(
            "Hassan", "Supervisor", 9_000, new TimeOnly(10, 0), [], new DateOnly(2026, 1, 5), null, OtantikPos.Hr.Domain.Employees.EmploymentType.Monthly, 0, null, null,
            null, "01012345678")), null, "boss", "Boss", CanApprove: true), Cancel);
        await sender.Send(new SetEmployeePinCommand(hassan, "2468"), Cancel);
        await sender.Send(new SetEmployeeHrTabsCommand(hassan, HrTabs.Now), Cancel);

        var (phone, _) = await PortalAsync(api, "01012345678");
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/hr/attendance/now", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/hr/settings", Cancel)).StatusCode);

        // A manager's token, brought over the Wi-Fi, opens nothing of HR there.
        var boss = (await scope.ServiceProvider.GetRequiredService<StaffDirectory>().GetTillStaffAsync(Cancel)).Single(s => s.FullName == "Boss");
        var replayed = api.Anonymous();
        replayed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Services.GetRequiredService<TokenService>().Issue(boss).Token);
        foreach (var path in new[] { "/api/hr/attendance/now", "/api/hr/employees", "/api/hr/access" })
            Assert.Equal(HttpStatusCode.Forbidden, (await replayed.GetAsync(path, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await replayed.PostAsJsonAsync("/api/hr/devices/sync", new { address = "192.168.1.106" }, Cancel)).StatusCode);
    }

    // Every HR endpoint says its tabs, so none is open to whoever sees HR at all by being forgotten.
    [Fact]
    public void Every_hr_endpoint_says_its_tabs()
    {
        var actions = typeof(HrController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
            .ToList();
        Assert.NotEmpty(actions);
        Assert.Empty(actions.Where(m => m.GetCustomAttribute<HrTabAttribute>() is null).Select(m => m.Name));
        // And none of them still asks HrView or HrManage alone, which an accountant would pass whatever their tabs.
        Assert.Empty(actions.Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Any(a => a.Policy is Otantik.SharedKernel.Authorization.Permissions.HrView or Otantik.SharedKernel.Authorization.Permissions.HrManage)).Select(m => m.Name));
    }
}
