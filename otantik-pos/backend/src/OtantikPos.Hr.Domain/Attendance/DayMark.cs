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

    // Unpaid leave (إجازة بدون راتب), an approved leave request's: a day's pay off, never counted
    // as an absence. Punched in after all, it is a working day like any other.
    UnpaidLeave = 4,
}

// A manager's word on one employee's day: that a day without punches was leave or a day off, not
// an absence; or that a weekly day off was a working day, swapped for another. One a day; changed
// or taken off as the manager decides.
public sealed class DayMark : AggregateRoot
{
    public const int MaxNotesLength = 300;

    private DayMark() { }

    public DayMark(Guid employeeId, DateOnly date, DayMarkKind kind, string? notes, string markedBy, Guid? leaveRequestId = null)
    {
        EmployeeId = employeeId;
        Date = date;
        Change(kind, notes, markedBy, leaveRequestId);
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public DayMarkKind Kind { get; private set; }
    public string? Notes { get; private set; }
    public string MarkedBy { get; private set; } = string.Empty;
    public DateTime MarkedAtUtc { get; private set; }
    // The approved leave request it is a day of; none for a manager's own mark.
    public Guid? LeaveRequestId { get; private set; }

    public void Change(DayMarkKind kind, string? notes, string markedBy, Guid? leaveRequestId = null)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("Say whether the day was leave, a day off or a working day.");
        if (notes?.Trim().Length > MaxNotesLength)
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");
        if (kind == DayMarkKind.UnpaidLeave && leaveRequestId is null)
            throw new DomainException("Unpaid leave is asked for and approved as a leave request.");
        Kind = kind;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        MarkedBy = markedBy;
        LeaveRequestId = leaveRequestId;
        MarkedAtUtc = DateTime.UtcNow;
    }
}
