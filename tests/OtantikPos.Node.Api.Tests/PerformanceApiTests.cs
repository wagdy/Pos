using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OtantikPos.Node.Api.Tests;

// Performance & Disciplinary through the API: the KPIs, evaluations scored on them, disciplinary
// actions escalating, a penalty deduction on Corrections and off the pay, withdrawn by a manager
// with it, and the employee's own history in the portal.
public class PerformanceApiTests
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

    private static async Task<Guid> HireAsync(HttpClient manager, string name, DateOnly hiredOn, string? mobile = null)
    {
        var id = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}", new
        {
            requestId = Guid.NewGuid(), name, jobTitle = "Chef", monthlySalary = 5_400, shiftStartsAt = "10:00:00", daysOff = Array.Empty<string>(),
            hiredOn = Iso(hiredOn), mobileNumber = mobile,
        }, Cancel));
        if (mobile is not null)
            await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}/pin", new { pin = "2468" }, Cancel));
        return id;
    }

    private static Task<HttpResponseMessage> IssueAsync(HttpClient client, Guid employee, DateOnly date, string severity, decimal? amount = null, string violation = "Negligence") =>
        client.PostAsJsonAsync($"/api/performance/employees/{employee}/disciplinary", new
        {
            id = Guid.NewGuid(), date = Iso(date), violationType = violation, severityLevel = severity, notes = "Left the walk-in open", penaltyAmount = amount,
        }, Cancel);

    [Fact]
    public async Task Evaluations_score_a_managers_kpis_and_warnings_escalate_to_a_penalty_withdrawn_with_it()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var today = await TodayAsync(manager);
        var month = new DateOnly(today.Year, today.Month, 1);
        var hassan = await HireAsync(manager, "Hassan", month.AddMonths(-2), "01012345678");

        // The KPIs: a manager's.
        var punctuality = Guid.NewGuid();
        var hygiene = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/performance/kpis/{punctuality}", new { name = "Punctuality" }, Cancel)).StatusCode);
        await Json(await manager.PutAsJsonAsync($"/api/performance/kpis/{punctuality}", new { name = "Punctuality" }, Cancel));
        await Json(await manager.PutAsJsonAsync($"/api/performance/kpis/{hygiene}", new { name = "Hygiene" }, Cancel));
        Assert.Contains("is called Hygiene already", await Problem(await manager.PutAsJsonAsync($"/api/performance/kpis/{Guid.NewGuid()}", new { name = " hygiene " }, Cancel)));
        Assert.Equal(["Hygiene", "Punctuality"], (await Json(await accountant.GetAsync("/api/performance/kpis", Cancel))).EnumerateArray().Select(k => k.GetProperty("name").GetString()));

        // An evaluation: 5 and 3, 80 percent.
        object Evaluation(int a, int b, DateOnly? date = null) => new
        {
            id = Guid.NewGuid(), date = Iso(date ?? today), scores = new[] { new { kpiId = punctuality, score = a }, new { kpiId = hygiene, score = b } }, notes = "Good month",
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/performance/employees/{hassan}/evaluations", Evaluation(5, 3), Cancel)).StatusCode);
        var evaluated = await Json(await manager.PostAsJsonAsync($"/api/performance/employees/{hassan}/evaluations", Evaluation(5, 3), Cancel));
        Assert.Equal((80.0m, "Boss"), (evaluated.GetProperty("totalScore").GetDecimal(), evaluated.GetProperty("reviewerName").GetString()));
        Assert.Contains("scored 1 to 5", await Problem(await manager.PostAsJsonAsync($"/api/performance/employees/{hassan}/evaluations", Evaluation(6, 3), Cancel)));
        Assert.Contains("today or before", await Problem(await manager.PostAsJsonAsync($"/api/performance/employees/{hassan}/evaluations", Evaluation(4, 4, today.AddDays(1)), Cancel)));

        // A scored KPI is retired, not deleted, and not scored again.
        Assert.Contains("retire it instead", await Problem(await manager.DeleteAsync($"/api/performance/kpis/{hygiene}", Cancel)));
        Assert.False((await Json(await manager.PostAsync($"/api/performance/kpis/{hygiene}/retire", null, Cancel))).GetProperty("isActive").GetBoolean());
        Assert.Contains("retired", await Problem(await manager.PostAsJsonAsync($"/api/performance/employees/{hassan}/evaluations", Evaluation(4, 4), Cancel)));
        await Json(await manager.PostAsync($"/api/performance/kpis/{hygiene}/restore", null, Cancel));

        // Warnings escalate: the record says what comes next.
        async Task<JsonElement> Record() => await Json(await accountant.GetAsync($"/api/performance/employees/{hassan}", Cancel));
        Assert.Equal("VerbalWarning", (await Record()).GetProperty("record").GetProperty("next").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await IssueAsync(accountant, hassan, today, "VerbalWarning")).StatusCode);
        await Json(await IssueAsync(manager, hassan, today, "VerbalWarning"));
        await Json(await IssueAsync(manager, hassan, today, "FirstWritten"));
        var record = (await Record()).GetProperty("record");
        Assert.Equal((2, "SecondWritten"), (record.GetProperty("warnings").GetInt32(), record.GetProperty("next").GetString()));
        Assert.Contains("Only a penalty deduction has an amount", await Problem(await IssueAsync(manager, hassan, today, "SecondWritten", 100)));
        Assert.Contains("has its amount", await Problem(await IssueAsync(manager, hassan, today, "PenaltyDeduction")));

        // A penalty deduction: on Corrections with its action, and off this month's pay.
        var deduction = await Json(await IssueAsync(manager, hassan, today, "PenaltyDeduction", 250, "Absence"));
        Assert.True(deduction.GetProperty("penaltyPending").GetBoolean());
        var deductionId = deduction.GetProperty("id").GetGuid();
        async Task<JsonElement> Figures() => (await Json(await accountant.GetAsync($"/api/hr/payroll?month={Iso(month)}", Cancel))).GetProperty("slips")[0].GetProperty("figures");
        Assert.Equal((250m, 250m), ((await Figures()).GetProperty("administrativePenaltyAmounts").GetDecimal(), (await Figures()).GetProperty("administrativePenalty").GetDecimal()));
        var listed = (await Json(await accountant.GetAsync($"/api/hr/penalties?month={Iso(month)}", Cancel))).EnumerateArray().Single();
        Assert.Equal((250m, deductionId), (listed.GetProperty("amount").GetDecimal(), listed.GetProperty("disciplinaryActionId").GetGuid()));

        // Withdrawn by a manager, with why: off Corrections and the pay, counted for nothing, kept.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/performance/disciplinary/{deductionId}/withdraw", new { reason = "Wrong person" }, Cancel)).StatusCode);
        Assert.Contains("Say why", await Problem(await manager.PostAsJsonAsync($"/api/performance/disciplinary/{deductionId}/withdraw", new { reason = " " }, Cancel)));
        var withdrawn = await Json(await manager.PostAsJsonAsync($"/api/performance/disciplinary/{deductionId}/withdraw", new { reason = "Wrong person" }, Cancel));
        Assert.Equal((true, "Boss", "Wrong person", false), (withdrawn.GetProperty("isWithdrawn").GetBoolean(), withdrawn.GetProperty("withdrawnBy").GetString(),
            withdrawn.GetProperty("withdrawalReason").GetString(), withdrawn.GetProperty("penaltyPending").GetBoolean()));
        Assert.Equal(0m, (await Figures()).GetProperty("administrativePenalty").GetDecimal());
        Assert.Empty((await Json(await accountant.GetAsync($"/api/hr/penalties?month={Iso(month)}", Cancel))).EnumerateArray());
        var after = await Record();
        Assert.Equal((3, 0, "SecondWritten"), (after.GetProperty("actions").GetArrayLength(), after.GetProperty("record").GetProperty("penaltyDeductions").GetInt32(),
            after.GetProperty("record").GetProperty("next").GetString()));
        Assert.Contains("withdrawn already", await Problem(await manager.PostAsJsonAsync($"/api/performance/disciplinary/{deductionId}/withdraw", new { reason = "Again" }, Cancel)));

        // Their own in the portal, read-only: what was withdrawn left out.
        var phone = api.Anonymous();
        var signedIn = await Json(await api.Anonymous().PostAsJsonAsync("/api/auth/employee-login", new { mobileNumber = "01012345678", pinCode = "2468" }, Cancel));
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.GetProperty("token").GetString());
        var own = await Json(await phone.GetAsync("/api/portal/performance", Cancel));
        Assert.Equal((1, 2), (own.GetProperty("evaluations").GetArrayLength(), own.GetProperty("actions").GetArrayLength()));
        Assert.DoesNotContain("Wrong person", own.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync($"/api/performance/employees/{hassan}", Cancel)).StatusCode);
    }

    [Fact]
    public async Task A_penalty_in_a_finalized_month_is_neither_issued_nor_withdrawn()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var today = await TodayAsync(manager);
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastDay = lastMonth.AddMonths(1).AddDays(-1);
        // Started on last month's last day, which was leave: a month to finalize.
        var hassan = await HireAsync(manager, "Hassan", lastDay);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/days/{Iso(lastDay)}", new { kind = "Leave" }, Cancel)).StatusCode);
        var warning = await Json(await IssueAsync(manager, hassan, lastDay, "VerbalWarning"));
        await Json(await manager.PostAsync($"/api/hr/payroll/calculate?month={Iso(lastMonth)}", null, Cancel));
        await Json(await manager.PostAsync($"/api/hr/payroll/{hassan}/finalize?month={Iso(lastMonth)}", null, Cancel));

        Assert.Contains("is finalized", await Problem(await IssueAsync(manager, hassan, lastDay, "PenaltyDeduction", 100)));
        // A warning with no penalty is still withdrawn.
        Assert.True((await Json(await manager.PostAsJsonAsync($"/api/performance/disciplinary/{warning.GetProperty("id").GetGuid()}/withdraw", new { reason = "Wrong day" }, Cancel)))
            .GetProperty("isWithdrawn").GetBoolean());
    }
}
