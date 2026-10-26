using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Leaves;

public enum LeaveRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,

    // Approved, then cancelled by a manager before it began: none of its days taken.
    Cancelled = 4,
}

// A leave asked for (طلب إجازة): its dates, within one year, and the working days they hold
// (TotalDaysRequested: neither a weekly day off of theirs nor a public holiday, LeaveDays). It
// waits for a manager, who approves it, its days then taken from the balance and marked leave on
// the timesheet, or rejects it, with their notes. DecidedById is the manager's till account,
// whichever they decided. An approved one can be cancelled by a manager from a day on (the
// employee recalled, the trip off): its days from then are given back and no longer leave, those
// before stay taken; cancelled from its first day or before, none of it stands (Cancelled).
public sealed class LeaveRequest : AggregateRoot
{
    public const int MaxReasonLength = 500;
    public const int MaxNotesLength = 500;
    public const int MaxDays = 90;

    private LeaveRequest() { }

    public LeaveRequest(
        Guid id, Guid employeeId, Guid leaveTypeId, DateOnly startDate, DateOnly endDate, int totalDaysRequested, string? reason, string requestedBy)
    {
        if (endDate < startDate)
            throw new DomainException("A leave ends on or after the day it starts.");
        if (endDate.Year != startDate.Year)
            throw new DomainException("A leave runs within one year: ask for each year's days apart.");
        if (endDate.DayNumber - startDate.DayNumber + 1 > MaxDays)
            throw new DomainException($"A leave runs {MaxDays} days at most: ask for a longer one in parts.");
        if (totalDaysRequested <= 0)
            throw new DomainException("Those dates hold no working day of theirs: there is nothing to take.");
        if (totalDaysRequested > endDate.DayNumber - startDate.DayNumber + 1)
            throw new DomainException("A leave takes no more days than its dates hold.");
        Id = id;
        EmployeeId = employeeId;
        LeaveTypeId = leaveTypeId;
        StartDate = startDate;
        EndDate = endDate;
        TotalDaysRequested = totalDaysRequested;
        Reason = Text(reason, MaxReasonLength, "reason");
        RequestedBy = requestedBy;
        RequestedAtUtc = DateTime.UtcNow;
        Status = LeaveRequestStatus.Pending;
    }

    public Guid EmployeeId { get; private set; }
    public Guid LeaveTypeId { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public int TotalDaysRequested { get; private set; }
    public LeaveRequestStatus Status { get; private set; }
    public string? Reason { get; private set; }
    public string? ManagerNotes { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAtUtc { get; private set; }
    public string? DecidedById { get; private set; }
    public string? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    // Cancelled from this day on, and the days given back; by whom, when, and why.
    public DateOnly? CancelledFrom { get; private set; }
    public int CancelledDays { get; private set; }
    public string? CancelledById { get; private set; }
    public string? CancelledBy { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancellationNotes { get; private set; }

    // What it takes now: its days, less any given back.
    public int DaysTaken => TotalDaysRequested - CancelledDays;

    public int Year => StartDate.Year;

    public bool IsPending => Status == LeaveRequestStatus.Pending;

    public bool Overlaps(DateOnly startDate, DateOnly endDate) => startDate <= EndDate && endDate >= StartDate;

    public void Approve(string decidedById, string decidedBy, string? notes) => Decide(LeaveRequestStatus.Approved, decidedById, decidedBy, notes);

    public void Reject(string decidedById, string decidedBy, string? notes) => Decide(LeaveRequestStatus.Rejected, decidedById, decidedBy, notes);

    // From `from` on: `givenBack` of its days returned. Its days before `from` stay leave, unless
    // `from` is its first day or before, when none of it stands.
    public void Cancel(DateOnly from, int givenBack, string cancelledById, string cancelledBy, string? notes)
    {
        if (Status != LeaveRequestStatus.Approved)
            throw new DomainException(IsPending ? "This leave waits for a decision: reject it instead." : $"This leave was {Status.ToString().ToLowerInvariant()} already.");
        if (CancelledFrom is not null)
            throw new DomainException("This leave was cut short already.");
        if (from > EndDate)
            throw new DomainException($"It ended on {EndDate:d MMM yyyy}: there is nothing of it left to cancel.");
        if (givenBack < 0 || givenBack > TotalDaysRequested)
            throw new DomainException("Give back no more days than it takes.");
        if (string.IsNullOrWhiteSpace(cancelledById))
            throw new DomainException("Say who cancelled it.");
        var whole = from <= StartDate;
        if (whole && givenBack != TotalDaysRequested)
            throw new DomainException("Cancelled before it began, all its days are given back.");
        CancelledFrom = whole ? StartDate : from;
        CancelledDays = givenBack;
        CancelledById = cancelledById;
        CancelledBy = cancelledBy;
        CancelledAtUtc = DateTime.UtcNow;
        CancellationNotes = Text(notes, MaxNotesLength, "notes");
        if (whole)
            Status = LeaveRequestStatus.Cancelled;
    }

    private void Decide(LeaveRequestStatus status, string decidedById, string decidedBy, string? notes)
    {
        if (!IsPending)
            throw new DomainException($"This leave was {Status.ToString().ToLowerInvariant()} already.");
        if (string.IsNullOrWhiteSpace(decidedById))
            throw new DomainException("Say who decided it.");
        Status = status;
        ManagerNotes = Text(notes, MaxNotesLength, "notes");
        DecidedById = decidedById;
        DecidedBy = decidedBy;
        DecidedAtUtc = DateTime.UtcNow;
    }

    private static string? Text(string? text, int max, string what)
    {
        var trimmed = text?.Trim();
        if (trimmed is { Length: > 0 } && trimmed.Length > max)
            throw new DomainException($"Keep the {what} under {max} characters.");
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
