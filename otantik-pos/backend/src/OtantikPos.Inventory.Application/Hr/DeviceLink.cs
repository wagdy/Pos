using System.Net;
using System.Net.Sockets;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// An attendance device reached by the till, over its own protocol (ZKTeco's, port 4370), beside
// ADMS where the device calls the till: a manager tests that the till can reach it, and reads its
// users and its attendance log at once. What is read is kept as ADMS keeps it: its punches once
// each, its users by number and name for the Employees tab. A device read for the first time is
// approved by that: a manager gave its address and asked.

public interface IAttendanceDeviceLink
{
    // Whether it answers, and what it says it is; never throws for a device that does not.
    Task<DeviceProbe> ProbeAsync(string address, int port, int commKey, CancellationToken cancellationToken);

    // Its serial number, model, users and whole attendance log. A DomainException, said plainly,
    // when it cannot be read.
    Task<DeviceReading> ReadAsync(string address, int port, int commKey, CancellationToken cancellationToken);
}

public sealed record DeviceProbe(
    bool Reached, string Message, string? SerialNumber = null, string? Model = null, string? Firmware = null, int? Users = null, int? Records = null);

public sealed record DeviceReading(string SerialNumber, string? Model, IReadOnlyList<DeviceUserLine> Users, IReadOnlyList<DevicePunch> Punches);

public sealed record DeviceSyncResult(string SerialNumber, string DeviceName, bool NewDevice, int Users, int UsersNotOnTheTill, int Punches, int NewPunches);

internal static class DeviceAddresses
{
    // An address on the restaurant's own network, or this machine: an IPv4 address in the
    // private ranges, link-local or loopback. The till does not reach out further than that.
    public static IPAddress Check(string? address, int port)
    {
        if (port is < 1 or > 65535)
            throw new DomainException("The device's port is 1 to 65535; ZKTeco's is 4370.");
        if (!IPAddress.TryParse(address?.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new DomainException("Give the device's IP address as it shows it, such as 192.168.1.106.");
        var b = ip.GetAddressBytes();
        var local = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 127;
        if (!local)
            throw new DomainException("That address is not on a local network: the device's is, such as 192.168.1.106.");
        return ip;
    }
}

public sealed record TestDeviceConnectionQuery(string Address, int Port, int CommKey) : IRequest<DeviceProbe>;

internal sealed class TestDeviceConnectionHandler(IAttendanceDeviceLink link) : IRequestHandler<TestDeviceConnectionQuery, DeviceProbe>
{
    public Task<DeviceProbe> Handle(TestDeviceConnectionQuery request, CancellationToken cancellationToken) =>
        link.ProbeAsync(DeviceAddresses.Check(request.Address, request.Port).ToString(), request.Port, request.CommKey, cancellationToken);
}

// Read now: its users and attendance log, kept; the device approved when it was not. A clash with
// its own ADMS upload of the same punches is read again, as that upload is.
public sealed record SyncDeviceCommand(string Address, int Port, int CommKey, string StaffName) : IRequest<DeviceSyncResult>, IRetryOnConflict;

internal sealed class SyncDeviceHandler(
    IAttendanceDeviceLink link, IAttendanceDeviceRepository devices, IEmployeeRepository employees, DeviceIntake intake, IUnitOfWork unitOfWork)
    : IRequestHandler<SyncDeviceCommand, DeviceSyncResult>
{
    public async Task<DeviceSyncResult> Handle(SyncDeviceCommand request, CancellationToken cancellationToken)
    {
        var address = DeviceAddresses.Check(request.Address, request.Port).ToString();
        var reading = await link.ReadAsync(address, request.Port, request.CommKey, cancellationToken);

        var device = await devices.GetBySerialNumberAsync(reading.SerialNumber, cancellationToken);
        var isNew = device is null;
        if (device is null)
        {
            device = AttendanceDevice.FirstSeen(reading.SerialNumber, DateTime.UtcNow, address);
            devices.Add(device);
        }

        if (!device.IsApproved)
            device.Approve(device.Name ?? $"{reading.Model ?? "Attendance device"} ({address})", request.StaffName);
        device.Seen(DateTime.UtcNow, address);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await intake.KeepUsersAsync(device, reading.Users, cancellationToken);
        var kept = await intake.KeepPunchesAsync(device, reading.Punches, cancellationToken);
        var numbers = (await employees.GetAllAsync(cancellationToken)).Select(e => e.DeviceUserId).OfType<string>().ToHashSet();
        return new DeviceSyncResult(
            device.SerialNumber, device.Name ?? device.SerialNumber, isNew, reading.Users.Count, reading.Users.Count(u => !numbers.Contains(u.DeviceUserId.Trim())),
            reading.Punches.Count, kept);
    }
}
