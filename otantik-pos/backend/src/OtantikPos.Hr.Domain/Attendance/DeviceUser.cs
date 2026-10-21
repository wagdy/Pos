using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

// Someone enrolled on an attendance device, as it told the till when asked (USERINFO): their user
// number there and the name typed on it, for a manager to make an employee of or link to one.
// Only these: never their device password, card number, or fingerprint and face templates.
public sealed class DeviceUser : AggregateRoot
{
    public const int MaxNameLength = 100;
    public const int MaxDeviceUserIdLength = 24;

    private DeviceUser() { }

    public DeviceUser(string serialNumber, string deviceUserId, string? name, DateTime seenAtUtc)
    {
        var number = deviceUserId?.Trim() ?? string.Empty;
        if (number.Length is 0 or > MaxDeviceUserIdLength)
            throw new DomainException("A device user has a number.");
        DeviceSerialNumber = serialNumber;
        DeviceUserId = number;
        FirstSeenAtUtc = seenAtUtc;
        Seen(name, seenAtUtc);
    }

    public string DeviceSerialNumber { get; private set; } = string.Empty;
    public string DeviceUserId { get; private set; } = string.Empty;

    // As typed on the device: may be empty, or cut short there.
    public string Name { get; private set; } = string.Empty;
    public DateTime FirstSeenAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }

    public void Seen(string? name, DateTime seenAtUtc)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        Name = trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
        LastSeenAtUtc = seenAtUtc;
    }
}
