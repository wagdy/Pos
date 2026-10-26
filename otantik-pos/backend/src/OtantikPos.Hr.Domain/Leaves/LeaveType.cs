using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Leaves;

// A kind of leave (نوع الإجازة): annual, sick, casual, unpaid. Paid leave is a paid day, not an
// absence, taken from the employee's balance of it, which opens each year at DefaultDaysPerYear.
// Unpaid leave takes a day's pay instead, never counted as an absence, and has no balance to run
// out: it has no days a year. One shared for new hires (ProratedForNewHires: annual leave) gives
// someone hired during a year only their share of its days, by the days of the year left.
public sealed class LeaveType : AggregateRoot
{
    public const int MaxNameLength = 100;
    public const int MaxDaysPerYear = 365;

    private LeaveType() { }

    public LeaveType(Guid id, string name, bool isPaid, int defaultDaysPerYear, bool proratedForNewHires = false)
    {
        Id = id;
        Rename(name);
        Change(isPaid, defaultDaysPerYear, proratedForNewHires);
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Name { get; private set; } = string.Empty;
    public bool IsPaid { get; private set; }
    public int DefaultDaysPerYear { get; private set; }
    public bool ProratedForNewHires { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the leave type its name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the leave type's name under {MaxNameLength} characters.");
        Name = trimmed;
    }

    // From the next balance opened: those of a year already open keep the days they were given.
    public void Change(bool isPaid, int defaultDaysPerYear, bool proratedForNewHires = false)
    {
        if (defaultDaysPerYear is < 0 or > MaxDaysPerYear)
            throw new DomainException($"A year gives 0 to {MaxDaysPerYear} days of a leave.");
        if (!isPaid && defaultDaysPerYear != 0)
            throw new DomainException("Unpaid leave has no days a year: it takes a day's pay instead, as many as are asked.");
        IsPaid = isPaid;
        DefaultDaysPerYear = defaultDaysPerYear;
        ProratedForNewHires = isPaid && proratedForNewHires;
    }
}
