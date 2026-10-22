using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Approvals;

namespace OtantikPos.Inventory.Application.Hr;

// HR's employees (الموظفين): added, changed, leaving and coming back. A manager's change is made at
// once; anyone else's is a request (module Employee), and nothing changes until a manager
// approves it. A new employee does not exist until then.

public sealed record EmployeeDto(
    Guid Id,
    string Name,
    // Their role's name; the role itself, in their department.
    string JobTitle,
    Guid? JobRoleId,
    // A monthly employee's salary, or a daily one's wage; the other is nothing.
    EmploymentType EmploymentType,
    decimal MonthlySalary,
    decimal DailyWage,
    Guid DepartmentId,
    // None for flexible hours.
    TimeOnly? ShiftStartsAt,
    TimeOnly? ShiftEndsAt,
    IReadOnlyList<DayOfWeek> DaysOff,
    string? DeviceUserId,
    DateOnly HiredOn,
    DateOnly? LeftOn,
    bool IsActive,
    decimal DailyRate,
    decimal HourlyRate,
    // رقم البطاقة and رقم الموبايل, when entered.
    string? NationalId,
    string? MobileNumber,
    // Their ID card's photo or scan, when one is uploaded: what it is, not its bytes.
    EmployeeIdPhotoDto? IdPhoto)
{
    public static EmployeeDto From(Employee e) => new(
        e.Id, e.Name, e.JobTitle, e.JobRoleId, e.EmploymentType, e.MonthlySalary, e.DailyWage, e.DepartmentId, e.ShiftStartsAt, e.ShiftStartsAt?.Add(Employee.ShiftLength), Employees.DaysOf(e.DaysOff),
        e.DeviceUserId, e.HiredOn, e.LeftOn, e.IsActive, Math.Round(e.DailyRate, 2, MidpointRounding.AwayFromZero),
        Math.Round(e.HourlyRate, 2, MidpointRounding.AwayFromZero), e.NationalId, e.MobileNumber,
        e.HasIdPhoto ? new EmployeeIdPhotoDto(e.IdPhotoFileName!, e.IdPhotoContentType!, e.IdPhotoBytes ?? 0, e.IdPhotoUploadedAtUtc!.Value, e.IdPhotoUploadedBy ?? string.Empty) : null);
}

public sealed record EmployeeIdPhotoDto(string FileName, string ContentType, int Bytes, DateTime UploadedAtUtc, string UploadedBy);

// An employee's details as entered: Name is their full name. Paid monthly at MonthlySalary or daily at DailyWage; in
// General when no department is given (a request asked before departments were). Their role is
// JobRoleId, one of their department's; given only a JobTitle, it is the department's role of
// that name, made when there is none.
public sealed record EmployeeDetails(
    string Name,
    string JobTitle,
    decimal MonthlySalary,
    TimeOnly? ShiftStartsAt,
    IReadOnlyList<DayOfWeek> DaysOff,
    DateOnly HiredOn,
    string? DeviceUserId,
    EmploymentType EmploymentType = EmploymentType.Monthly,
    decimal DailyWage = 0,
    Guid? DepartmentId = null,
    Guid? JobRoleId = null,
    string? NationalId = null,
    string? MobileNumber = null)
{
    public decimal Pay => EmploymentType == EmploymentType.Daily ? DailyWage : MonthlySalary;

    public Guid Department => DepartmentId ?? OtantikPos.Hr.Domain.Employees.Department.GeneralId;
}

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
internal sealed class EmployeeWriter(
    IEmployeeRepository employees, IAttendanceLogRepository punches, IDepartmentRepository departments, IJobRoleRepository roles)
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
                employeeId, details.Name, details.JobTitle, 0, details.ShiftStartsAt, Employees.Flags(details.DaysOff), details.HiredOn,
                details.DeviceUserId);
            draft.SetPay(details.EmploymentType, details.Pay);
            draft.SetNationalId(details.NationalId);
            draft.SetMobileNumber(details.MobileNumber);
            if (await departments.GetByIdAsync(details.Department, cancellationToken) is null)
                throw new DomainException("Choose the employee's department: that one is not there any more.");
            if (details.JobRoleId is { } roleId)
                Check(await roles.GetByIdAsync(roleId, cancellationToken), details);
            if (draft.DeviceUserId is { } number && await employees.GetByDeviceUserIdAsync(number, cancellationToken) is { } holder && holder.Id != employeeId)
                throw new DomainException($"{holder.Name} has number {number} on the attendance device already.");
            if (draft.NationalId is { } card && await employees.GetByNationalIdAsync(card, cancellationToken) is { } carded && carded.Id != employeeId)
                throw new DomainException($"{carded.Name} has national ID {card} already.");
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
                employee = new Employee(employeeId, d.Name, d.JobTitle, 0, d.ShiftStartsAt, Employees.Flags(d.DaysOff), d.HiredOn, d.DeviceUserId);
                employee.SetPay(d.EmploymentType, d.Pay);
                employee.MoveTo(d.Department);
                employee.AssignRole(await RoleAsync(d, cancellationToken));
                employee.SetNationalId(d.NationalId);
                employee.SetMobileNumber(d.MobileNumber);
                employees.Add(employee);
            }
            else
            {
                employee.Rename(d.Name);
                employee.SetJobTitle(d.JobTitle);
                employee.SetPay(d.EmploymentType, d.Pay);
                employee.MoveTo(d.Department);
                employee.AssignRole(await RoleAsync(d, cancellationToken));
                employee.SetShift(d.ShiftStartsAt);
                employee.SetDaysOff(Employees.Flags(d.DaysOff));
                employee.ChangeStart(d.HiredOn);
                employee.LinkDevice(d.DeviceUserId);
                employee.SetNationalId(d.NationalId);
                employee.SetMobileNumber(d.MobileNumber);
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

    // The role chosen, one of their department's; or, given only a title, the department's role of
    // that name, made (for the caller to save) when there is none.
    private async Task<JobRole> RoleAsync(EmployeeDetails d, CancellationToken cancellationToken)
    {
        if (d.JobRoleId is { } id)
            return Check(await roles.GetByIdAsync(id, cancellationToken), d);
        var title = d.JobTitle?.Trim() ?? string.Empty;
        var named = (await roles.GetInDepartmentAsync(d.Department, cancellationToken))
            .FirstOrDefault(r => string.Equals(r.Name, title, StringComparison.CurrentCultureIgnoreCase));
        if (named is not null)
            return named;
        var made = new JobRole(Guid.NewGuid(), d.Department, title);
        roles.Add(made);
        return made;
    }

    private static JobRole Check(JobRole? role, EmployeeDetails d)
    {
        if (role is null)
            throw new DomainException("That role is not there any more: choose another.");
        if (role.DepartmentId != d.Department)
            throw new DomainException($"The {role.Name} role is another department's: choose one of theirs.");
        return role;
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

        var shift = d.ShiftStartsAt is { } start ? $"from {start.ToString("HH:mm", Invariant)}" : "flexible hours";
        var names = (await departments.GetAllAsync(cancellationToken)).ToDictionary(x => x.Id, x => x.Name);
        string Department(Guid id) => names.GetValueOrDefault(id) ?? "—";
        Line("Name", existing?.Name, d.Name.Trim());
        Line("Job title", existing?.JobTitle, d.JobTitle.Trim());
        Line("Department", existing is null ? null : Department(existing.DepartmentId), Department(d.Department));
        Line("Pay", existing is null ? null : Pay(existing.EmploymentType, existing.IsDaily ? existing.DailyWage : existing.MonthlySalary), Pay(d.EmploymentType, d.Pay));
        Line("Shift", existing is null ? null : Shift(existing.ShiftStartsAt), Shift(d.ShiftStartsAt));
        Line("Days off", existing is null ? null : DaysOff(existing.DaysOff), DaysOff(Employees.Flags(d.DaysOff)));
        Line("Started on", existing is null ? null : Day(existing.HiredOn), Day(d.HiredOn));
        Line("Attendance device number", existing?.DeviceUserId ?? "—", string.IsNullOrWhiteSpace(d.DeviceUserId) ? "—" : d.DeviceUserId.Trim());
        // As they will be kept: "٠١٠ ١٢٣" is 010123. Said for someone new only when entered.
        var draft = new Employee(employeeId, "draft", "draft", 0, null, WeekDays.None, d.HiredOn);
        draft.SetNationalId(d.NationalId);
        draft.SetMobileNumber(d.MobileNumber);
        if (existing is not null || draft.NationalId is not null)
            Line("National ID", existing?.NationalId ?? "—", draft.NationalId ?? "—");
        if (existing is not null || draft.MobileNumber is not null)
            Line("Mobile", existing?.MobileNumber ?? "—", draft.MobileNumber ?? "—");
        return existing is null
            ? ($"New employee · {d.Name.Trim()} · {d.JobTitle.Trim()} · {Pay(d.EmploymentType, d.Pay)}, {shift}", ApprovalAction.Create, lines)
            : ($"Employee changed · {existing.Name}", ApprovalAction.Update, lines);
    }

    private static string Shift(TimeOnly? start) =>
        start is { } s ? $"{s.ToString("HH:mm", Invariant)}–{s.Add(Employee.ShiftLength).ToString("HH:mm", Invariant)}" : "Flexible hours";

    private static string DaysOff(WeekDays days) =>
        days == WeekDays.None ? "None" : string.Join(", ", Employees.DaysOf(days).Select(d => Invariant.DateTimeFormat.GetAbbreviatedDayName(d)));

    private static string Money(decimal value) => $"L.E {value.ToString("N2", Invariant)}";

    // "L.E 5,400.00 a month", or "L.E 250.00 a day" for daily pay.
    private static string Pay(EmploymentType type, decimal amount) => $"{Money(amount)} {(type == EmploymentType.Daily ? "a day" : "a month")}";

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

public sealed record GetEmployeeQuery(Guid EmployeeId) : IRequest<EmployeeDto?>;

internal sealed class GetEmployeeHandler(IEmployeeRepository employees) : IRequestHandler<GetEmployeeQuery, EmployeeDto?>
{
    public async Task<EmployeeDto?> Handle(GetEmployeeQuery request, CancellationToken cancellationToken) =>
        await employees.GetByIdAsync(request.EmployeeId, cancellationToken) is { } e ? EmployeeDto.From(e) : null;
}

// Their ID card, photographed or scanned (صورة البطاقة): a JPEG, PNG, WebP or PDF of up to 5 MB,
// replacing the one there was. Not money, so it is not asked of a manager: whoever manages HR
// uploads it.
public sealed record UploadEmployeeIdPhotoCommand(Guid EmployeeId, string FileName, string ContentType, byte[] Content, string StaffName)
    : IRequest<EmployeeDto>;

internal sealed class UploadEmployeeIdPhotoHandler(IEmployeeRepository employees, IEmployeeIdPhotoStore photos, IUnitOfWork unitOfWork)
    : IRequestHandler<UploadEmployeeIdPhotoCommand, EmployeeDto>
{
    public async Task<EmployeeDto> Handle(UploadEmployeeIdPhotoCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetByIdAsync(request.EmployeeId, cancellationToken)
            ?? throw new NotFoundException(nameof(Employee), request.EmployeeId);
        // A type it cannot be is said as such (by AttachIdPhoto); one it can be, checked by its bytes.
        if (Employee.IdPhotoTypes.Contains(request.ContentType) && !UploadedFile.LooksLike(request.ContentType, request.Content))
            throw new DomainException("That file is not the photo or PDF it says it is.");
        employee.AttachIdPhoto(request.FileName, request.ContentType, request.Content.Length, request.StaffName);
        photos.Put(employee.Id, request.Content);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return EmployeeDto.From(employee);
    }
}

// Taken off: the wrong card, say. A new one is uploaded over the old without this.
public sealed record RemoveEmployeeIdPhotoCommand(Guid EmployeeId) : IRequest<EmployeeDto>;

internal sealed class RemoveEmployeeIdPhotoHandler(IEmployeeRepository employees, IEmployeeIdPhotoStore photos, IUnitOfWork unitOfWork)
    : IRequestHandler<RemoveEmployeeIdPhotoCommand, EmployeeDto>
{
    public async Task<EmployeeDto> Handle(RemoveEmployeeIdPhotoCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetByIdAsync(request.EmployeeId, cancellationToken)
            ?? throw new NotFoundException(nameof(Employee), request.EmployeeId);
        employee.RemoveIdPhoto();
        await photos.RemoveAsync(employee.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return EmployeeDto.From(employee);
    }
}

public sealed record EmployeeIdPhotoFile(string FileName, string ContentType, byte[] Content);

public sealed record GetEmployeeIdPhotoQuery(Guid EmployeeId) : IRequest<EmployeeIdPhotoFile?>;

internal sealed class GetEmployeeIdPhotoHandler(IEmployeeRepository employees, IEmployeeIdPhotoStore photos)
    : IRequestHandler<GetEmployeeIdPhotoQuery, EmployeeIdPhotoFile?>
{
    public async Task<EmployeeIdPhotoFile?> Handle(GetEmployeeIdPhotoQuery request, CancellationToken cancellationToken)
    {
        if (await employees.GetByIdAsync(request.EmployeeId, cancellationToken) is not { HasIdPhoto: true } employee)
            return null;
        return await photos.GetAsync(employee.Id, cancellationToken) is { } content
            ? new EmployeeIdPhotoFile(employee.IdPhotoFileName!, employee.IdPhotoContentType!, content)
            : null;
    }
}
