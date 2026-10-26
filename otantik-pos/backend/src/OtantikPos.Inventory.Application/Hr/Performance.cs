using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Hr.Domain.Performance;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// Performance & Disciplinary (تقييم الأداء والإنذارات): a manager's KPIs, the evaluations scored on
// them, and disciplinary actions, a penalty deduction among them reaching the pay as an
// administrative penalty in money on Corrections. An action issued by mistake is withdrawn while
// its month's pay is open, its penalty taken off with it. All of it a manager's; employees see
// their own in the portal, withdrawn ones left out.

public sealed record PerformanceKpiDto(Guid Id, string Name, bool IsActive, bool InUse);

public sealed record KpiScoreDto(Guid KpiId, string KpiName, int Score);

public sealed record EvaluationDto(
    Guid Id, Guid EmployeeId, DateOnly Date, IReadOnlyList<KpiScoreDto> Scores, decimal TotalScore, string? Notes, string ReviewerName, DateTime RecordedAtUtc);

// PenaltyPending: a penalty deduction standing, its month's pay not finalized yet.
public sealed record DisciplinaryActionDto(
    Guid Id, Guid EmployeeId, DateOnly Date, string ViolationType, SeverityLevel SeverityLevel, string? Notes, decimal? PenaltyAmount, string IssuerName,
    DateTime IssuedAtUtc, bool IsWithdrawn, string? WithdrawnBy, DateTime? WithdrawnAtUtc, string? WithdrawalReason, bool PenaltyPending);

public sealed record DisciplinaryRecordDto(
    int VerbalWarnings, int FirstWritten, int SecondWritten, int PenaltyDeductions, int Terminations, int Warnings, SeverityLevel Next);

public sealed record EmployeePerformanceDto(
    Guid EmployeeId, string EmployeeName, IReadOnlyList<EvaluationDto> Evaluations, IReadOnlyList<DisciplinaryActionDto> Actions, DisciplinaryRecordDto Record);

public sealed record KpiScoreInput(Guid KpiId, int Score);

// ---- KPIs: a manager's ----

public sealed record GetPerformanceKpisQuery : IRequest<IReadOnlyList<PerformanceKpiDto>>;

internal sealed class GetPerformanceKpisHandler(IPerformanceKpiRepository kpis, IPerformanceEvaluationRepository evaluations)
    : IRequestHandler<GetPerformanceKpisQuery, IReadOnlyList<PerformanceKpiDto>>
{
    public async Task<IReadOnlyList<PerformanceKpiDto>> Handle(GetPerformanceKpisQuery request, CancellationToken cancellationToken)
    {
        var list = new List<PerformanceKpiDto>();
        foreach (var k in await kpis.GetAllAsync(cancellationToken))
            list.Add(new PerformanceKpiDto(k.Id, k.Name, k.IsActive, await evaluations.AnyForKpiAsync(k.Id, cancellationToken)));
        return list;
    }
}

// Added under this id, or renamed.
public sealed record SavePerformanceKpiCommand(Guid Id, string Name) : IRequest<PerformanceKpiDto>;

internal sealed class SavePerformanceKpiHandler(IPerformanceKpiRepository kpis, IPerformanceEvaluationRepository evaluations, IUnitOfWork unitOfWork)
    : IRequestHandler<SavePerformanceKpiCommand, PerformanceKpiDto>
{
    public async Task<PerformanceKpiDto> Handle(SavePerformanceKpiCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if ((await kpis.GetAllAsync(cancellationToken)).FirstOrDefault(k => k.Id != request.Id && string.Equals(k.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } taken)
            throw new DomainException($"A KPI is called {taken.Name} already.");
        var kpi = await kpis.GetByIdAsync(request.Id, cancellationToken);
        if (kpi is null)
        {
            kpi = new PerformanceKpi(request.Id, name);
            kpis.Add(kpi);
        }
        else
        {
            kpi.Rename(name);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PerformanceKpiDto(kpi.Id, kpi.Name, kpi.IsActive, await evaluations.AnyForKpiAsync(kpi.Id, cancellationToken));
    }
}

// Retired (not offered for new evaluations) or brought back.
public sealed record SetPerformanceKpiActiveCommand(Guid Id, bool IsActive) : IRequest<PerformanceKpiDto>;

internal sealed class SetPerformanceKpiActiveHandler(IPerformanceKpiRepository kpis, IPerformanceEvaluationRepository evaluations, IUnitOfWork unitOfWork)
    : IRequestHandler<SetPerformanceKpiActiveCommand, PerformanceKpiDto>
{
    public async Task<PerformanceKpiDto> Handle(SetPerformanceKpiActiveCommand request, CancellationToken cancellationToken)
    {
        var kpi = await kpis.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(PerformanceKpi), request.Id);
        if (request.IsActive)
            kpi.Restore();
        else
            kpi.Retire();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PerformanceKpiDto(kpi.Id, kpi.Name, kpi.IsActive, await evaluations.AnyForKpiAsync(kpi.Id, cancellationToken));
    }
}

// Deleted while no evaluation scored it; one that did is retired instead.
public sealed record DeletePerformanceKpiCommand(Guid Id) : IRequest;

internal sealed class DeletePerformanceKpiHandler(IPerformanceKpiRepository kpis, IPerformanceEvaluationRepository evaluations, IUnitOfWork unitOfWork)
    : IRequestHandler<DeletePerformanceKpiCommand>
{
    public async Task Handle(DeletePerformanceKpiCommand request, CancellationToken cancellationToken)
    {
        var kpi = await kpis.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(PerformanceKpi), request.Id);
        if (await evaluations.AnyForKpiAsync(kpi.Id, cancellationToken))
            throw new DomainException($"Evaluations have scored {kpi.Name}: retire it instead, and they keep it.");
        kpis.Remove(kpi);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// ---- An employee's record ----

// Theirs: evaluations, actions and the record, the latest first. ForEmployee: the portal's, with
// what was withdrawn left out.
public sealed record GetEmployeePerformanceQuery(Guid EmployeeId, bool ForEmployee = false) : IRequest<EmployeePerformanceDto>;

internal sealed class GetEmployeePerformanceHandler(
    IEmployeeRepository employees, IPerformanceEvaluationRepository evaluations, IDisciplinaryActionRepository actions, IPayrollSlipRepository slips)
    : IRequestHandler<GetEmployeePerformanceQuery, EmployeePerformanceDto>
{
    public async Task<EmployeePerformanceDto> Handle(GetEmployeePerformanceQuery request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        var theirs = (await actions.GetForEmployeeAsync(employee.Id, cancellationToken)).Where(a => !request.ForEmployee || !a.IsWithdrawn).ToList();
        var list = new List<DisciplinaryActionDto>();
        foreach (var a in theirs)
            list.Add(await PerformanceDtos.OfAsync(a, slips, cancellationToken));
        var record = DisciplinaryRecord.Of(theirs);
        return new EmployeePerformanceDto(
            employee.Id, employee.Name,
            (await evaluations.GetForEmployeeAsync(employee.Id, cancellationToken)).Select(PerformanceDtos.Of).ToList(),
            list,
            new DisciplinaryRecordDto(record.VerbalWarnings, record.FirstWritten, record.SecondWritten, record.PenaltyDeductions, record.Terminations, record.Warnings, record.Next));
    }
}

// A manager's evaluation on a day up to today, while they were employed. Id made by the till: a
// retry finds the one made.
public sealed record RecordEvaluationCommand(
    Guid Id, Guid EmployeeId, DateOnly Date, IReadOnlyList<KpiScoreInput> Scores, string? Notes, string StaffId, string StaffName)
    : IRequest<EvaluationDto>, IRetryOnConflict;

internal sealed class RecordEvaluationHandler(
    IEmployeeRepository employees, IPerformanceKpiRepository kpis, IPerformanceEvaluationRepository evaluations, IRecordDescriptions clock, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordEvaluationCommand, EvaluationDto>
{
    public async Task<EvaluationDto> Handle(RecordEvaluationCommand request, CancellationToken cancellationToken)
    {
        if (await evaluations.GetByIdAsync(request.Id, cancellationToken) is { } made)
            return PerformanceDtos.Of(made);
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        Performances.EnsureDay(employee, request.Date, clock);
        var all = (await kpis.GetAllAsync(cancellationToken)).ToDictionary(k => k.Id);
        var scores = (request.Scores ?? []).Select(s => (all.GetValueOrDefault(s.KpiId) ?? throw new NotFoundException(nameof(PerformanceKpi), s.KpiId), s.Score));
        var evaluation = new PerformanceEvaluation(request.Id, employee.Id, request.Date, scores, request.Notes, request.StaffId, request.StaffName);
        evaluations.Add(evaluation);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PerformanceDtos.Of(evaluation);
    }
}

// A manager's disciplinary action on a day up to today, while they were employed; a penalty
// deduction's penalty made with it, in a month whose pay is open.
public sealed record IssueDisciplinaryActionCommand(
    Guid Id, Guid EmployeeId, DateOnly Date, string ViolationType, SeverityLevel SeverityLevel, string? Notes, decimal? PenaltyAmount, string StaffId,
    string StaffName) : IRequest<DisciplinaryActionDto>, IRetryOnConflict;

internal sealed class IssueDisciplinaryActionHandler(
    IEmployeeRepository employees, IDisciplinaryActionRepository actions, IAdministrativePenaltyRepository penalties, IPayrollSlipRepository slips,
    IRecordDescriptions clock, IUnitOfWork unitOfWork)
    : IRequestHandler<IssueDisciplinaryActionCommand, DisciplinaryActionDto>
{
    public async Task<DisciplinaryActionDto> Handle(IssueDisciplinaryActionCommand request, CancellationToken cancellationToken)
    {
        if (await actions.GetByIdAsync(request.Id, cancellationToken) is { } made)
            return await PerformanceDtos.OfAsync(made, slips, cancellationToken);
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        Performances.EnsureDay(employee, request.Date, clock);
        var action = new DisciplinaryAction(
            request.Id, employee.Id, request.Date, request.ViolationType, request.SeverityLevel, request.Notes, request.PenaltyAmount, request.StaffId,
            request.StaffName);
        if (action.Penalty() is { } penalty)
        {
            await slips.EnsureOpenAsync(employee, action.Date, cancellationToken);
            penalties.Add(penalty);
        }

        actions.Add(action);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await PerformanceDtos.OfAsync(action, slips, cancellationToken);
    }
}

// A manager's: one issued by mistake withdrawn, with why; its penalty taken off Corrections and
// the pay, while its month's pay is open.
public sealed record WithdrawDisciplinaryActionCommand(Guid Id, string Reason, string StaffId, string StaffName)
    : IRequest<DisciplinaryActionDto>, IRetryOnConflict;

internal sealed class WithdrawDisciplinaryActionHandler(
    IEmployeeRepository employees, IDisciplinaryActionRepository actions, IAdministrativePenaltyRepository penalties, IPayrollSlipRepository slips,
    IUnitOfWork unitOfWork)
    : IRequestHandler<WithdrawDisciplinaryActionCommand, DisciplinaryActionDto>
{
    public async Task<DisciplinaryActionDto> Handle(WithdrawDisciplinaryActionCommand request, CancellationToken cancellationToken)
    {
        var action = await actions.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(DisciplinaryAction), request.Id);
        var employee = await employees.GetRequiredAsync(action.EmployeeId, cancellationToken);
        if (!action.IsWithdrawn && await penalties.GetForDisciplinaryActionAsync(action.Id, cancellationToken) is { } penalty)
        {
            if (await slips.GetAsync(employee.Id, PayrollMonth.Of(action.Date), cancellationToken) is { IsFinalized: true })
                throw new DomainException($"{employee.Name}'s pay for {action.Date:MMMM yyyy} is finalized with its deduction: it is not withdrawn after it.");
            penalties.Remove(penalty);
        }

        action.Withdraw(request.StaffId, request.StaffName, request.Reason);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await PerformanceDtos.OfAsync(action, slips, cancellationToken);
    }
}

internal static class Performances
{
    // On a day they were employed, and not after the business day.
    public static void EnsureDay(OtantikPos.Hr.Domain.Employees.Employee employee, DateOnly date, IRecordDescriptions clock)
    {
        if (date > clock.BusinessDateOf(DateTime.UtcNow))
            throw new DomainException("It is dated today or before.");
        if (!employee.IsEmployedOn(date))
            throw new DomainException($"{employee.Name} is not employed on {date:d MMM yyyy}.");
    }
}

internal static class PerformanceDtos
{
    public static EvaluationDto Of(PerformanceEvaluation e) =>
        new(e.Id, e.EmployeeId, e.Date, e.Scores.Select(s => new KpiScoreDto(s.KpiId, s.KpiName, s.Score)).ToList(), e.TotalScore, e.Notes, e.ReviewerName, e.RecordedAtUtc);

    public static async Task<DisciplinaryActionDto> OfAsync(DisciplinaryAction a, IPayrollSlipRepository slips, CancellationToken cancellationToken)
    {
        var pending = a.SeverityLevel == SeverityLevel.PenaltyDeduction && !a.IsWithdrawn
            && await slips.GetAsync(a.EmployeeId, PayrollMonth.Of(a.Date), cancellationToken) is not { IsFinalized: true };
        return new DisciplinaryActionDto(
            a.Id, a.EmployeeId, a.Date, a.ViolationType, a.SeverityLevel, a.Notes, a.PenaltyAmount, a.IssuerName, a.IssuedAtUtc, a.IsWithdrawn, a.WithdrawnBy,
            a.WithdrawnAtUtc, a.WithdrawalReason, pending);
    }
}
