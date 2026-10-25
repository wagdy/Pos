using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Employees;

namespace OtantikPos.Inventory.Application.Hr;

// Employee permissions (صلاحيات الموظفين): the HR & Payroll tabs a manager gives an employee, who
// works them in the portal on their phone, or an accountant, on the till. A manager's to give, and
// a manager has every tab. Whoever is given a tab works it as an accountant does: what they change
// is asked of a manager, and what only a manager does (correcting attendance, finalizing pay,
// the devices) stays a manager's.

// A till account the tabs are given to: an accountant's, read by the API from the till's accounts.
public sealed record HrStaffAccount(string Id, string Name, string Role);

// HasMobile and HasPin: whether they can sign in to the portal to use their tabs.
public sealed record EmployeeHrAccessDto(
    Guid Id, string Name, string JobTitle, string Department, bool HasMobile, bool HasPin, IReadOnlyList<HrTabs> Tabs);

// Restricted once a manager has ticked their tabs; every tab until then, as accountants have always had.
public sealed record StaffHrAccessDto(string Id, string Name, string Role, bool Restricted, IReadOnlyList<HrTabs> Tabs, string? SetBy, DateTime? SetAtUtc);

public sealed record HrAccessDto(IReadOnlyList<EmployeeHrAccessDto> Employees, IReadOnlyList<StaffHrAccessDto> Staff);

// Everyone employed, by name, and the accounts given (the accountants').
public sealed record GetHrAccessQuery(IReadOnlyList<HrStaffAccount> Staff) : IRequest<HrAccessDto>;

internal sealed class GetHrAccessHandler(IEmployeeRepository employees, IDepartmentRepository departments, IStaffHrAccessRepository access)
    : IRequestHandler<GetHrAccessQuery, HrAccessDto>
{
    public async Task<HrAccessDto> Handle(GetHrAccessQuery request, CancellationToken cancellationToken)
    {
        var names = (await departments.GetAllAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        var employed = (await employees.GetAllAsync(cancellationToken))
            .Where(e => e.IsActive)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => HrAccessDtos.Of(e, names.GetValueOrDefault(e.DepartmentId) ?? "—"))
            .ToList();
        var rows = (await access.GetAllAsync(cancellationToken)).ToDictionary(a => a.StaffId);
        var staff = request.Staff
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => HrAccessDtos.Of(s, rows.GetValueOrDefault(s.Id)))
            .ToList();
        return new HrAccessDto(employed, staff);
    }
}

// An employee's tabs, from their next sign-in: changing them signs them out of the portal.
public sealed record SetEmployeeHrTabsCommand(Guid EmployeeId, HrTabs Tabs) : IRequest<EmployeeHrAccessDto>;

internal sealed class SetEmployeeHrTabsHandler(IEmployeeRepository employees, IDepartmentRepository departments, IUnitOfWork unitOfWork)
    : IRequestHandler<SetEmployeeHrTabsCommand, EmployeeHrAccessDto>
{
    public async Task<EmployeeHrAccessDto> Handle(SetEmployeeHrTabsCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetByIdAsync(request.EmployeeId, cancellationToken)
            ?? throw new NotFoundException(nameof(Employee), request.EmployeeId);
        if (!employee.IsActive && request.Tabs != HrTabs.None)
            throw new DomainException($"{employee.Name} has left: bring them back before giving them HR tabs.");
        employee.SetHrTabs(request.Tabs);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var department = (await departments.GetByIdAsync(employee.DepartmentId, cancellationToken))?.Name ?? "—";
        return HrAccessDtos.Of(employee, department);
    }
}

// An accountant's tabs: only those from now on.
public sealed record SetStaffHrTabsCommand(HrStaffAccount Account, HrTabs Tabs, string SetBy) : IRequest<StaffHrAccessDto>;

internal sealed class SetStaffHrTabsHandler(IStaffHrAccessRepository access, IUnitOfWork unitOfWork)
    : IRequestHandler<SetStaffHrTabsCommand, StaffHrAccessDto>
{
    public async Task<StaffHrAccessDto> Handle(SetStaffHrTabsCommand request, CancellationToken cancellationToken)
    {
        var row = await access.GetByStaffIdAsync(request.Account.Id, cancellationToken);
        if (row is null)
        {
            row = new StaffHrAccess(request.Account.Id, request.Tabs, request.SetBy);
            access.Add(row);
        }
        else
        {
            row.Set(request.Tabs, request.SetBy);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return HrAccessDtos.Of(request.Account, row);
    }
}

internal static class HrAccessDtos
{
    public static EmployeeHrAccessDto Of(Employee e, string department) =>
        new(e.Id, e.Name, e.JobTitle, department, e.MobileNumber is not null, e.HasPin, HrTabSet.Split(e.HrTabs));

    public static StaffHrAccessDto Of(HrStaffAccount account, StaffHrAccess? row) =>
        new(account.Id, account.Name, account.Role, row is not null, HrTabSet.Split(row?.Tabs ?? HrTabs.All), row?.SetBy, row?.SetAtUtc);
}
