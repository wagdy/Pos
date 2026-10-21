using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

public enum PunchSource
{
    // Pushed by an attendance device.
    Device = 1,

    // Entered by a manager for a punch the device missed: a forgotten scan, the device down.
    Manual = 2,
}

// One punch (بصمة), as the device sent it or a manager entered it: the raw record the timesheets
// are worked out from. Kept as it came: a punch that should not count (a scan made twice, a test)
// is ignored, with who did it and why, never changed or removed.
//
// The time is the device's own clock, local time, as it shows on its screen; the punches are
// paired in order, so the In/Out key pressed on the device (DeviceStatus) is kept but not relied
// on: staff forget to press it.
public sealed class AttendanceLog : AggregateRoot
{
    public const int MaxReasonLength = 300;

    private AttendanceLog() { }

    private AttendanceLog(PunchSource source, Guid? employeeId, DateTime punchedAt)
    {
        if (punchedAt.Year is < 2000 or > 2100)
            throw new DomainException("A punch's time is its date and time on the device's clock.");
        Source = source;
        EmployeeId = employeeId;
        // To the second, and local: it is the clock on the wall, not a moment in UTC.
        PunchedAt = DateTime.SpecifyKind(new DateTime(punchedAt.Ticks - punchedAt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
        ReceivedAtUtc = DateTime.UtcNow;
    }

    public PunchSource Source { get; private set; }

    // The device it came from, and the user number it was made under there.
    public string? DeviceSerialNumber { get; private set; }
    public string? DeviceUserId { get; private set; }

    // Who it is, once the user number is linked to an employee; a punch from a number no one has
    // yet waits for that, and is counted then.
    public Guid? EmployeeId { get; private set; }

    public DateTime PunchedAt { get; private set; }

    // As the device sent them: the state key (0 in, 1 out, 2 break out, 3 break in, 4 overtime in,
    // 5 overtime out) and how they were recognised (1 fingerprint, 15 face...).
    public int? DeviceStatus { get; private set; }
    public int? VerifyMode { get; private set; }

    public DateTime ReceivedAtUtc { get; private set; }

    // A manual punch's: who entered it and why.
    public string? RecordedBy { get; private set; }
    public string? Reason { get; private set; }

    public DateTime? IgnoredAtUtc { get; private set; }
    public string? IgnoredBy { get; private set; }
    public string? IgnoreReason { get; private set; }
    public bool IsIgnored => IgnoredAtUtc is not null;

    // A punch that came after its month's pay was finalized, seen to by a manager: settled in a
    // later month's pay, or nothing to settle.
    public DateTime? LateSettledAtUtc { get; private set; }
    public string? LateSettledBy { get; private set; }

    // What the timesheet counts: someone's, and not ignored.
    public bool Counts => EmployeeId is not null && !IsIgnored;

    public static AttendanceLog FromDevice(
        string serialNumber, string deviceUserId, DateTime punchedAt, int? deviceStatus, int? verifyMode, Guid? employeeId)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(deviceUserId))
            throw new DomainException("A device's punch names the device and the user number it was made under.");
        return new AttendanceLog(PunchSource.Device, employeeId, punchedAt)
        {
            DeviceSerialNumber = serialNumber.Trim(),
            DeviceUserId = deviceUserId.Trim(),
            DeviceStatus = deviceStatus,
            VerifyMode = verifyMode,
        };
    }

    // Its id is made by the till, so a retried one is entered once.
    public static AttendanceLog Manual(Guid id, Guid employeeId, DateTime punchedAt, string reason, string recordedBy) =>
        new(PunchSource.Manual, employeeId, punchedAt)
        {
            Id = id,
            Reason = Required(reason, "Say why the punch is entered by hand: the device was down, they forgot to scan."),
            RecordedBy = recordedBy,
        };

    public void SettleLate(string settledBy)
    {
        LateSettledAtUtc ??= DateTime.UtcNow;
        LateSettledBy ??= settledBy;
    }

    // A punch from a user number nobody had, now someone's.
    public void AssignTo(Guid employeeId)
    {
        if (EmployeeId is { } current && current != employeeId)
            throw new DomainException("This punch is already someone else's.");
        EmployeeId = employeeId;
    }

    public void Ignore(string reason, string ignoredBy)
    {
        if (IsIgnored)
            throw new DomainException("This punch is already ignored.");
        IgnoreReason = Required(reason, "Say why the punch should not count: scanned twice, a test.");
        IgnoredBy = ignoredBy;
        IgnoredAtUtc = DateTime.UtcNow;
    }

    public void Restore()
    {
        IgnoredAtUtc = null;
        IgnoredBy = null;
        IgnoreReason = null;
    }

    private static string Required(string text, string message)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException(message);
        if (trimmed.Length > MaxReasonLength)
            throw new DomainException($"Keep the reason under {MaxReasonLength} characters.");
        return trimmed;
    }
}
