using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Leaves;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// Leaves (الإجازات): their types, each employee's balances a year, and the requests a manager
// approves, rejects, or cancels from a day on once approved. A request takes the working days of
// its dates (LeaveDays); paid ones come out of the balance, which opens each year at the type's
// days (a new hire's share of annual leave), unpaid ones take a day's pay each. Approving marks
// its days on the timesheet, leave or unpaid leave; cancelling gives back and unmarks those from
// the day it is cancelled from. Nothing is changed in a month whose pay is finalized.

public sealed record LeaveTypeDto(Guid Id, string Name, bool IsPaid, int DefaultDaysPerYear, bool ProratedForNewHires, bool InUse);

// Opened: kept, a manager's or a request's; otherwise what the year would open at, not kept yet.
// Pending: the days of requests waiting for a manager, not taken yet but asked for.
public sealed record LeaveBalanceDto(
    Guid EmployeeId, string EmployeeName, Guid LeaveTypeId, string LeaveTypeName, int Year, decimal AllocatedDays, decimal UsedDays,
    decimal RemainingDays, decimal PendingDays, bool Opened, string? AllocatedBy);

public sealed record LeaveRequestDto(
    Guid Id, Guid EmployeeId, string EmployeeName, Guid LeaveTypeId, string LeaveTypeName, bool IsPaid, DateOnly StartDate, DateOnly EndDate, int Days,
    int DaysTaken, LeaveRequestStatus Status, string? Reason, string? ManagerNotes, string RequestedBy, DateTime RequestedAtUtc, string? DecidedBy,
    DateTime? DecidedAtUtc, DateOnly? CancelledFrom, int CancelledDays, string? CancelledBy, DateTime? CancelledAtUtc, string? CancellationNotes);

// The working days a leave's dates hold for an employee, as a request would take them.
public sealed record LeaveDaysDto(int Days, IReadOnlyList<DateOnly> Dates);

// An employee's own: this year's balances, and their requests, the latest first; the types they
// may ask for, paid and unpaid.
public sealed record EmployeeLeavesDto(int Year, IReadOnlyList<LeaveBalanceDto> Balances, IReadOnlyList<LeaveRequestDto> Requests, IReadOnlyList<LeaveChoiceDto> Types);

// A type as the portal offers it to ask for.
public sealed record LeaveChoiceDto(Guid Id, string Name, bool IsPaid);

// ---- Types: a manager's ----

public sealed record GetLeaveTypesQuery : IRequest<IReadOnlyList<LeaveTypeDto>>;

internal sealed class GetLeaveTypesHandler(ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests)
    : IRequestHandler<GetLeaveTypesQuery, IReadOnlyList<LeaveTypeDto>>
{
    public async Task<IReadOnlyList<LeaveTypeDto>> Handle(GetLeaveTypesQuery request, CancellationToken cancellationToken)
    {
        var list = new List<LeaveTypeDto>();
        foreach (var t in await types.GetAllAsync(cancellationToken))
        {
            var inUse = await balances.AnyForTypeAsync(t.Id, cancellationToken) || await requests.AnyForTypeAsync(t.Id, cancellationToken);
            list.Add(LeaveDtos.Of(t, inUse));
        }

        return list;
    }
}

// Added under this id, or changed: from the next balance opened.
public sealed record SaveLeaveTypeCommand(Guid Id, string Name, bool IsPaid, int DefaultDaysPerYear, bool ProratedForNewHires) : IRequest<LeaveTypeDto>;

internal sealed class SaveLeaveTypeHandler(ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveLeaveTypeCommand, LeaveTypeDto>
{
    public async Task<LeaveTypeDto> Handle(SaveLeaveTypeCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if ((await types.GetAllAsync(cancellationToken)).FirstOrDefault(t => t.Id != request.Id && string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } taken)
            throw new DomainException($"A leave type is called {taken.Name} already.");
        var type = await types.GetByIdAsync(request.Id, cancellationToken);
        var inUse = type is not null && (await balances.AnyForTypeAsync(type.Id, cancellationToken) || await requests.AnyForTypeAsync(type.Id, cancellationToken));
        if (type is null)
        {
            type = new LeaveType(request.Id, name, request.IsPaid, request.DefaultDaysPerYear, request.ProratedForNewHires);
            types.Add(type);
        }
        else
        {
            if (inUse && type.IsPaid != request.IsPaid)
                throw new DomainException($"{type.Name} has balances or requests: whether it is paid stays as it is. Add another type instead.");
            type.Rename(name);
            type.Change(request.IsPaid, request.DefaultDaysPerYear, request.ProratedForNewHires);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return LeaveDtos.Of(type, inUse);
    }
}

public sealed record DeleteLeaveTypeCommand(Guid Id) : IRequest;

internal sealed class DeleteLeaveTypeHandler(ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteLeaveTypeCommand>
{
    public async Task Handle(DeleteLeaveTypeCommand request, CancellationToken cancellationToken)
    {
        var type = await types.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(LeaveType), request.Id);
        if (await balances.AnyForTypeAsync(type.Id, cancellationToken) || await requests.AnyForTypeAsync(type.Id, cancellationToken))
            throw new DomainException($"{type.Name} has balances or requests: it stays.");
        types.Remove(type);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// ---- Balances ----

// A year's: everyone employed in it, or one employee; a row for each paid type.
public sealed record GetLeaveBalancesQuery(int Year, Guid? EmployeeId = null) : IRequest<IReadOnlyList<LeaveBalanceDto>>;

internal sealed class GetLeaveBalancesHandler(
    IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests)
    : IRequestHandler<GetLeaveBalancesQuery, IReadOnlyList<LeaveBalanceDto>>
{
    public async Task<IReadOnlyList<LeaveBalanceDto>> Handle(GetLeaveBalancesQuery request, CancellationToken cancellationToken) =>
        await LeaveBalances.ReadAsync(employees, types, balances, requests, request.Year, request.EmployeeId, cancellationToken);
}

// A manager's: an employee's days of a type in a year, in half days.
public sealed record SetLeaveBalanceCommand(Guid EmployeeId, Guid LeaveTypeId, int Year, decimal Days, string StaffName) : IRequest<LeaveBalanceDto>, IRetryOnConflict;

internal sealed class SetLeaveBalanceHandler(
    IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests, IUnitOfWork unitOfWork)
    : IRequestHandler<SetLeaveBalanceCommand, LeaveBalanceDto>
{
    public async Task<LeaveBalanceDto> Handle(SetLeaveBalanceCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        var type = await types.GetByIdAsync(request.LeaveTypeId, cancellationToken) ?? throw new NotFoundException(nameof(LeaveType), request.LeaveTypeId);
        if (!type.IsPaid)
            throw new DomainException($"{type.Name} is unpaid: it has no balance.");
        var balance = await balances.GetAsync(employee.Id, type.Id, request.Year, cancellationToken);
        if (balance is null)
        {
            balance = new EmployeeLeaveBalance(employee.Id, type.Id, request.Year, request.Days, request.StaffName);
            balances.Add(balance);
        }
        else
        {
            balance.Allocate(request.Days, request.StaffName);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await LeaveBalances.ReadAsync(employees, types, balances, requests, request.Year, employee.Id, cancellationToken)).Single(b => b.LeaveTypeId == type.Id);
    }
}

// ---- Requests ----

// The working days a leave would take: for the form, before it is asked.
public sealed record CountLeaveDaysQuery(Guid EmployeeId, DateOnly StartDate, DateOnly EndDate) : IRequest<LeaveDaysDto>;

internal sealed class CountLeaveDaysHandler(IEmployeeRepository employees, IPublicHolidayRepository holidays)
    : IRequestHandler<CountLeaveDaysQuery, LeaveDaysDto>
{
    public async Task<LeaveDaysDto> Handle(CountLeaveDaysQuery request, CancellationToken cancellationToken)
    {
        if (request.EndDate < request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber >= LeaveRequest.MaxDays)
            return new LeaveDaysDto(0, []);
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        var days = LeaveDays.Of(employee, request.StartDate, request.EndDate, await holidays.GetBetweenAsync(request.StartDate, request.EndDate, cancellationToken));
        return new LeaveDaysDto(days.Count, days);
    }
}

// Waiting ones (Status Pending), an employee's, or a year's; the latest first, waiting ones the oldest first.
public sealed record GetLeaveRequestsQuery(LeaveRequestStatus? Status = null, Guid? EmployeeId = null, int? Year = null) : IRequest<IReadOnlyList<LeaveRequestDto>>;

internal sealed class GetLeaveRequestsHandler(IEmployeeRepository employees, ILeaveTypeRepository types, ILeaveRequestRepository requests, IRecordDescriptions clock)
    : IRequestHandler<GetLeaveRequestsQuery, IReadOnlyList<LeaveRequestDto>>
{
    public async Task<IReadOnlyList<LeaveRequestDto>> Handle(GetLeaveRequestsQuery request, CancellationToken cancellationToken)
    {
        var list = request.Status == LeaveRequestStatus.Pending ? await requests.GetPendingAsync(cancellationToken)
            : request.EmployeeId is { } employeeId ? await requests.GetForEmployeeAsync(employeeId, cancellationToken)
            : await requests.GetForYearAsync(request.Year ?? clock.BusinessDateOf(DateTime.UtcNow).Year, cancellationToken);
        var filtered = list.Where(r => (request.Status is null || r.Status == request.Status)
            && (request.EmployeeId is null || r.EmployeeId == request.EmployeeId)
            && (request.Year is null || r.Year == request.Year)).ToList();
        return await LeaveDtos.OfAsync(filtered, employees, types, cancellationToken);
    }
}

// Asked for: by the employee in the portal, or by someone of HR for them. RequestId is made by
// whoever asks: a retry finds the one made.
public sealed record SubmitLeaveRequestCommand(
    Guid RequestId, Guid EmployeeId, Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? Reason, string RequestedBy)
    : IRequest<LeaveRequestDto>, IRetryOnConflict;

internal sealed class SubmitLeaveRequestHandler(
    IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests,
    IPublicHolidayRepository holidays, IUnitOfWork unitOfWork)
    : IRequestHandler<SubmitLeaveRequestCommand, LeaveRequestDto>
{
    public async Task<LeaveRequestDto> Handle(SubmitLeaveRequestCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } made)
            return (await LeaveDtos.OfAsync([made], employees, types, cancellationToken)).Single();
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        if (!employee.IsActive)
            throw new DomainException($"{employee.Name} has left.");
        var type = await types.GetByIdAsync(request.LeaveTypeId, cancellationToken) ?? throw new NotFoundException(nameof(LeaveType), request.LeaveTypeId);
        if (request.EndDate < request.StartDate)
            throw new DomainException("A leave ends on or after the day it starts.");
        var days = LeaveDays.Of(employee, request.StartDate, request.EndDate, await holidays.GetBetweenAsync(request.StartDate, request.EndDate, cancellationToken));
        var asked = new LeaveRequest(
            request.RequestId, employee.Id, type.Id, request.StartDate, request.EndDate, days.Count, request.Reason, request.RequestedBy);

        if ((await requests.GetOverlappingAsync(employee.Id, request.StartDate, request.EndDate, cancellationToken)).FirstOrDefault() is { } other)
            throw new DomainException(
                $"{employee.Name} has asked for leave from {other.StartDate:d MMM} to {(other.CancelledFrom?.AddDays(-1) ?? other.EndDate):d MMM} already: a day is asked for once.");

        if (type.IsPaid)
        {
            var balance = await balances.GetAsync(employee.Id, type.Id, asked.Year, cancellationToken);
            if (balance is null)
            {
                balance = EmployeeLeaveBalance.Open(employee, type, asked.Year);
                balances.Add(balance);
            }

            var pending = (await requests.GetPendingAsync(cancellationToken))
                .Where(r => r.EmployeeId == employee.Id && r.LeaveTypeId == type.Id && r.Year == asked.Year)
                .Sum(r => r.TotalDaysRequested);
            if (days.Count > balance.RemainingDays - pending)
            {
                var left = EmployeeLeaveBalance.Days(balance.RemainingDays);
                throw new DomainException(pending > 0
                    ? $"Only {left} days of {type.Name} are left in {asked.Year}, {pending} of them asked for already: {days.Count} are asked."
                    : $"Only {left} days of {type.Name} are left in {asked.Year}: {days.Count} are asked.");
            }
        }

        requests.Add(asked);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await LeaveDtos.OfAsync([asked], employees, types, cancellationToken)).Single();
    }
}

// A manager's: its days taken from the balance (paid) and marked on the timesheet.
public sealed record ApproveLeaveRequestCommand(Guid Id, string? Notes, string StaffId, string StaffName) : IRequest<LeaveRequestDto>, IRetryOnConflict;

internal sealed class ApproveLeaveRequestHandler(
    IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests,
    IPublicHolidayRepository holidays, IDayMarkRepository marks, IPayrollSlipRepository slips, IUnitOfWork unitOfWork, AttendanceBroadcast broadcast)
    : IRequestHandler<ApproveLeaveRequestCommand, LeaveRequestDto>
{
    public async Task<LeaveRequestDto> Handle(ApproveLeaveRequestCommand request, CancellationToken cancellationToken)
    {
        var asked = await requests.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(LeaveRequest), request.Id);
        if (!asked.IsPending)
            throw new DomainException($"This leave was {asked.Status.ToString().ToLowerInvariant()} already.");
        var employee = await employees.GetRequiredAsync(asked.EmployeeId, cancellationToken);
        var type = await types.GetByIdAsync(asked.LeaveTypeId, cancellationToken) ?? throw new NotFoundException(nameof(LeaveType), asked.LeaveTypeId);
        var days = LeaveDays.Of(employee, asked.StartDate, asked.EndDate, await holidays.GetBetweenAsync(asked.StartDate, asked.EndDate, cancellationToken));
        if (days.Count != asked.TotalDaysRequested)
            throw new DomainException(
                $"Its working days have changed since it was asked ({days.Count} now, {asked.TotalDaysRequested} asked): a holiday or their days off. Reject it, and ask again.");
        foreach (var month in days.Select(PayrollMonth.Of).Distinct())
            await slips.EnsureOpenAsync(employee, month, cancellationToken);

        if (type.IsPaid)
        {
            var balance = await balances.GetAsync(employee.Id, type.Id, asked.Year, cancellationToken);
            if (balance is null)
            {
                balance = EmployeeLeaveBalance.Open(employee, type, asked.Year);
                balances.Add(balance);
            }

            balance.Use(days.Count);
        }

        var kind = type.IsPaid ? DayMarkKind.Leave : DayMarkKind.UnpaidLeave;
        var existing = (await marks.GetBetweenAsync(asked.StartDate, asked.EndDate, cancellationToken)).Where(m => m.EmployeeId == employee.Id).ToDictionary(m => m.Date);
        foreach (var day in days)
        {
            // Read untracked: changed through the tracked one.
            if (existing.ContainsKey(day) && await marks.GetAsync(employee.Id, day, cancellationToken) is { } mark)
                mark.Change(kind, type.Name, request.StaffName, asked.Id);
            else
                marks.Add(new DayMark(employee.Id, day, kind, type.Name, request.StaffName, asked.Id));
        }

        asked.Approve(request.StaffId, request.StaffName, request.Notes);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await broadcast.ChangedAsync([employee.Id], cancellationToken);
        return (await LeaveDtos.OfAsync([asked], employees, types, cancellationToken)).Single();
    }
}

public sealed record RejectLeaveRequestCommand(Guid Id, string? Notes, string StaffId, string StaffName) : IRequest<LeaveRequestDto>, IRetryOnConflict;

internal sealed class RejectLeaveRequestHandler(IEmployeeRepository employees, ILeaveTypeRepository types, ILeaveRequestRepository requests, IUnitOfWork unitOfWork)
    : IRequestHandler<RejectLeaveRequestCommand, LeaveRequestDto>
{
    public async Task<LeaveRequestDto> Handle(RejectLeaveRequestCommand request, CancellationToken cancellationToken)
    {
        var asked = await requests.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(LeaveRequest), request.Id);
        asked.Reject(request.StaffId, request.StaffName, request.Notes);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await LeaveDtos.OfAsync([asked], employees, types, cancellationToken)).Single();
    }
}

// A manager's: an approved leave cancelled from `From` on (the business day unless said), its
// days from then given back and unmarked; from its first day or before, all of it.
public sealed record CancelLeaveRequestCommand(Guid Id, DateOnly? From, string? Notes, string StaffId, string StaffName)
    : IRequest<LeaveRequestDto>, IRetryOnConflict;

internal sealed class CancelLeaveRequestHandler(
    IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests,
    IDayMarkRepository marks, IPayrollSlipRepository slips, IRecordDescriptions clock, IUnitOfWork unitOfWork, AttendanceBroadcast broadcast)
    : IRequestHandler<CancelLeaveRequestCommand, LeaveRequestDto>
{
    public async Task<LeaveRequestDto> Handle(CancelLeaveRequestCommand request, CancellationToken cancellationToken)
    {
        var asked = await requests.GetByIdAsync(request.Id, cancellationToken) ?? throw new NotFoundException(nameof(LeaveRequest), request.Id);
        if (asked.Status != LeaveRequestStatus.Approved)
            throw new DomainException(asked.IsPending ? "This leave waits for a decision: reject it instead." : $"This leave was {asked.Status.ToString().ToLowerInvariant()} already.");
        var employee = await employees.GetRequiredAsync(asked.EmployeeId, cancellationToken);
        var type = await types.GetByIdAsync(asked.LeaveTypeId, cancellationToken) ?? throw new NotFoundException(nameof(LeaveType), asked.LeaveTypeId);
        var from = request.From ?? clock.BusinessDateOf(DateTime.UtcNow);
        var whole = from <= asked.StartDate;
        var givenBack = (await marks.GetForLeaveRequestAsync(asked.Id, cancellationToken)).Where(m => whole || m.Date >= from).ToList();
        foreach (var month in givenBack.Select(m => PayrollMonth.Of(m.Date)).Distinct())
            await slips.EnsureOpenAsync(employee, month, cancellationToken);

        asked.Cancel(from, whole ? asked.TotalDaysRequested : givenBack.Count, request.StaffId, request.StaffName, request.Notes);
        foreach (var mark in givenBack)
            marks.Remove(mark);
        var days = whole ? asked.TotalDaysRequested : givenBack.Count;
        if (type.IsPaid && days > 0 && await balances.GetAsync(employee.Id, type.Id, asked.Year, cancellationToken) is { } balance)
            balance.Release(days);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await broadcast.ChangedAsync([employee.Id], cancellationToken);
        return (await LeaveDtos.OfAsync([asked], employees, types, cancellationToken)).Single();
    }
}

// The portal's: their own balances this business year, and their requests.
public sealed record GetEmployeeLeavesQuery(Guid EmployeeId) : IRequest<EmployeeLeavesDto>;

internal sealed class GetEmployeeLeavesHandler(
    IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests, IRecordDescriptions clock)
    : IRequestHandler<GetEmployeeLeavesQuery, EmployeeLeavesDto>
{
    public async Task<EmployeeLeavesDto> Handle(GetEmployeeLeavesQuery request, CancellationToken cancellationToken)
    {
        var year = clock.BusinessDateOf(DateTime.UtcNow).Year;
        var theirs = await LeaveBalances.ReadAsync(employees, types, balances, requests, year, request.EmployeeId, cancellationToken);
        var asked = await requests.GetForEmployeeAsync(request.EmployeeId, cancellationToken);
        var choices = (await types.GetAllAsync(cancellationToken)).Select(t => new LeaveChoiceDto(t.Id, t.Name, t.IsPaid)).ToList();
        return new EmployeeLeavesDto(year, theirs, await LeaveDtos.OfAsync(asked, employees, types, cancellationToken), choices);
    }
}

internal static class LeaveBalances
{
    public static async Task<IReadOnlyList<LeaveBalanceDto>> ReadAsync(
        IEmployeeRepository employees, ILeaveTypeRepository types, IEmployeeLeaveBalanceRepository balances, ILeaveRequestRepository requests, int year,
        Guid? employeeId, CancellationToken cancellationToken)
    {
        var paid = (await types.GetAllAsync(cancellationToken)).Where(t => t.IsPaid).ToList();
        var first = new DateOnly(year, 1, 1);
        var last = new DateOnly(year, 12, 31);
        var people = employeeId is { } id
            ? [await employees.GetRequiredAsync(id, cancellationToken)]
            : (await employees.GetAllAsync(cancellationToken)).Where(e => e.HiredOn <= last && (e.LeftOn is null || e.LeftOn >= first)).ToList();
        var kept = (await balances.GetForYearAsync(year, employeeId, cancellationToken)).ToDictionary(b => (b.EmployeeId, b.LeaveTypeId));
        var pending = (await requests.GetPendingAsync(cancellationToken)).Where(r => r.Year == year).ToList();
        return people
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(e => paid.Select(t =>
            {
                var b = kept.GetValueOrDefault((e.Id, t.Id));
                var allocated = b?.TotalAllocatedDays ?? EmployeeLeaveBalance.DaysFor(e, t, year);
                var used = b?.UsedDays ?? 0;
                var asked = pending.Where(r => r.EmployeeId == e.Id && r.LeaveTypeId == t.Id).Sum(r => r.TotalDaysRequested);
                return new LeaveBalanceDto(e.Id, e.Name, t.Id, t.Name, year, allocated, used, allocated - used, asked, b is not null, b?.AllocatedBy);
            }))
            .ToList();
    }
}

internal static class LeaveDtos
{
    public static LeaveTypeDto Of(LeaveType t, bool inUse) => new(t.Id, t.Name, t.IsPaid, t.DefaultDaysPerYear, t.ProratedForNewHires, inUse);

    public static async Task<IReadOnlyList<LeaveRequestDto>> OfAsync(
        IReadOnlyList<LeaveRequest> list, IEmployeeRepository employees, ILeaveTypeRepository types, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
            return [];
        var names = (await employees.GetAllAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.Name);
        var kinds = (await types.GetAllAsync(cancellationToken)).ToDictionary(t => t.Id);
        return list.Select(r =>
        {
            var type = kinds.GetValueOrDefault(r.LeaveTypeId);
            return new LeaveRequestDto(
                r.Id, r.EmployeeId, names.GetValueOrDefault(r.EmployeeId) ?? "—", r.LeaveTypeId, type?.Name ?? "—", type?.IsPaid ?? true, r.StartDate, r.EndDate,
                r.TotalDaysRequested, r.DaysTaken, r.Status, r.Reason, r.ManagerNotes, r.RequestedBy, r.RequestedAtUtc, r.DecidedBy, r.DecidedAtUtc,
                r.CancelledFrom, r.CancelledDays, r.CancelledBy, r.CancelledAtUtc, r.CancellationNotes);
        }).ToList();
    }
}
