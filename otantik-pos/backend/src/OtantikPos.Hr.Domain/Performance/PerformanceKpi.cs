using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Performance;

// A measure employees are evaluated on (مؤشر أداء): punctuality, hygiene, service. A manager's
// list; each evaluation scores the ones it takes from 1 to 5. One that evaluations have scored is
// retired rather than deleted: they keep its name, and it is not offered for new ones.
public sealed class PerformanceKpi : AggregateRoot
{
    public const int MaxNameLength = 100;

    private PerformanceKpi() { }

    public PerformanceKpi(Guid id, string name)
    {
        Id = id;
        Rename(name);
        IsActive = true;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the KPI its name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the KPI's name under {MaxNameLength} characters.");
        Name = trimmed;
    }

    public void Retire() => IsActive = false;

    public void Restore() => IsActive = true;
}
