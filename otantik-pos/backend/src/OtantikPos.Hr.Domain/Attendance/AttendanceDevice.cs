using System.Text.RegularExpressions;
using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

// An attendance device (جهاز البصمة), a ZKTeco K40 Pro, pushing its punches over the restaurant's
// network (ZKTeco's ADMS push protocol). It names itself only by its serial number, over plain
// HTTP, so one is heard from but refused until a manager approves it: a device first seen is kept
// as waiting, to be recognised in the list and approved, and nothing it sends is taken till then.
public sealed partial class AttendanceDevice : AggregateRoot
{
    public const int MaxSerialNumberLength = 64;
    public const int MaxNameLength = 100;

    private AttendanceDevice() { }

    private AttendanceDevice(string serialNumber, DateTime atUtc, string? address)
    {
        SerialNumber = serialNumber;
        FirstSeenAtUtc = atUtc;
        LastSeenAtUtc = atUtc;
        LastAddress = Address(address);
    }

    public string SerialNumber { get; private set; } = string.Empty;

    // What the manager calls it: "Kitchen door".
    public string? Name { get; private set; }

    public DateTime FirstSeenAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }

    // The network address it last called from, to tell two devices apart in the list.
    public string? LastAddress { get; private set; }

    public bool IsApproved => ApprovedAtUtc is not null;
    public DateTime? ApprovedAtUtc { get; private set; }
    public string? ApprovedBy { get; private set; }

    // The protocol's mark of the newest attendance it has handed over (ATTLOGStamp): given back
    // to it when it asks, so it sends only what came after.
    public string? AttendanceStamp { get; private set; }

    public static AttendanceDevice FirstSeen(string serialNumber, DateTime atUtc, string? address)
    {
        var serial = serialNumber?.Trim() ?? string.Empty;
        if (serial.Length == 0 || serial.Length > MaxSerialNumberLength || !SerialPattern().IsMatch(serial))
            throw new DomainException("An attendance device is known by its serial number: letters, digits and dashes.");
        return new AttendanceDevice(serial, atUtc, address);
    }

    public void Seen(DateTime atUtc, string? address)
    {
        if (atUtc > LastSeenAtUtc)
            LastSeenAtUtc = atUtc;
        LastAddress = Address(address) ?? LastAddress;
    }

    public void Approve(string name, string approvedBy)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Name the device, so it is known in the list: \"Kitchen door\".");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the device's name under {MaxNameLength} characters.");
        Name = trimmed;
        if (!IsApproved)
        {
            ApprovedAtUtc = DateTime.UtcNow;
            ApprovedBy = approvedBy;
        }
    }

    // Refused again, as a device first seen is. What it sent while approved stays.
    public void Revoke()
    {
        ApprovedAtUtc = null;
        ApprovedBy = null;
    }

    public void Acknowledge(string stamp)
    {
        var trimmed = stamp?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 32)
            throw new DomainException("A device's attendance stamp is a short number.");
        AttendanceStamp = trimmed;
    }

    private static string? Address(string? address)
    {
        var trimmed = address?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex SerialPattern();
}
