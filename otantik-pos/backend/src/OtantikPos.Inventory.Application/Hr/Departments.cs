using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// HR's departments (الأقسام): the kitchen, service, management, each employee in one. A manager's
// to add, rename and delete. General is always there; another is deleted once its employees,
// those who left too, are moved to one that stays.

public sealed record DepartmentDto(Guid Id, string Name, bool IsGeneral, int Employed, int Everyone);

public sealed record GetDepartmentsQuery : IRequest<IReadOnlyList<DepartmentDto>>;

internal sealed class GetDepartmentsHandler(IDepartmentRepository departments, IEmployeeRepository employees)
    : IRequestHandler<GetDepartmentsQuery, IReadOnlyList<DepartmentDto>>
{
    public async Task<IReadOnlyList<DepartmentDto>> Handle(GetDepartmentsQuery request, CancellationToken cancellationToken)
    {
        var people = await employees.GetAllAsync(cancellationToken);
        return (await departments.GetAllAsync(cancellationToken))
            .Select(d => new DepartmentDto(
                d.Id, d.Name, d.IsGeneral, people.Count(e => e.DepartmentId == d.Id && e.IsActive), people.Count(e => e.DepartmentId == d.Id)))
            .ToList();
    }
}

// Added under this id, or renamed. Two departments are not named alike.
public sealed record SaveDepartmentCommand(Guid DepartmentId, string Name) : IRequest<DepartmentDto>, IRetryOnConflict;

internal sealed class SaveDepartmentHandler(IDepartmentRepository departments, IEmployeeRepository employees, IUnitOfWork unitOfWork)
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
        return new DepartmentDto(department.Id, department.Name, department.IsGeneral, people.Count(e => e.IsActive), people.Count);
    }
}

// Deleted: its employees moved first to MoveTo, when it has any. General stays.
public sealed record DeleteDepartmentCommand(Guid DepartmentId, Guid? MoveTo) : IRequest, IRetryOnConflict;

internal sealed class DeleteDepartmentHandler(IDepartmentRepository departments, IEmployeeRepository employees, IUnitOfWork unitOfWork)
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
        }

        departments.Remove(department);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
