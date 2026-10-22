using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Treasury;

namespace OtantikPos.Inventory.Application.Hr;

// HR's advances (السلفيات): what is deducted from whose pay, and what was handed out with no one
// named yet.
//
// An advance is a safe entry (kind EmployeeAdvance), out of the safe or a day's takings, entered
// in Financials or the daily batch. Paid to an employee, it is deducted from their pay. Handed out
// as a lump sum with no one named (السلفيات المعلقة), the cash leaves at once and it waits here: a
// manager gives it out to employees, a share each (AdvanceAllocation), in parts if need be, and
// each share is deducted from its employee's pay as their own advance would be. Fully allocated
// once the shares add up to it. A share is taken back while the pay it is deducted from is not
// finalized; the advance is voided only once no share of it stands.

public enum AdvanceOrigin
{
    // Paid to the employee.
    Direct = 1,

    // Their share of advances handed out with no one named.
    Share = 2,
}

// An advance deducted from an employee's pay of PayrollMonth. Id is the safe entry's (Direct) or
// the share's; SourceTransactionId is the safe entry the cash moved with.
public sealed record AllocatedAdvanceDto(
    Guid Id, AdvanceOrigin Origin, Guid SourceTransactionId, Guid EmployeeId, string Employee, decimal Amount, DateOnly Date, DateOnly PayrollMonth,
    DateOnly? FromTakingsOf, string? Notes, string RecordedBy);

public sealed record AdvanceShareDto(
    Guid Id, Guid EmployeeId, string Employee, decimal Amount, DateOnly PayrollMonth, string? Notes, string AllocatedBy, DateTime AllocatedAtUtc);

// Advances handed out with no one named: how much, how much is given out, what is left, and the
// shares standing.
public sealed record PendingAdvanceDto(
    Guid Id, DateOnly Date, decimal Amount, decimal Allocated, decimal Remaining, DateOnly? FromTakingsOf, string? Notes, string RecordedBy,
    IReadOnlyList<AdvanceShareDto> Shares)
{
    public bool FullyAllocated => Remaining == 0;
}

public sealed record AdvancesDto(DateOnly Month, IReadOnlyList<AllocatedAdvanceDto> Allocated, IReadOnlyList<PendingAdvanceDto> Pending);

internal static class Advances
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static PendingAdvanceDto Pending(SafeTransaction advance, IEnumerable<AdvanceAllocation> shares, Func<Guid, string> name)
    {
        var standing = shares.Where(a => a.SourceTransactionId == advance.Id && !a.IsVoided).OrderBy(a => a.AllocatedAtUtc).ToList();
        var amount = -advance.Amount;
        var allocated = standing.Sum(a => a.Amount);
        return new PendingAdvanceDto(
            advance.Id, advance.BusinessDate, amount, allocated, amount - allocated, advance.RevenueDate, advance.Notes, advance.RecordedBy,
            standing.Select(a => new AdvanceShareDto(a.Id, a.EmployeeId, name(a.EmployeeId), a.Amount, a.PayrollMonth, a.Notes, a.AllocatedBy, a.AllocatedAtUtc))
                .ToList());
    }

    // The month an advance handed out on `date` counts in for an employee: its own, or the first
    // after it whose pay is not finalized for them.
    public static async Task<DateOnly> OpenMonthAsync(IPayrollSlipRepository slips, Guid employeeId, DateOnly date, CancellationToken cancellationToken)
    {
        var month = PayrollMonth.Of(date);
        while (await slips.GetAsync(employeeId, month, cancellationToken) is { IsFinalized: true })
            month = month.AddMonths(1);
        return month;
    }

    public static string Money(decimal amount) => $"L.E {amount.ToString("N2", Invariant)}";

    public static string Day(DateOnly day) => day.ToString("d MMM yyyy", Invariant);

    public static async Task<Func<Guid, string>> NamesAsync(IEmployeeRepository employees, CancellationToken cancellationToken)
    {
        var names = (await employees.GetAllAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.Name);
        return id => names.GetValueOrDefault(id) ?? "A removed employee";
    }
}

// A payroll month's advances, each deducted from whose pay; and the advances handed out with no
// one named: every one not all given out, with those all given out in the month.
public sealed record GetAdvancesQuery(DateOnly Month) : IRequest<AdvancesDto>;

internal sealed class GetAdvancesHandler(ISafeTransactionRepository safe, IAdvanceAllocationRepository allocations, IEmployeeRepository employees)
    : IRequestHandler<GetAdvancesQuery, AdvancesDto>
{
    public async Task<AdvancesDto> Handle(GetAdvancesQuery request, CancellationToken cancellationToken)
    {
        var month = PayrollMonth.Required(request.Month);
        var name = await Advances.NamesAsync(employees, cancellationToken);
        var pending = await safe.GetPendingAdvancesAsync(cancellationToken);
        var sources = pending.ToDictionary(t => t.Id);

        var direct = (await safe.GetForPayrollMonthAsync(month, null, cancellationToken))
            .Where(t => t.Kind == SafeTransactionKind.EmployeeAdvance && !t.IsReversal && !t.IsVoided)
            .Select(t => new AllocatedAdvanceDto(
                t.Id, AdvanceOrigin.Direct, t.Id, t.EmployeeId!.Value, name(t.EmployeeId.Value), -t.Amount, t.BusinessDate, month, t.RevenueDate, t.Notes,
                t.RecordedBy));
        var shared = (await allocations.GetForPayrollMonthAsync(month, null, cancellationToken))
            .Where(a => !a.IsVoided)
            .Select(a => new AllocatedAdvanceDto(
                a.Id, AdvanceOrigin.Share, a.SourceTransactionId, a.EmployeeId, name(a.EmployeeId), a.Amount, a.Date, month,
                sources.GetValueOrDefault(a.SourceTransactionId)?.RevenueDate, a.Notes, a.AllocatedBy));

        var shares = await allocations.GetForAdvancesAsync(pending.Select(t => t.Id).ToList(), cancellationToken);
        var waiting = pending.Select(t => Advances.Pending(t, shares, name))
            .Where(p => p.Remaining > 0 || PayrollMonth.Of(p.Date) == month)
            .ToList();
        return new AdvancesDto(
            month, direct.Concat(shared).OrderByDescending(a => a.Date).ThenBy(a => a.Employee, StringComparer.CurrentCulture).ToList(), waiting);
    }
}

public sealed record AdvanceShareRequest(Guid EmployeeId, decimal Amount, string? Notes = null);

// Advances handed out with no one named, given out (in part, or the rest of them): a share each,
// deducted from each employee's pay. A manager's.
public sealed record DistributeAdvanceCommand(Guid AdvanceId, IReadOnlyList<AdvanceShareRequest> Shares, string StaffName)
    : IRequest<PendingAdvanceDto>, IRetryOnConflict;

internal sealed class DistributeAdvanceHandler(
    ISafeTransactionRepository safe,
    IAdvanceAllocationRepository allocations,
    IEmployeeRepository employees,
    IPayrollSlipRepository slips,
    IUnitOfWork unitOfWork) : IRequestHandler<DistributeAdvanceCommand, PendingAdvanceDto>
{
    public async Task<PendingAdvanceDto> Handle(DistributeAdvanceCommand request, CancellationToken cancellationToken)
    {
        var advance = await safe.GetByIdAsync(request.AdvanceId, cancellationToken) ?? throw new NotFoundException("Advance", request.AdvanceId);
        if (!advance.IsPendingAdvance || advance.IsReversal)
            throw new DomainException("Only advances handed out with no one named are given out here: this one is an employee's already.");
        if (advance.IsVoided)
            throw new DomainException("These advances were voided: there is nothing to give out.");
        if (request.Shares.Count == 0)
            throw new DomainException("Give at least one employee a share.");

        var name = await Advances.NamesAsync(employees, cancellationToken);
        if (request.Shares.GroupBy(s => s.EmployeeId).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new DomainException($"{name(twice.Key)} is on the list twice: give them one share.");
        var standing = (await allocations.GetForAdvancesAsync([advance.Id], cancellationToken)).Where(a => !a.IsVoided).ToList();
        var remaining = -advance.Amount - standing.Sum(a => a.Amount);
        var total = request.Shares.Sum(s => Math.Round(s.Amount, 2, MidpointRounding.AwayFromZero));
        if (remaining <= 0)
            throw new DomainException("These advances are all given out already.");
        if (total > remaining)
            throw new DomainException(
                $"{Advances.Money(remaining)} of these advances is left to give out, and the shares come to {Advances.Money(total)}: give out that much or less.");

        foreach (var share in request.Shares)
        {
            var employee = await employees.GetByIdAsync(share.EmployeeId, cancellationToken) ?? throw new NotFoundException("Employee", share.EmployeeId);
            if (!employee.IsEmployedOn(advance.BusinessDate))
                throw new DomainException($"{employee.Name} was not employed on {Advances.Day(advance.BusinessDate)}, when these advances were handed out.");
            var month = await Advances.OpenMonthAsync(slips, employee.Id, advance.BusinessDate, cancellationToken);
            var made = new AdvanceAllocation(Guid.NewGuid(), advance.Id, employee.Id, share.Amount, advance.BusinessDate, month, share.Notes, request.StaffName);
            allocations.Add(made);
            standing.Add(made);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Advances.Pending(advance, standing, name);
    }
}

// A share taken back, for the advances to be given out again: while the pay it is deducted from
// is not finalized. A manager's.
public sealed record TakeBackAdvanceShareCommand(Guid ShareId, string StaffName) : IRequest<PendingAdvanceDto>;

internal sealed class TakeBackAdvanceShareHandler(
    ISafeTransactionRepository safe,
    IAdvanceAllocationRepository allocations,
    IEmployeeRepository employees,
    IPayrollSlipRepository slips,
    IUnitOfWork unitOfWork) : IRequestHandler<TakeBackAdvanceShareCommand, PendingAdvanceDto>
{
    public async Task<PendingAdvanceDto> Handle(TakeBackAdvanceShareCommand request, CancellationToken cancellationToken)
    {
        var share = await allocations.GetByIdAsync(request.ShareId, cancellationToken) ?? throw new NotFoundException("Advance share", request.ShareId);
        var name = await Advances.NamesAsync(employees, cancellationToken);
        if (await slips.GetAsync(share.EmployeeId, share.PayrollMonth, cancellationToken) is { IsFinalized: true })
            throw new DomainException(
                $"{name(share.EmployeeId)}'s pay for {share.PayrollMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} is finalized with this share deducted: it cannot be taken back now.");
        share.Void(request.StaffName);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var advance = await safe.GetByIdAsync(share.SourceTransactionId, cancellationToken) ?? throw new NotFoundException("Advance", share.SourceTransactionId);
        return Advances.Pending(advance, await allocations.GetForAdvancesAsync([advance.Id], cancellationToken), name);
    }
}
