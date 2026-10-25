using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Approvals;

namespace OtantikPos.Inventory.Application.Hr;

// The employee self-service portal (البوابة): an employee signs in with their mobile number and a
// PIN a manager set, and sees, read-only, their own details, payslips and custody; nothing else.
// The PIN is kept hashed; five misses lock it five minutes.

// Hashes a PIN and checks one against its hash; a newer hash when the scheme has moved on.
public interface IPinHasher
{
    string Hash(string pin);

    PinCheck Verify(string hash, string pin);

    // A hash of no one's PIN, checked when there is no one to check against, so a number that is
    // nobody's takes as long to refuse as a wrong PIN: how fast the answer comes does not say
    // whose numbers are known.
    string NobodysHash { get; }
}

public enum PinCheck
{
    Wrong = 1,
    Right = 2,
    RightRehash = 3,
}

// A manager's: a new PIN (4 to 6 digits), unlocking them; or none, and they cannot sign in.
public sealed record SetEmployeePinCommand(Guid EmployeeId, string? Pin) : IRequest<EmployeeDto>;

internal sealed class SetEmployeePinHandler(IEmployeeRepository employees, IPinHasher hasher, IUnitOfWork unitOfWork)
    : IRequestHandler<SetEmployeePinCommand, EmployeeDto>
{
    public async Task<EmployeeDto> Handle(SetEmployeePinCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetByIdAsync(request.EmployeeId, cancellationToken) ?? throw new NotFoundException(nameof(Employee), request.EmployeeId);
        if (request.Pin is null)
            employee.ClearPin();
        else
        {
            if (!employee.IsActive)
                throw new DomainException($"{employee.Name} has left: bring them back first.");
            employee.SetPinHash(hasher.Hash(Employee.Pin(request.Pin)));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return EmployeeDto.From(employee);
    }
}

// Who signed in, as the token carries them: PinSetAtUtc stamps it, so a new PIN signs them out.
public sealed record PortalEmployee(Guid Id, string Name, DateTime PinSetAtUtc);

// Employee null: refused, LockedUntilUtc set when it is locked.
public sealed record EmployeeSignInResult(PortalEmployee? Employee, DateTime? LockedUntilUtc);

public sealed record EmployeeSignInCommand(string? MobileNumber, string? Pin) : IRequest<EmployeeSignInResult>;

internal sealed class EmployeeSignInHandler(IEmployeeRepository employees, IPinHasher hasher, IUnitOfWork unitOfWork)
    : IRequestHandler<EmployeeSignInCommand, EmployeeSignInResult>
{
    public async Task<EmployeeSignInResult> Handle(EmployeeSignInCommand request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var mobile = Employee.Mobile(request.MobileNumber);
        var employee = mobile is null ? null : await employees.GetByMobileNumberAsync(mobile, cancellationToken);
        if (employee is not { IsActive: true, PinHash: { } hash })
        {
            hasher.Verify(hasher.NobodysHash, request.Pin ?? string.Empty);
            return new EmployeeSignInResult(null, null);
        }

        if (employee.IsPinLockedAt(now))
            return new EmployeeSignInResult(null, employee.PinLockedUntilUtc);

        var check = hasher.Verify(hash, request.Pin?.Trim() ?? string.Empty);
        if (check == PinCheck.Wrong)
        {
            employee.PinRefused(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new EmployeeSignInResult(null, employee.IsPinLockedAt(now) ? employee.PinLockedUntilUtc : null);
        }

        employee.PinAccepted(check == PinCheck.RightRehash ? hasher.Hash(request.Pin!.Trim()) : null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new EmployeeSignInResult(new PortalEmployee(employee.Id, employee.Name, employee.PinSetAtUtc!.Value), null);
    }
}

// What the portal shows of them: who they are and their work; nothing of anyone else.
public sealed record PortalProfileDto(
    Guid Id,
    string Name,
    string JobTitle,
    string Department,
    EmploymentType EmploymentType,
    DateOnly HiredOn,
    string? NationalId,
    string? MobileNumber,
    EmployeeIdPhotoDto? IdPhoto,
    TimeOnly? ShiftStartsAt,
    TimeOnly? ShiftEndsAt,
    IReadOnlyList<DayOfWeek> DaysOff);

public sealed record GetPortalProfileQuery(Guid EmployeeId) : IRequest<PortalProfileDto?>;

internal sealed class GetPortalProfileHandler(IEmployeeRepository employees, IDepartmentRepository departments)
    : IRequestHandler<GetPortalProfileQuery, PortalProfileDto?>
{
    public async Task<PortalProfileDto?> Handle(GetPortalProfileQuery request, CancellationToken cancellationToken)
    {
        if (await employees.GetByIdAsync(request.EmployeeId, cancellationToken) is not { } e)
            return null;
        var dto = EmployeeDto.From(e);
        var department = (await departments.GetByIdAsync(e.DepartmentId, cancellationToken))?.Name ?? "—";
        return new PortalProfileDto(
            e.Id, e.Name, e.JobTitle, department, e.EmploymentType, e.HiredOn, e.NationalId, e.MobileNumber, dto.IdPhoto, dto.ShiftStartsAt, dto.ShiftEndsAt,
            dto.DaysOff);
    }
}

// The months there is a payslip for, the latest first: from the month they started to the
// business month, two years at most.
public sealed record GetPortalMonthsQuery(Guid EmployeeId) : IRequest<IReadOnlyList<DateOnly>>;

internal sealed class GetPortalMonthsHandler(IEmployeeRepository employees, IRecordDescriptions clock)
    : IRequestHandler<GetPortalMonthsQuery, IReadOnlyList<DateOnly>>
{
    public async Task<IReadOnlyList<DateOnly>> Handle(GetPortalMonthsQuery request, CancellationToken cancellationToken) =>
        await employees.GetByIdAsync(request.EmployeeId, cancellationToken) is { } e ? Portal.Months(e, clock.BusinessDateOf(DateTime.UtcNow)) : [];
}

// Their payslip for a month: a draft as it stands now, or as it was finalized. None for a month
// they have no payslip for.
public sealed record GetPortalPayslipQuery(Guid EmployeeId, DateOnly Month) : IRequest<PayrollSlipDto?>;

internal sealed class GetPortalPayslipHandler(IEmployeeRepository employees, PayrollWorker payroll, IRecordDescriptions clock)
    : IRequestHandler<GetPortalPayslipQuery, PayrollSlipDto?>
{
    public async Task<PayrollSlipDto?> Handle(GetPortalPayslipQuery request, CancellationToken cancellationToken)
    {
        if (await employees.GetByIdAsync(request.EmployeeId, cancellationToken) is not { } e)
            return null;
        var month = new DateOnly(request.Month.Year, request.Month.Month, 1);
        if (!Portal.Months(e, clock.BusinessDateOf(DateTime.UtcNow)).Contains(month))
            return null;
        return (await payroll.WorkOutAsync(month, e.Id, cancellationToken)).Select(PayrollWorker.SlipDto).FirstOrDefault();
    }
}

internal static class Portal
{
    public const int MonthsShown = 24;

    public static IReadOnlyList<DateOnly> Months(Employee employee, DateOnly today)
    {
        var first = new DateOnly(employee.HiredOn.Year, employee.HiredOn.Month, 1);
        var last = new DateOnly(today.Year, today.Month, 1);
        if (employee.LeftOn is { } left && left < today)
            last = new DateOnly(left.Year, left.Month, 1);
        var months = new List<DateOnly>();
        for (var m = last; m >= first && months.Count < MonthsShown; m = m.AddMonths(-1))
            months.Add(m);
        return months;
    }
}
