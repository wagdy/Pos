using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Inventory.Application.Hr;

// The monthly payroll (مسير الرواتب): each employee's slip worked out from their salary, their
// days, the month's incentives and leave allowances, the advances the treasury paid them against
// it, and what last month's slip carried over. A draft is worked out afresh whenever it is read;
// calculating saves the drafts; finalizing one fixes it, with the days it was worked out from.
// What an employee is paid of it is a safe entry (SalaryPayment).
//
// Incentives, allowances and finalizing are a manager's; the API keeps them to managers.

public sealed record PayrollAdjustmentDto(
    Guid Id, Guid EmployeeId, DateOnly Month, AdjustmentKind Kind, decimal Amount, string? Reason, AdjustmentSource Source, DateOnly? ForDay,
    string RecordedBy, DateTime RecordedAtUtc, DateTime? VoidedAtUtc, string? VoidedBy)
{
    public static PayrollAdjustmentDto From(PayrollAdjustment a) => new(
        a.Id, a.EmployeeId, a.Month, a.Kind, a.Amount, a.Reason, a.Source, a.ForDay, a.RecordedBy, a.RecordedAtUtc, a.VoidedAtUtc, a.VoidedBy);
}

// An advance or salary payment counted in the month. A void is shown on what it voided, not apart.
// OutsideTill: an advance paid outside the till, recorded in HR only (or a share of one).
public sealed record PayrollCashDto(
    Guid Id, SafeTransactionKind Kind, DateOnly Date, decimal Amount, DateOnly? FromTakingsOf, string? Notes, string RecordedBy, string? ApprovedBy,
    DateTime? VoidedAtUtc, string? VoidedBy, bool OutsideTill = false);

public sealed record PayrollSlipDto(
    EmployeeDto Employee,
    DateOnly Month,
    // Null for a draft not yet saved: worked out now, as it would be.
    PayrollSlipStatus? Status,
    PayrollFigures Figures,
    decimal SalaryPaid,
    decimal SalaryOwed,
    DateTime? CalculatedAtUtc,
    DateTime? FinalizedAtUtc,
    string? FinalizedBy,
    IReadOnlyList<PayrollAdjustmentDto> Adjustments,
    IReadOnlyList<PayrollCashDto> Advances,
    IReadOnlyList<PayrollCashDto> Salaries);

public sealed record PayrollTotals(
    decimal BaseSalaries, decimal Incentives, decimal LeaveAllowances, decimal OvertimePay, decimal Advances, decimal BroughtForward,
    decimal LatenessPenalties, decimal AbsencePenalties, decimal NetPayable, decimal CarriedForward, decimal WagesCost, decimal SalaryPaid,
    decimal DaysOffWorkedPay, decimal HolidaysWorkedPay);

public sealed record PayrollMonthDto(DateOnly Month, PayrollTotals Totals, IReadOnlyList<PayrollSlipDto> Slips);

// Shares: their shares of advances handed out with no one named; Outside: advances paid to them
// outside the till, recorded in HR only; both deducted as their own advances from the till are.
public sealed record WorkedOut(
    Employee Employee, PayrollFigures Figures, PayrollSlip? Slip, IReadOnlyList<PayrollAdjustment> Adjustments, IReadOnlyList<SafeTransaction> Cash,
    EmployeeSheet Sheet, IReadOnlyList<AdvanceAllocation> Shares, IReadOnlyList<HrAdvance> Outside);

internal sealed class PayrollWorker(
    IEmployeeRepository employees,
    Timesheets timesheets,
    IPayrollAdjustmentRepository adjustments,
    IPayrollSlipRepository slips,
    ISafeTransactionRepository safe,
    IAdvanceAllocationRepository allocations,
    IHrAdvanceRepository hrAdvances)
{
    // Everyone employed in the month, or one employee, as their pay stands now: a finalized slip's
    // figures as they were finalized, a draft's worked out afresh.
    public async Task<IReadOnlyList<WorkedOut>> WorkOutAsync(DateOnly month, Guid? employeeId, CancellationToken cancellationToken)
    {
        PayrollMonth.Required(month);
        var last = month.AddMonths(1).AddDays(-1);
        IReadOnlyList<Employee> everyone = employeeId is { } id
            ? [await employees.GetRequiredAsync(id, cancellationToken)]
            : await employees.GetAllAsync(cancellationToken);
        var people = everyone.Where(e => e.HiredOn <= last && (e.LeftOn is null || e.LeftOn >= month)).ToList();
        if (people.Count == 0)
            return [];

        var (_, sheets) = await timesheets.ReadAsync(people, month, last, cancellationToken);
        var monthAdjustments = await adjustments.GetForMonthAsync(month, cancellationToken);
        var cash = await safe.GetForPayrollMonthAsync(month, employeeId, cancellationToken);
        var shares = (await allocations.GetForPayrollMonthAsync(month, employeeId, cancellationToken)).Where(a => !a.IsVoided).ToList();
        var outside = (await hrAdvances.GetForPayrollMonthAsync(month, employeeId, cancellationToken)).Where(a => !a.IsVoided).ToList();
        var saved = (await slips.GetForMonthAsync(month, cancellationToken)).ToDictionary(s => s.EmployeeId);
        var before = (await slips.GetForMonthAsync(month.AddMonths(-1), cancellationToken)).ToDictionary(s => s.EmployeeId);

        return sheets.Select(sheet =>
        {
            var e = sheet.Employee;
            var theirs = monthAdjustments.Where(a => a.EmployeeId == e.Id).ToList();
            var theirCash = cash.Where(t => t.EmployeeId == e.Id).ToList();
            var theirShares = shares.Where(a => a.EmployeeId == e.Id).ToList();
            var theirOutside = outside.Where(a => a.EmployeeId == e.Id).ToList();
            var slip = saved.GetValueOrDefault(e.Id);
            var figures = slip is { IsFinalized: true }
                ? slip.Figures
                : PayrollCalculator.Calculate(
                    e, month, sheet.Days.Select(d => d.Evaluation), theirs,
                    Math.Max(0, -theirCash.Where(t => t.Kind == SafeTransactionKind.EmployeeAdvance).Sum(t => t.Amount)) + theirShares.Sum(a => a.Amount)
                        + theirOutside.Sum(a => a.Amount),
                    before.GetValueOrDefault(e.Id) is { IsFinalized: true } previous ? previous.CarriedForward : 0);
            return new WorkedOut(e, figures, slip, theirs, theirCash, sheet, theirShares, theirOutside);
        }).ToList();
    }

    public static PayrollMonthDto Dto(DateOnly month, IReadOnlyList<WorkedOut> worked)
    {
        var slipDtos = worked.Select(SlipDto).ToList();
        decimal Sum(Func<PayrollFigures, decimal> of) => worked.Sum(w => of(w.Figures));
        return new PayrollMonthDto(
            month,
            new PayrollTotals(
                Sum(f => f.BaseSalary), Sum(f => f.Incentives), Sum(f => f.LeaveAllowances), Sum(f => f.OvertimePay), Sum(f => f.Advances),
                Sum(f => f.BroughtForward), Sum(f => f.LatenessPenalty), Sum(f => f.AbsencePenalty), Sum(f => f.NetPayable), Sum(f => f.CarriedForward),
                Sum(f => f.WagesCost), slipDtos.Sum(s => s.SalaryPaid), Sum(f => f.DaysOffWorkedPay), Sum(f => f.HolidaysWorkedPay)),
            slipDtos);
    }

    public static PayrollSlipDto SlipDto(WorkedOut w)
    {
        // Each entry once, marked when it is voided.
        PayrollCashDto Cash(SafeTransaction t) => new(
            t.Id, t.Kind, t.BusinessDate, -t.Amount, t.RevenueDate, t.Notes, t.RecordedBy, t.ApprovedBy, t.VoidedAtUtc, t.VoidedBy);
        var entries = w.Cash.Where(t => t.ReversalOf is null).ToList();
        var paid = Math.Max(0, -w.Cash.Where(t => t.Kind == SafeTransactionKind.SalaryPayment).Sum(t => t.Amount));
        return new PayrollSlipDto(
            EmployeeDto.From(w.Employee), PayrollMonth.Of(w.Sheet.Days[0].Evaluation.Day), w.Slip?.Status, w.Figures, paid,
            w.Slip is { IsFinalized: true } ? Math.Max(0, w.Figures.NetPayable - paid) : 0, w.Slip?.CalculatedAtUtc, w.Slip?.FinalizedAtUtc,
            w.Slip?.FinalizedBy, w.Adjustments.OrderBy(a => a.RecordedAtUtc).Select(PayrollAdjustmentDto.From).ToList(),
            entries.Where(t => t.Kind == SafeTransactionKind.EmployeeAdvance).Select(Cash)
                .Concat(w.Shares.Select(a => new PayrollCashDto(
                    a.Id, SafeTransactionKind.EmployeeAdvance, a.Date, a.Amount, null,
                    $"Share of the advances handed out on {a.Date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}" +
                    (a.Notes is { } notes ? $": {notes}" : ""), a.AllocatedBy, null, null, null, a.OutsideTill)))
                .Concat(w.Outside.Select(a => new PayrollCashDto(
                    a.Id, SafeTransactionKind.EmployeeAdvance, a.Date, a.Amount, null, a.Notes, a.RecordedBy, a.ApprovedBy, null, null, true)))
                .OrderBy(c => c.Date).ToList(),
            entries.Where(t => t.Kind == SafeTransactionKind.SalaryPayment).Select(Cash).ToList());
    }
}

public sealed record GetPayrollQuery(DateOnly Month) : IRequest<PayrollMonthDto>;

internal sealed class GetPayrollHandler(PayrollWorker payroll) : IRequestHandler<GetPayrollQuery, PayrollMonthDto>
{
    public async Task<PayrollMonthDto> Handle(GetPayrollQuery request, CancellationToken cancellationToken) =>
        PayrollWorker.Dto(request.Month, await payroll.WorkOutAsync(request.Month, null, cancellationToken));
}

// The month's drafts saved as they stand: every employee's slip that is not finalized.
public sealed record CalculatePayrollCommand(DateOnly Month) : IRequest<PayrollMonthDto>, IRetryOnConflict;

internal sealed class CalculatePayrollHandler(PayrollWorker payroll, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<CalculatePayrollCommand, PayrollMonthDto>
{
    public async Task<PayrollMonthDto> Handle(CalculatePayrollCommand request, CancellationToken cancellationToken)
    {
        var worked = await payroll.WorkOutAsync(request.Month, null, cancellationToken);
        var saved = worked.Select(w =>
        {
            if (w.Slip is { IsFinalized: true })
                return w;
            var slip = w.Slip;
            if (slip is null)
            {
                slip = new PayrollSlip(Guid.NewGuid(), w.Employee.Id, request.Month, w.Figures);
                slips.Add(slip);
            }
            else
            {
                slip.Recalculate(w.Figures);
            }

            return w with { Slip = slip };
        }).ToList();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PayrollWorker.Dto(request.Month, saved);
    }
}

// One employee's slip finalized: worked out once more, and fixed, with the days it was worked out
// from kept as they were.
public sealed record FinalizePayrollSlipCommand(DateOnly Month, Guid EmployeeId, string StaffName) : IRequest<PayrollSlipDto>, IRetryOnConflict;

internal sealed class FinalizePayrollSlipHandler(
    PayrollWorker payroll, IPayrollSlipRepository slips, IDailyRecordRepository records, IUnitOfWork unitOfWork)
    : IRequestHandler<FinalizePayrollSlipCommand, PayrollSlipDto>
{
    public async Task<PayrollSlipDto> Handle(FinalizePayrollSlipCommand request, CancellationToken cancellationToken)
    {
        var worked = (await payroll.WorkOutAsync(request.Month, request.EmployeeId, cancellationToken)).SingleOrDefault()
            ?? throw new DomainException("They were not employed that month.");
        if (worked.Slip is { IsFinalized: true })
            return PayrollWorker.SlipDto(worked);

        var slip = worked.Slip;
        if (slip is null)
        {
            slip = new PayrollSlip(Guid.NewGuid(), worked.Employee.Id, request.Month, worked.Figures);
            slips.Add(slip);
        }
        else
        {
            slip.Recalculate(worked.Figures);
        }

        slip.Finalize(request.StaffName);

        var days = worked.Sheet.Days.Select(d => d.Evaluation).Where(d => d.Status != DayStatus.NotEmployed).ToList();
        var kept = (await records.GetForEmployeeAsync(worked.Employee.Id, days.FirstOrDefault()?.Day ?? request.Month, request.Month.AddMonths(1).AddDays(-1), cancellationToken))
            .ToDictionary(r => r.Date);
        foreach (var day in days)
        {
            if (kept.GetValueOrDefault(day.Day) is { } record)
                record.Update(day);
            else
                records.Add(new DailyRecord(worked.Employee.Id, day));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PayrollWorker.SlipDto(worked with { Slip = slip });
    }
}

// An incentive or leave allowance for a month, while its slip is not finalized. AdjustmentId is
// made by the till.
public sealed record AddPayrollAdjustmentCommand(
    Guid AdjustmentId, Guid EmployeeId, DateOnly Month, AdjustmentKind Kind, decimal Amount, string? Reason, DateOnly? ForDay, string StaffName)
    : IRequest<PayrollAdjustmentDto>, IRetryOnConflict;

internal sealed class AddPayrollAdjustmentHandler(
    IEmployeeRepository employees, IPayrollAdjustmentRepository adjustments, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<AddPayrollAdjustmentCommand, PayrollAdjustmentDto>
{
    public async Task<PayrollAdjustmentDto> Handle(AddPayrollAdjustmentCommand request, CancellationToken cancellationToken)
    {
        if (await adjustments.GetByIdAsync(request.AdjustmentId, cancellationToken) is { } made)
            return PayrollAdjustmentDto.From(made);
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        var month = PayrollMonth.Required(request.Month);
        if (employee.HiredOn > month.AddMonths(1).AddDays(-1) || employee.LeftOn < month)
            throw new DomainException($"{employee.Name} was not employed in {month:MMMM yyyy}.");
        await slips.EnsureOpenAsync(employee, month, cancellationToken);
        var adjustment = new PayrollAdjustment(
            request.AdjustmentId, employee.Id, month, request.Kind, request.Amount, request.Reason, request.StaffName, forDay: request.ForDay);
        adjustments.Add(adjustment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PayrollAdjustmentDto.From(adjustment);
    }
}

public sealed record VoidPayrollAdjustmentCommand(Guid AdjustmentId, string StaffName) : IRequest<PayrollAdjustmentDto>;

internal sealed class VoidPayrollAdjustmentHandler(
    IEmployeeRepository employees, IPayrollAdjustmentRepository adjustments, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<VoidPayrollAdjustmentCommand, PayrollAdjustmentDto>
{
    public async Task<PayrollAdjustmentDto> Handle(VoidPayrollAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var adjustment = await adjustments.GetRequiredAsync(request.AdjustmentId, cancellationToken);
        await slips.EnsureOpenAsync(await employees.GetRequiredAsync(adjustment.EmployeeId, cancellationToken), adjustment.Month, cancellationToken);
        adjustment.Void(request.StaffName);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PayrollAdjustmentDto.From(adjustment);
    }
}
