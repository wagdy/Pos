using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Employees;

// A department of the restaurant (الأقسام): the kitchen, service, management. Every employee is in
// one. General is always there, the one employees start in, so it is renamed but never deleted;
// another is deleted once its employees are moved to a department that stays.
public sealed class Department : AggregateRoot
{
    public const int MaxNameLength = 100;

    // Made with the table, for the employees already entered then.
    public static readonly Guid GeneralId = new("7a1e3c4d-0000-4000-8000-000000000001");

    private Department() { }

    public Department(Guid id, string name)
    {
        Id = id;
        Rename(name);
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Name { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public bool IsGeneral => Id == GeneralId;

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the department its name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the department's name under {MaxNameLength} characters.");
        Name = trimmed;
    }
}
