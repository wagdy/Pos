using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Performance;
using OtantikPos.Inventory.Application.Hr;
using OtantikPos.Node.Api.Auth;

namespace OtantikPos.Node.Api.Controllers;

// Performance & Disciplinary (تقييم الأداء والإنذارات), on the employee's profile: the KPIs, and
// each employee's evaluations and disciplinary actions with their record. Seen by whoever holds
// the Employees tab; recorded, issued and withdrawn by a manager alone. A penalty deduction's
// penalty goes on Corrections and off the month's pay with it. Employees see their own in the
// portal (PortalController).
[ApiController]
[Route("api/performance")]
[HrTab(HrTabs.Employees)]
[TypeFilter(typeof(HrPrivacyFilter))]
public sealed class PerformanceController(ISender sender) : ControllerBase
{
    // ---- KPIs ----

    [HttpGet("kpis")]
    [HrTab(HrTabs.Employees)]
    public Task<IReadOnlyList<PerformanceKpiDto>> GetKpis(CancellationToken cancellationToken) => sender.Send(new GetPerformanceKpisQuery(), cancellationToken);

    // Added under this id, or renamed.
    [HttpPut("kpis/{kpiId:guid}")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PerformanceKpiDto> SaveKpi(Guid kpiId, KpiRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SavePerformanceKpiCommand(kpiId, request.Name), cancellationToken);

    [HttpPost("kpis/{kpiId:guid}/retire")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PerformanceKpiDto> RetireKpi(Guid kpiId, CancellationToken cancellationToken) =>
        sender.Send(new SetPerformanceKpiActiveCommand(kpiId, false), cancellationToken);

    [HttpPost("kpis/{kpiId:guid}/restore")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<PerformanceKpiDto> RestoreKpi(Guid kpiId, CancellationToken cancellationToken) =>
        sender.Send(new SetPerformanceKpiActiveCommand(kpiId, true), cancellationToken);

    // While no evaluation scored it.
    [HttpDelete("kpis/{kpiId:guid}")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteKpi(Guid kpiId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePerformanceKpiCommand(kpiId), cancellationToken);
        return NoContent();
    }

    // ---- An employee's record ----

    // Their evaluations and disciplinary actions, the latest first, and their record: each level's
    // count, and the level that comes next.
    [HttpGet("employees/{employeeId:guid}")]
    [HrTab(HrTabs.Employees)]
    public Task<EmployeePerformanceDto> GetEmployee(Guid employeeId, CancellationToken cancellationToken) =>
        sender.Send(new GetEmployeePerformanceQuery(employeeId), cancellationToken);

    [HttpPost("employees/{employeeId:guid}/evaluations")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<EvaluationDto> RecordEvaluation(Guid employeeId, EvaluationRequest request, CancellationToken cancellationToken) =>
        sender.Send(new RecordEvaluationCommand(
            request.Id, employeeId, request.Date, request.Scores ?? [], request.Notes, StaffId, StaffName), cancellationToken);

    [HttpPost("employees/{employeeId:guid}/disciplinary")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DisciplinaryActionDto> IssueAction(Guid employeeId, DisciplinaryRequest request, CancellationToken cancellationToken) =>
        sender.Send(new IssueDisciplinaryActionCommand(
            request.Id, employeeId, request.Date, request.ViolationType, request.SeverityLevel, request.Notes, request.PenaltyAmount, StaffId, StaffName),
            cancellationToken);

    // Issued by mistake: withdrawn, with why; its penalty taken off, while its month's pay is open.
    [HttpPost("disciplinary/{actionId:guid}/withdraw")]
    [HrTab(HrTabs.Employees)]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<DisciplinaryActionDto> WithdrawAction(Guid actionId, WithdrawRequest request, CancellationToken cancellationToken) =>
        sender.Send(new WithdrawDisciplinaryActionCommand(actionId, request.Reason, StaffId, StaffName), cancellationToken);

    private string StaffId => StaffClaims.IdOf(User) ?? string.Empty;

    private string StaffName => User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty;
}

public sealed record KpiRequest(string Name);

// Id made by the till: a retry finds the one made.
public sealed record EvaluationRequest(Guid Id, DateOnly Date, IReadOnlyList<KpiScoreInput>? Scores, string? Notes = null);

public sealed record DisciplinaryRequest(
    Guid Id, DateOnly Date, string ViolationType, SeverityLevel SeverityLevel, string? Notes = null, decimal? PenaltyAmount = null);

public sealed record WithdrawRequest(string Reason);
