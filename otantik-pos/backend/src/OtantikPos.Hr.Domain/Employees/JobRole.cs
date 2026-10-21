using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Employees;

// A job role in a department (الوظيفة): the kitchen's chef and helper, service's waiter and
// captain. An employee holds one of their department's roles, and is titled by its name. One name
// to a department; a role is renamed for everyone holding it, and deleted once they hold another.
public sealed class JobRole : AggregateRoot
{
    public const int MaxNameLength = Employee.MaxJobTitleLength;

    private JobRole() { }

    public JobRole(Guid id, Guid departmentId, string name)
    {
        Id = id;
        MoveTo(departmentId);
        Rename(name);
        CreatedAtUtc = DateTime.UtcNow;
    }

    public Guid DepartmentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the role its name: chef, waiter, cashier.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the role's name under {MaxNameLength} characters.");
        Name = trimmed;
    }

    // With its department's employees, when that department is deleted.
    public void MoveTo(Guid departmentId)
    {
        if (departmentId == Guid.Empty)
            throw new DomainException("A role is in a department.");
        DepartmentId = departmentId;
    }
}
