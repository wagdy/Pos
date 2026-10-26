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

    private static async Task<Guid> HireAsync(HttpClient manager, string name, string mobile, string nationalId = "29001010123456")
    {
        var id = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}", new
        {
            requestId = Guid.NewGuid(), name, jobTitle = "Supervisor", monthlySalary = 9_000, shiftStartsAt = "10:00:00", daysOff = Array.Empty<string>(),
            hiredOn = "2026-01-05", mobileNumber = mobile, nationalId,
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

        // What only a manager does otherwise they do at once in their tabs, and in no other: a punch
        // put right on Attendance, not a holiday. Who holds which tab stays a manager's.
        var punch = await Json(await phone.PostAsJsonAsync("/api/hr/punches", new
        {
            punchId = Guid.NewGuid(), employeeId = hassan, punchedAt = $"{Iso(today)}T10:00:00", reason = "Forgot",
        }, Cancel));
        Assert.Equal("Hassan", punch.GetProperty("recordedBy").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await phone.PutAsJsonAsync($"/api/hr/holidays/{Guid.NewGuid()}", new { name = "Eid", firstDay = Iso(today), lastDay = Iso(today) }, Cancel)).StatusCode);
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

    // Every HR and Custody endpoint says its tabs, so none is open to whoever sees HR at all by
    // being forgotten; the PINs and who holds which tab stay a manager's alone.
    [Fact]
    public void Every_hr_endpoint_says_its_tabs()
    {
        var actions = new[] { typeof(HrController), typeof(CustodyController), typeof(LeavesController), typeof(PerformanceController) }
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
            .ToList();
        Assert.NotEmpty(actions);
        Assert.Empty(actions.Where(m => m.GetCustomAttribute<HrTabAttribute>() is null).Select(m => m.Name));
        // And none of them still asks HrView or HrManage alone, which an accountant would pass whatever their tabs.
        Assert.Empty(actions.Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Any(a => a.Policy is Otantik.SharedKernel.Authorization.Permissions.HrView or Otantik.SharedKernel.Authorization.Permissions.HrManage)).Select(m => m.Name));
        var managers = actions.Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Any(a => a.Policy == Otantik.SharedKernel.Authorization.Permissions.ApprovalDecide)).Select(m => m.Name).Order().ToList();
        string[] expected =
        [
            // HR: the PINs and who holds which tab.
            "GetAccess", "RemovePin", "SetEmployeeAccess", "SetPin", "SetStaffAccess",
            // Leaves: deciding them, the balances' days and the types.
            "Approve", "Cancel", "DeleteType", "Reject", "SaveType", "SetBalance",
            // Performance & Disciplinary: all of it but reading.
            "DeleteKpi", "IssueAction", "RecordEvaluation", "RestoreKpi", "RetireKpi", "SaveKpi", "WithdrawAction",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), managers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task In_their_tabs_an_employee_does_at_once_what_only_a_manager_did_and_never_more()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var hassan = await HireAsync(manager, "Hassan", "01012345678");
        var mona = await HireAsync(manager, "Mona", "01087654321", "29001010654321");
        var today = await TodayAsync(manager);
        await Json(await GiveAsync(manager, hassan, "Departments", "Holidays", "Employees"));
        var (phone, _) = await PortalAsync(api, "01012345678");

        // A department and its role, and a holiday: made at once.
        var bar = Guid.NewGuid();
        Assert.Equal("Bar", (await Json(await phone.PutAsJsonAsync($"/api/hr/departments/{bar}", new { name = "Bar" }, Cancel))).GetProperty("name").GetString());
        await Json(await phone.PutAsJsonAsync($"/api/hr/departments/{bar}/roles/{Guid.NewGuid()}", new { name = "Barista" }, Cancel));
        var eid = Guid.NewGuid();
        await Json(await phone.PutAsJsonAsync($"/api/hr/holidays/{eid}", new { name = "Eid", firstDay = Iso(today), lastDay = Iso(today) }, Cancel));
        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/hr/holidays/{eid}", Cancel)).StatusCode);
        Assert.Contains("Bar", await (await manager.GetAsync("/api/hr/departments", Cancel)).Content.ReadAsStringAsync(Cancel));

        // An employee's record still waits for a manager; their portal PIN is a manager's alone.
        Assert.Equal(HttpStatusCode.Accepted, (await phone.PostAsJsonAsync($"/api/hr/employees/{mona}/leave", new { requestId = Guid.NewGuid(), lastDay = Iso(today) }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PutAsJsonAsync($"/api/hr/employees/{mona}/pin", new { pin = "1111" }, Cancel)).StatusCode);
        // Not their tabs: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/hr/punches", new
        {
            punchId = Guid.NewGuid(), employeeId = mona, punchedAt = $"{Iso(today)}T10:00:00", reason = "Forgot",
        }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsync($"/api/hr/payroll/{mona}/finalize?month={Iso(new DateOnly(today.Year, today.Month, 1))}", null, Cancel)).StatusCode);

        // An accountant, holding every tab, is as before: none of a manager's.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/hr/departments/{Guid.NewGuid()}", new { name = "Grill" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.PutAsJsonAsync($"/api/hr/holidays/{Guid.NewGuid()}", new { name = "Eid", firstDay = Iso(today), lastDay = Iso(today) }, Cancel)).StatusCode);
    }

    [Fact]
    public async Task Custody_is_a_tab_and_an_employee_holding_it_never_sees_or_loses_what_an_asset_cost()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var hassan = await HireAsync(manager, "Hassan", "01012345678");
        var tableware = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/custody/categories/{tableware}", new { name = "Tableware", isConsumable = true }, Cancel));
        var plates = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/custody/assets/{plates}", new { requestId = Guid.NewGuid(), name = "Dinner plates", categoryId = tableware, unitCost = 35, quantity = 40 }, Cancel));

        // Without the tab: nothing of it.
        await Json(await GiveAsync(manager, hassan, "Now"));
        var (phone, _) = await PortalAsync(api, "01012345678");
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/custody/assets", Cancel)).StatusCode);

        await Json(await GiveAsync(manager, hassan, "Custody"));
        (phone, _) = await PortalAsync(api, "01012345678");
        var listed = await (await phone.GetAsync("/api/custody/assets", Cancel)).Content.ReadAsStringAsync(Cancel);
        var asset = JsonDocument.Parse(listed).RootElement.EnumerateArray().Single();
        Assert.Equal(("Dinner plates", 40), (asset.GetProperty("name").GetString(), asset.GetProperty("quantity").GetInt32()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (asset.GetProperty("unitCost").ValueKind, asset.GetProperty("totalCost").ValueKind));
        Assert.DoesNotContain("1400", listed);
        // The check-in's list of who to hand it to, without what anyone earns.
        var roster = JsonDocument.Parse(await (await phone.GetAsync("/api/hr/employees", Cancel)).Content.ReadAsStringAsync(Cancel)).RootElement.EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, roster.GetProperty("monthlySalary").ValueKind);

        // A category at once; an asset's change asked of a manager, and approved it keeps its cost.
        await Json(await phone.PutAsJsonAsync($"/api/custody/categories/{Guid.NewGuid()}", new { name = "Uniforms", isConsumable = false }, Cancel));
        var renamed = await phone.PutAsJsonAsync($"/api/custody/assets/{plates}", new
        {
            requestId = Guid.NewGuid(), name = "Dinner plates (white)", categoryId = tableware, unitCost = (decimal?)null, quantity = 40, requestReason = "Their colour",
        }, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, renamed.StatusCode);
        var asked = (await Json(renamed)).GetProperty("request").GetProperty("id").GetGuid();
        await Json(await manager.PostAsJsonAsync($"/api/inventory/approval-requests/{asked}/approve", new { }, Cancel));
        var kept = (await Json(await manager.GetAsync($"/api/custody/assets/{plates}", Cancel))).GetProperty("asset");
        Assert.Equal(("Dinner plates (white)", 35m), (kept.GetProperty("name").GetString(), kept.GetProperty("unitCost").GetDecimal()));

        // An accountant has it until a manager ticks their tabs without it.
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/custody/assets", Cancel)).StatusCode);
        await Json(await manager.PutAsJsonAsync($"/api/hr/access/staff/{TestApi.AccountantId}", new { tabs = new[] { "Payroll" } }, Cancel));
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/custody/assets", Cancel)).StatusCode);
    }
}
