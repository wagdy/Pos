using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// HR's departments (الأقسام): the kitchen, service, management, each employee in one, holding one of
// its job roles. A manager's to add, rename and delete, and their roles too. General is always
// there; another is deleted once its employees, those who left too, are moved to one that stays,
// its roles going with them. A role is renamed for everyone holding it, and deleted once they hold
// another of the department's.

// What a role's or a department's people employed now cost: MonthlyCost their monthly salaries,
// DailyCost the daily wages of those paid by the day, apart, since their month is the days they
// work.
public sealed record JobRoleDto(Guid Id, Guid DepartmentId, string Name, int Employed, decimal MonthlyCost = 0, decimal DailyCost = 0);

// Its costs are everyone employed in it, those holding no role too.
public sealed record DepartmentDto(
    Guid Id, string Name, bool IsGeneral, int Employed, int Everyone, IReadOnlyList<JobRoleDto> Roles, decimal MonthlyCost = 0, decimal DailyCost = 0);

public sealed record GetDepartmentsQuery : IRequest<IReadOnlyList<DepartmentDto>>;

internal sealed class GetDepartmentsHandler(IDepartmentRepository departments, IJobRoleRepository roles, IEmployeeRepository employees)
    : IRequestHandler<GetDepartmentsQuery, IReadOnlyList<DepartmentDto>>
{
    public async Task<IReadOnlyList<DepartmentDto>> Handle(GetDepartmentsQuery request, CancellationToken cancellationToken)
    {
        var people = await employees.GetAllAsync(cancellationToken);
        var allRoles = await roles.GetAllAsync(cancellationToken);
        return (await departments.GetAllAsync(cancellationToken))
            .Select(d => Departments.Dto(d, people.Where(e => e.DepartmentId == d.Id).ToList(), allRoles.Where(r => r.DepartmentId == d.Id)))
            .ToList();
    }
}

internal static class Departments
{
    // `people`: the department's, those who left too.
    public static DepartmentDto Dto(Department department, IReadOnlyList<Employee> people, IEnumerable<JobRole> roles)
    {
        var (monthly, daily) = Cost(people);
        return new DepartmentDto(
            department.Id, department.Name, department.IsGeneral, people.Count(e => e.IsActive), people.Count,
            roles.OrderBy(r => r.Name).Select(r => Role(r, people)).ToList(), monthly, daily);
    }

    public static JobRoleDto Role(JobRole role, IEnumerable<Employee> people)
    {
        var holders = people.Where(e => e.JobRoleId == role.Id).ToList();
        var (monthly, daily) = Cost(holders);
        return new(role.Id, role.DepartmentId, role.Name, holders.Count(e => e.IsActive), monthly, daily);
    }

    // Of those employed now: the monthly salaries, and the daily wages.
    private static (decimal Monthly, decimal Daily) Cost(IEnumerable<Employee> people)
    {
        var employed = people.Where(e => e.IsActive).ToList();
        return (employed.Where(e => !e.IsDaily).Sum(e => e.MonthlySalary), employed.Where(e => e.IsDaily).Sum(e => e.DailyWage));
    }
}

// Added under this id, or renamed. Two departments are not named alike.
public sealed record SaveDepartmentCommand(Guid DepartmentId, string Name) : IRequest<DepartmentDto>, IRetryOnConflict;

internal sealed class SaveDepartmentHandler(IDepartmentRepository departments, IJobRoleRepository roles, IEmployeeRepository employees, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveDepartmentCommand, DepartmentDto>
{
    public async Task<DepartmentDto> Handle(SaveDepartmentCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var all = await departments.GetAllAsync(cancellationToken);
        if (all.FirstOrDefault(d => d.Id != request.DepartmentId && string.Equals(d.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            throw new DomainException($"There is a {same.Name} department already.");

        var department = await departments.GetByIdAsync(request.DepartmentId, cancellationToken);
        if (department is null)
        {
            department = new Department(request.DepartmentId, name);
            departments.Add(department);
        }
        else
        {
            department.Rename(name);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var people = await employees.GetInDepartmentAsync(department.Id, cancellationToken);
        return Departments.Dto(department, people, await roles.GetInDepartmentAsync(department.Id, cancellationToken));
    }
}

// Deleted: its employees moved first to MoveTo, when it has any, their roles with them: one the
// other department has by the same name becomes theirs there. General stays.
public sealed record DeleteDepartmentCommand(Guid DepartmentId, Guid? MoveTo) : IRequest, IRetryOnConflict;

internal sealed class DeleteDepartmentHandler(IDepartmentRepository departments, IJobRoleRepository roles, IEmployeeRepository employees, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteDepartmentCommand>
{
    public async Task Handle(DeleteDepartmentCommand request, CancellationToken cancellationToken)
    {
        var department = await departments.GetRequiredAsync(request.DepartmentId, cancellationToken);
        if (department.IsGeneral)
            throw new DomainException("General stays, as new employees start in it: rename it if you like.");
        var people = await employees.GetInDepartmentAsync(department.Id, cancellationToken);
        if (people.Count > 0)
        {
            if (request.MoveTo is not { } moveTo || moveTo == department.Id)
                throw new DomainException($"{department.Name} has {people.Count} {(people.Count == 1 ? "employee" : "employees")}: say which department they move to.");
            var target = await departments.GetByIdAsync(moveTo, cancellationToken)
                ?? throw new DomainException("The department they move to is not there any more.");
            foreach (var employee in people)
                employee.MoveTo(target.Id);
            await MoveRolesAsync(department.Id, target.Id, people, cancellationToken);
        }
        else
        {
            foreach (var role in await roles.GetInDepartmentAsync(department.Id, cancellationToken))
                roles.Remove(role);
        }

        departments.Remove(department);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // The deleted department's roles, to the department its employees moved to: kept there, or
    // the one there of the same name taken instead.
    private async Task MoveRolesAsync(Guid from, Guid to, IReadOnlyList<Employee> people, CancellationToken cancellationToken)
    {
        var theirs = await roles.GetInDepartmentAsync(to, cancellationToken);
        foreach (var role in await roles.GetInDepartmentAsync(from, cancellationToken))
        {
            var same = theirs.FirstOrDefault(r => string.Equals(r.Name, role.Name, StringComparison.CurrentCultureIgnoreCase));
            if (same is null)
            {
                role.MoveTo(to);
                foreach (var employee in people.Where(e => e.JobRoleId == role.Id))
                    employee.AssignRole(role);
            }
            else
            {
                foreach (var employee in people.Where(e => e.JobRoleId == role.Id))
                    employee.AssignRole(same);
                roles.Remove(role);
            }
        }
    }
}

// A role added to a department under this id, or renamed: for everyone holding it, their title.
// One name to a department.
public sealed record SaveJobRoleCommand(Guid RoleId, Guid DepartmentId, string Name) : IRequest<JobRoleDto>, IRetryOnConflict;

internal sealed class SaveJobRoleHandler(IDepartmentRepository departments, IJobRoleRepository roles, IEmployeeRepository employees, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveJobRoleCommand, JobRoleDto>
{
    public async Task<JobRoleDto> Handle(SaveJobRoleCommand request, CancellationToken cancellationToken)
    {
        var department = await departments.GetRequiredAsync(request.DepartmentId, cancellationToken);
        var name = request.Name?.Trim() ?? string.Empty;
        var itsRoles = await roles.GetInDepartmentAsync(department.Id, cancellationToken);
        if (itsRoles.FirstOrDefault(r => r.Id != request.RoleId && string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            throw new DomainException($"{department.Name} has a {same.Name} role already.");

        var role = await roles.GetByIdAsync(request.RoleId, cancellationToken);
        if (role is null)
        {
            role = new JobRole(request.RoleId, department.Id, name);
            roles.Add(role);
        }
        else
        {
            if (role.DepartmentId != department.Id)
                throw new DomainException($"The {role.Name} role is another department's.");
            role.Rename(name);
        }

        var holding = await employees.GetWithRoleAsync(role.Id, cancellationToken);
        foreach (var employee in holding)
            employee.AssignRole(role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Departments.Role(role, holding);
    }
}

// A role deleted: whoever holds it, those who left too, moved first to MoveTo, another of its
// department's roles.
public sealed record DeleteJobRoleCommand(Guid RoleId, Guid? MoveTo) : IRequest, IRetryOnConflict;

internal sealed class DeleteJobRoleHandler(IJobRoleRepository roles, IEmployeeRepository employees, IUnitOfWork unitOfWork) : IRequestHandler<DeleteJobRoleCommand>
{
    public async Task Handle(DeleteJobRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await roles.GetRequiredAsync(request.RoleId, cancellationToken);
        var holding = await employees.GetWithRoleAsync(role.Id, cancellationToken);
        if (holding.Count > 0)
        {
            if (request.MoveTo is not { } moveTo || moveTo == role.Id)
                throw new DomainException($"{holding.Count} {(holding.Count == 1 ? "employee holds" : "employees hold")} the {role.Name} role: say which role they take instead.");
            var target = await roles.GetByIdAsync(moveTo, cancellationToken)
                ?? throw new DomainException("The role they take instead is not there any more.");
            if (target.DepartmentId != role.DepartmentId)
                throw new DomainException("They take another of the same department's roles.");
            foreach (var employee in holding)
                employee.AssignRole(target);
        }

        roles.Remove(role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
