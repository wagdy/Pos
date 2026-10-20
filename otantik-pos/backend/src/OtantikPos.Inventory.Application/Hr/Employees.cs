using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Domain.Approvals;

namespace OtantikPos.Inventory.Application.Hr;

// HR's employees (الموظفين): added, changed, leaving and coming back. A manager's change is made at
// once; anyone else's is a request (module Employee), and nothing changes until a manager
// approves it. A new employee does not exist until then.

public sealed record EmployeeDto(
    Guid Id,
    string Name,
    string JobTitle,
    decimal MonthlySalary,
    TimeOnly ShiftStartsAt,
    TimeOnly ShiftEndsAt,
    IReadOnlyList<DayOfWeek> DaysOff,
    string? DeviceUserId,
    DateOnly HiredOn,
    DateOnly? LeftOn,
    bool IsActive,
    decimal DailyRate,
    decimal HourlyRate)
{
    public static EmployeeDto From(Employee e) => new(
        e.Id, e.Name, e.JobTitle, e.MonthlySalary, e.ShiftStartsAt, e.ShiftStartsAt.Add(Employee.ShiftLength), Employees.DaysOf(e.DaysOff),
        e.DeviceUserId, e.HiredOn, e.LeftOn, e.IsActive, Math.Round(e.DailyRate, 2, MidpointRounding.AwayFromZero),
        Math.Round(e.HourlyRate, 2, MidpointRounding.AwayFromZero));
}

// An employee's details as entered.
public sealed record EmployeeDetails(
    string Name,
    string JobTitle,
    decimal MonthlySalary,
    TimeOnly ShiftStartsAt,
    IReadOnlyList<DayOfWeek> DaysOff,
    DateOnly HiredOn,
    string? DeviceUserId);

// What a request holds: the details (a new employee, or a change), or that they leave or return.
public sealed record EmployeeRequest(EmployeeDetails? Details = null, DateOnly? LeavesOn = null, DateOnly? ReturnsOn = null);

public sealed record EmployeeResultDto(ApprovalOutcome Outcome, EmployeeDto? Employee, ApprovalRequestDto? Request);

public static class Employees
{
    public static WeekDays Flags(IEnumerable<DayOfWeek> days) =>
        days.Aggregate(WeekDays.None, (all, day) => all | WeekDaysExtensions.Of(Enum.IsDefined(day) ? day : throw new DomainException("Choose the days off from the days of the week.")));

    public static IReadOnlyList<DayOfWeek> DaysOf(WeekDays days) =>
        Enum.GetValues<DayOfWeek>().Where(d => (days & WeekDaysExtensions.Of(d)) != 0).ToList();
}

// Checks, describes and makes an employee's change: for a manager's own and for an approval
// alike. What it makes is added for the caller to save.
internal sealed class EmployeeWriter(IEmployeeRepository employees, IAttendanceLogRepository punches)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Refused before a request goes to a manager, and checked again on approval.
    public async Task CheckAsync(Guid employeeId, EmployeeRequest request, CancellationToken cancellationToken)
    {
        var existing = await employees.GetByIdAsync(employeeId, cancellationToken);
        if (request.Details is { } details)
        {
            // The domain's own checks: a draft that is never saved.
            var draft = new Employee(
                employeeId, details.Name, details.JobTitle, details.MonthlySalary, details.ShiftStartsAt, Employees.Flags(details.DaysOff), details.HiredOn,
                details.DeviceUserId);
            if (draft.DeviceUserId is { } number && await employees.GetByDeviceUserIdAsync(number, cancellationToken) is { } holder && holder.Id != employeeId)
                throw new DomainException($"{holder.Name} has number {number} on the attendance device already.");
            if (existing is { LeftOn: { } left } && details.HiredOn > left)
                throw new DomainException("They have left: they start again by coming back, not by a later start.");
            return;
        }

        if (existing is null)
            throw new NotFoundException(nameof(Employee), employeeId);
        if (request.LeavesOn is { } lastDay)
        {
            if (!existing.IsActive)
                throw new DomainException($"{existing.Name} has already left.");
            if (lastDay < existing.HiredOn)
                throw new DomainException("Their last day is on or after the day they started.");
        }
        else if (request.ReturnsOn is { } returnsOn)
        {
            if (existing.IsActive)
                throw new DomainException($"{existing.Name} is still employed.");
            if (returnsOn <= existing.LeftOn)
                throw new DomainException("They come back after their last day.");
        }
        else
        {
            throw new DomainException("Say what changes.");
        }
    }

    public async Task<Employee> ApplyAsync(Guid employeeId, EmployeeRequest request, CancellationToken cancellationToken)
    {
        await CheckAsync(employeeId, request, cancellationToken);
        var employee = await employees.GetByIdAsync(employeeId, cancellationToken);
        if (request.Details is { } d)
        {
            if (employee is null)
            {
                employee = new Employee(employeeId, d.Name, d.JobTitle, d.MonthlySalary, d.ShiftStartsAt, Employees.Flags(d.DaysOff), d.HiredOn, d.DeviceUserId);
                employees.Add(employee);
            }
            else
            {
                employee.Rename(d.Name);
                employee.SetJobTitle(d.JobTitle);
                employee.SetSalary(d.MonthlySalary);
                employee.SetShift(d.ShiftStartsAt);
                employee.SetDaysOff(Employees.Flags(d.DaysOff));
                employee.ChangeStart(d.HiredOn);
                employee.LinkDevice(d.DeviceUserId);
            }

            // Punches already sent under their number, kept waiting for someone to have it.
            if (employee.DeviceUserId is { } number)
                foreach (var punch in await punches.GetUnassignedAsync(number, cancellationToken))
                    punch.AssignTo(employee.Id);
            return employee;
        }

        if (request.LeavesOn is { } lastDay)
            employee!.Leave(lastDay);
        else
            employee!.Return(request.ReturnsOn!.Value);
        return employee;
    }

    // "New employee · Hassan · Chef · L.E 5,400.00 a month", and what it changes, line by line.
    public async Task<(string Summary, ApprovalAction Action, IReadOnlyList<ApprovalChange> Changes)> DescribeAsync(
        Guid employeeId, EmployeeRequest request, CancellationToken cancellationToken)
    {
        var existing = await employees.GetByIdAsync(employeeId, cancellationToken);
        if (request.Details is not { } d)
        {
            var name = existing?.Name ?? "An employee";
            return request.LeavesOn is { } lastDay
                ? ($"{name} leaves · last day {Day(lastDay)}", ApprovalAction.Update, [new ApprovalChange("Status", "Employed", $"Left, last day {Day(lastDay)}")])
                : ($"{name} comes back · from {Day(request.ReturnsOn!.Value)}", ApprovalAction.Update,
                    [new ApprovalChange("Status", existing?.LeftOn is { } left ? $"Left, last day {Day(left)}" : "Left", $"Employed from {Day(request.ReturnsOn!.Value)}")]);
        }

        var lines = new List<ApprovalChange>();
        void Line(string label, string? before, string after)
        {
            if (existing is null || before != after)
                lines.Add(new ApprovalChange(label, existing is null ? null : before, after));
        }

        var shift = d.ShiftStartsAt.ToString("HH:mm", Invariant);
        Line("Name", existing?.Name, d.Name.Trim());
        Line("Job title", existing?.JobTitle, d.JobTitle.Trim());
        Line("Monthly salary", existing is null ? null : Money(existing.MonthlySalary), Money(d.MonthlySalary));
        Line("Shift", existing is null ? null : Shift(existing.ShiftStartsAt), Shift(d.ShiftStartsAt));
        Line("Days off", existing is null ? null : DaysOff(existing.DaysOff), DaysOff(Employees.Flags(d.DaysOff)));
        Line("Started on", existing is null ? null : Day(existing.HiredOn), Day(d.HiredOn));
        Line("Attendance device number", existing?.DeviceUserId ?? "—", string.IsNullOrWhiteSpace(d.DeviceUserId) ? "—" : d.DeviceUserId.Trim());
        return existing is null
            ? ($"New employee · {d.Name.Trim()} · {d.JobTitle.Trim()} · {Money(d.MonthlySalary)} a month, from {shift}", ApprovalAction.Create, lines)
            : ($"Employee changed · {existing.Name}", ApprovalAction.Update, lines);
    }

    private static string Shift(TimeOnly start) => $"{start.ToString("HH:mm", Invariant)}–{start.Add(Employee.ShiftLength).ToString("HH:mm", Invariant)}";

    private static string DaysOff(WeekDays days) =>
        days == WeekDays.None ? "None" : string.Join(", ", Employees.DaysOf(days).Select(d => Invariant.DateTimeFormat.GetAbbreviatedDayName(d)));

    private static string Money(decimal value) => $"L.E {value.ToString("N2", Invariant)}";

    private static string Day(DateOnly day) => day.ToString("d MMM yyyy", Invariant);
}

public sealed record GetEmployeesQuery : IRequest<IReadOnlyList<EmployeeDto>>;

internal sealed class GetEmployeesHandler(IEmployeeRepository employees) : IRequestHandler<GetEmployeesQuery, IReadOnlyList<EmployeeDto>>
{
    public async Task<IReadOnlyList<EmployeeDto>> Handle(GetEmployeesQuery request, CancellationToken cancellationToken) =>
        (await employees.GetAllAsync(cancellationToken)).Select(EmployeeDto.From).ToList();
}

// An employee added or changed, leaving or coming back. RequestId is made by the till: a retry
// finds what was made, or asked.
public sealed record ChangeEmployeeCommand(
    Guid EmployeeId, Guid RequestId, EmployeeRequest Change, string? Reason, string StaffId, string StaffName, bool CanApprove)
    : IRequest<EmployeeResultDto>, IRetryOnConflict;

internal sealed class ChangeEmployeeHandler(
    EmployeeWriter writer,
    IEmployeeRepository employees,
    IApprovalRequestRepository requests,
    IUnitOfWork unitOfWork) : IRequestHandler<ChangeEmployeeCommand, EmployeeResultDto>
{
    public async Task<EmployeeResultDto> Handle(ChangeEmployeeCommand request, CancellationToken cancellationToken)
    {
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return already.IsPending
                ? new EmployeeResultDto(ApprovalOutcome.Requested, null, ApprovalRequestDto.From(already))
                : new EmployeeResultDto(ApprovalOutcome.Applied, await DtoAsync(request.EmployeeId, cancellationToken), ApprovalRequestDto.From(already));
        await requests.EnsureNotPendingAsync(request.EmployeeId, cancellationToken);
        var (summary, action, changes) = await writer.DescribeAsync(request.EmployeeId, request.Change, cancellationToken);

        if (request.CanApprove)
        {
            var employee = await writer.ApplyAsync(request.EmployeeId, request.Change, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new EmployeeResultDto(ApprovalOutcome.Applied, EmployeeDto.From(employee), null);
        }

        await writer.CheckAsync(request.EmployeeId, request.Change, cancellationToken);
        var asked = ApprovalRequest.AskToChange(
            request.RequestId, ApprovalModule.Employee, action, request.EmployeeId, summary, changes, ApprovalPayload.Write(request.Change),
            request.Reason, request.StaffId, request.StaffName);
        requests.Add(asked);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new EmployeeResultDto(ApprovalOutcome.Requested, null, ApprovalRequestDto.From(asked));
    }

    private async Task<EmployeeDto?> DtoAsync(Guid id, CancellationToken cancellationToken) =>
        await employees.GetByIdAsync(id, cancellationToken) is { } e ? EmployeeDto.From(e) : null;
}
