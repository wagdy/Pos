using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// The attendance devices (أجهزة البصمة) and what they push (ZKTeco ADMS, the API's /iclock). A
// device first heard from is kept as waiting, and refused, until a manager approves it by name:
// it names itself only by its serial number. It keeps its own log, and sends everything after its
// stamp once approved, so nothing is lost while it waits.

public sealed record AttendanceDeviceDto(
    Guid Id, string SerialNumber, string? Name, bool IsApproved, string? ApprovedBy, DateTime? ApprovedAtUtc, DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc, string? LastAddress)
{
    public static AttendanceDeviceDto From(AttendanceDevice d) => new(
        d.Id, d.SerialNumber, d.Name, d.IsApproved, d.ApprovedBy, d.ApprovedAtUtc, d.FirstSeenAtUtc, d.LastSeenAtUtc, d.LastAddress);
}

public sealed record GetAttendanceDevicesQuery : IRequest<IReadOnlyList<AttendanceDeviceDto>>;

internal sealed class GetAttendanceDevicesHandler(IAttendanceDeviceRepository devices)
    : IRequestHandler<GetAttendanceDevicesQuery, IReadOnlyList<AttendanceDeviceDto>>
{
    public async Task<IReadOnlyList<AttendanceDeviceDto>> Handle(GetAttendanceDevicesQuery request, CancellationToken cancellationToken) =>
        (await devices.GetAllAsync(cancellationToken)).Select(AttendanceDeviceDto.From).ToList();
}

// A manager approves a device by name (or renames one), or refuses it again (Name null).
public sealed record ApproveAttendanceDeviceCommand(Guid DeviceId, string? Name, string StaffName) : IRequest<AttendanceDeviceDto>;

internal sealed class ApproveAttendanceDeviceHandler(IAttendanceDeviceRepository devices, IUnitOfWork unitOfWork)
    : IRequestHandler<ApproveAttendanceDeviceCommand, AttendanceDeviceDto>
{
    public async Task<AttendanceDeviceDto> Handle(ApproveAttendanceDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetRequiredAsync(request.DeviceId, cancellationToken);
        if (request.Name is null)
            device.Revoke();
        else
            device.Approve(request.Name, request.StaffName);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return AttendanceDeviceDto.From(device);
    }
}

// A device calling: kept (as waiting, the first time), and when it was last heard from. Whether it
// may send, and the stamp of the newest attendance it has handed over.
public sealed record DeviceContactCommand(string SerialNumber, string? Address) : IRequest<DeviceContact>, IRetryOnConflict;

public sealed record DeviceContact(bool Approved, string? AttendanceStamp);

internal sealed class DeviceContactHandler(IAttendanceDeviceRepository devices, IUnitOfWork unitOfWork) : IRequestHandler<DeviceContactCommand, DeviceContact>
{
    // It calls every few seconds: last seen is written only this often.
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);

    public async Task<DeviceContact> Handle(DeviceContactCommand request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var device = await devices.GetBySerialNumberAsync(request.SerialNumber.Trim(), cancellationToken);
        if (device is null)
        {
            device = AttendanceDevice.FirstSeen(request.SerialNumber, now, request.Address);
            devices.Add(device);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else if (now - device.LastSeenAtUtc > SeenEvery || (request.Address is not null && request.Address != device.LastAddress))
        {
            device.Seen(now, request.Address);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new DeviceContact(device.IsApproved, device.AttendanceStamp);
    }
}

// One line of a device's attendance upload (ATTLOG): the user number, the time on its clock, and
// its codes.
public sealed record DevicePunch(string DeviceUserId, DateTime PunchedAt, int? Status, int? VerifyMode);

public sealed record DevicePunchesResult(int Received, int Kept);

// A device's punches, kept once each: one already kept (sent again after a lost answer) is not
// kept twice. Each goes to the employee with its user number, or waits for one to have it. Null
// when the device is not approved: nothing it sends is taken.
public sealed record RecordDevicePunchesCommand(string SerialNumber, IReadOnlyList<DevicePunch> Punches, string? Stamp, string? Address)
    : IRequest<DevicePunchesResult?>, IRetryOnConflict;

internal sealed class RecordDevicePunchesHandler(
    IAttendanceDeviceRepository devices, IAttendanceLogRepository logs, IEmployeeRepository employees, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordDevicePunchesCommand, DevicePunchesResult?>
{
    public const int MaxPunches = 5_000;

    public async Task<DevicePunchesResult?> Handle(RecordDevicePunchesCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetBySerialNumberAsync(request.SerialNumber.Trim(), cancellationToken);
        if (device is not { IsApproved: true })
            return null;
        if (request.Punches.Count > MaxPunches)
            throw new DomainException($"Send at most {MaxPunches} punches at a time.");

        var kept = 0;
        if (request.Punches.Count > 0)
        {
            var from = request.Punches.Min(p => p.PunchedAt).AddSeconds(-1);
            var to = request.Punches.Max(p => p.PunchedAt).AddSeconds(1);
            var already = (await logs.GetFromDeviceAsync(device.SerialNumber, from, to, cancellationToken))
                .Select(l => (l.DeviceUserId, l.PunchedAt))
                .ToHashSet();
            var people = (await employees.GetAllAsync(cancellationToken))
                .Where(e => e.DeviceUserId is not null)
                .ToDictionary(e => e.DeviceUserId!, e => e.Id);
            foreach (var p in request.Punches)
            {
                var punch = AttendanceLog.FromDevice(device.SerialNumber, p.DeviceUserId, p.PunchedAt, p.Status, p.VerifyMode,
                    people.TryGetValue(p.DeviceUserId.Trim(), out var id) ? id : null);
                if (!already.Add((punch.DeviceUserId, punch.PunchedAt)))
                    continue;
                logs.Add(punch);
                kept++;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Stamp))
            device.Acknowledge(request.Stamp);
        device.Seen(DateTime.UtcNow, request.Address);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DevicePunchesResult(request.Punches.Count, kept);
    }
}
