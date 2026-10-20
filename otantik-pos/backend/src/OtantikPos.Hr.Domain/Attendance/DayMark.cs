using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

public enum DayMarkKind
{
    // Paid leave (إجازة): not an absence.
    Leave = 1,

    // A day off outside their weekly ones, one swapped for another: not an absence.
    DayOff = 2,

    // A weekly day off worked in exchange for another day off: a normal shift, late and overtime
    // counted, and no leave allowance. The day taken instead is marked DayOff.
    WorkingDay = 3,
}

// A manager's word on one employee's day: that a day without punches was leave or a day off, not
// an absence; or that a weekly day off was a working day, swapped for another. One a day; changed
// or taken off as the manager decides.
public sealed class DayMark : AggregateRoot
{
    public const int MaxNotesLength = 300;

    private DayMark() { }

    public DayMark(Guid employeeId, DateOnly date, DayMarkKind kind, string? notes, string markedBy)
    {
        EmployeeId = employeeId;
        Date = date;
        Change(kind, notes, markedBy);
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public DayMarkKind Kind { get; private set; }
    public string? Notes { get; private set; }
    public string MarkedBy { get; private set; } = string.Empty;
    public DateTime MarkedAtUtc { get; private set; }

    public void Change(DayMarkKind kind, string? notes, string markedBy)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("Say whether the day was leave, a day off or a working day.");
        if (notes?.Trim().Length > MaxNotesLength)
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");
        Kind = kind;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        MarkedBy = markedBy;
        MarkedAtUtc = DateTime.UtcNow;
    }
}
