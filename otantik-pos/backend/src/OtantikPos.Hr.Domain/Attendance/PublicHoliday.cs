using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

// A public holiday (عطلة رسمية): its name and the days it runs, one or several (the Eids). It is
// everyone's: a paid day off, never an absence. Whoever works it is measured as on any other day
// and earns a holiday allowance on top (PayrollCalculator). Entered by a manager each year, as the
// government sets the dates, and not two on one day.
public sealed class PublicHoliday : AggregateRoot
{
    public const int MaxNameLength = 100;
    public const int MaxDays = 10;

    private PublicHoliday() { }

    public PublicHoliday(Guid id, string name, DateOnly firstDay, DateOnly lastDay, string setBy)
    {
        Id = id;
        Change(name, firstDay, lastDay, setBy);
    }

    public string Name { get; private set; } = string.Empty;
    public DateOnly FirstDay { get; private set; }
    public DateOnly LastDay { get; private set; }
    public string SetBy { get; private set; } = string.Empty;
    public DateTime SetAtUtc { get; private set; }

    public int Days => LastDay.DayNumber - FirstDay.DayNumber + 1;

    public bool Covers(DateOnly day) => day >= FirstDay && day <= LastDay;

    public bool Overlaps(DateOnly firstDay, DateOnly lastDay) => firstDay <= LastDay && lastDay >= FirstDay;

    public void Change(string name, DateOnly firstDay, DateOnly lastDay, string setBy)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the holiday its name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the holiday's name under {MaxNameLength} characters.");
        if (lastDay < firstDay)
            throw new DomainException("The holiday's last day comes on or after its first.");
        if (lastDay.DayNumber - firstDay.DayNumber + 1 > MaxDays)
            throw new DomainException($"A holiday runs {MaxDays} days at most: enter a longer one as two.");
        Name = trimmed;
        FirstDay = firstDay;
        LastDay = lastDay;
        SetBy = setBy;
        SetAtUtc = DateTime.UtcNow;
    }
}
