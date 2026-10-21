using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// The attendance devices (أجهزة البصمة) and what they push (ZKTeco ADMS, the API's /iclock). A
// device first heard from is kept as waiting, and refused, until a manager approves it by name:
// it names itself only by its serial number. It keeps its own log, and sends everything after its
// stamp once approved, so nothing is lost while it waits.

// A command left for a device: waiting until it asks, sent, and answered (0 is done).
public sealed record DeviceCommandDto(
    Guid Id, int Number, DeviceCommandKind Kind, string Text, string QueuedBy, DateTime QueuedAtUtc, DateTime? SentAtUtc, DateTime? AnsweredAtUtc,
    int? ReturnCode)
{
    public static DeviceCommandDto From(DeviceCommand c) =>
        new(c.Id, c.Number, c.Kind, c.Text, c.QueuedBy, c.QueuedAtUtc, c.SentAtUtc, c.AnsweredAtUtc, c.ReturnCode);
}

// A device, and its newest commands, newest first.
public sealed record AttendanceDeviceDto(
    Guid Id, string SerialNumber, string? Name, bool IsApproved, string? ApprovedBy, DateTime? ApprovedAtUtc, DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc, string? LastAddress, IReadOnlyList<DeviceCommandDto> Commands)
{
    public static AttendanceDeviceDto From(AttendanceDevice d, IEnumerable<DeviceCommand>? commands = null) => new(
        d.Id, d.SerialNumber, d.Name, d.IsApproved, d.ApprovedBy, d.ApprovedAtUtc, d.FirstSeenAtUtc, d.LastSeenAtUtc, d.LastAddress,
        (commands ?? []).Where(c => c.DeviceSerialNumber == d.SerialNumber).OrderByDescending(c => c.Number).Select(DeviceCommandDto.From).ToList());
}

public sealed record GetAttendanceDevicesQuery : IRequest<IReadOnlyList<AttendanceDeviceDto>>;

internal sealed class GetAttendanceDevicesHandler(IAttendanceDeviceRepository devices, IDeviceCommandRepository commands)
    : IRequestHandler<GetAttendanceDevicesQuery, IReadOnlyList<AttendanceDeviceDto>>
{
    public async Task<IReadOnlyList<AttendanceDeviceDto>> Handle(GetAttendanceDevicesQuery request, CancellationToken cancellationToken)
    {
        var recent = await commands.GetRecentAsync(3, cancellationToken);
        return (await devices.GetAllAsync(cancellationToken)).Select(d => AttendanceDeviceDto.From(d, recent)).ToList();
    }
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

// A device's punches, kept once each: one already kept (sent again after a lost answer, or the
// whole log read again) is not kept twice. Each goes to the employee with its user number, or
// waits for one to have it. Null when the device is not approved: nothing it sends is taken.
//
// A device keeps its punches while the till is away (the laptop off the network) and sends them
// all when it is back: any number, saved a thousand at a time. The days are worked out from the
// punches' own times, so past days come out as if they had arrived on time. One that arrives for
// a month whose pay is finalized is kept, and Corrections lists it for a manager.
public sealed record RecordDevicePunchesCommand(string SerialNumber, IReadOnlyList<DevicePunch> Punches, string? Stamp, string? Address)
    : IRequest<DevicePunchesResult?>, IRetryOnConflict;

internal sealed class RecordDevicePunchesHandler(IAttendanceDeviceRepository devices, DeviceIntake intake, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordDevicePunchesCommand, DevicePunchesResult?>
{
    public async Task<DevicePunchesResult?> Handle(RecordDevicePunchesCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetBySerialNumberAsync(request.SerialNumber.Trim(), cancellationToken);
        if (device is not { IsApproved: true })
            return null;

        if (!string.IsNullOrWhiteSpace(request.Stamp))
            device.Acknowledge(request.Stamp);
        device.Seen(DateTime.UtcNow, request.Address);
        var kept = await intake.KeepPunchesAsync(device, request.Punches, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DevicePunchesResult(request.Punches.Count, kept);
    }
}

// What a device sends or is read for, kept the same way whichever way it came (ADMS, or port 4370):
// its punches once each, to whoever has their number; its users by number and name.
internal sealed class DeviceIntake(
    IAttendanceLogRepository logs, IEmployeeRepository employees, IDeviceUserRepository users, IUnitOfWork unitOfWork, IAttendanceNotifier notifier)
{
    // Saved this many at a time, so a device back after weeks away is not one long save.
    public const int SaveEvery = 1_000;

    // How many were new; saved, and the screens told when any were.
    public async Task<int> KeepPunchesAsync(AttendanceDevice device, IReadOnlyList<DevicePunch> punches, CancellationToken cancellationToken)
    {
        var kept = 0;
        if (punches.Count > 0)
        {
            var from = punches.Min(p => p.PunchedAt).AddSeconds(-1);
            var to = punches.Max(p => p.PunchedAt).AddSeconds(1);
            var already = (await logs.GetFromDeviceAsync(device.SerialNumber, from, to, cancellationToken))
                .Select(l => (l.DeviceUserId, l.PunchedAt))
                .ToHashSet();
            var people = (await employees.GetAllAsync(cancellationToken))
                .Where(e => e.DeviceUserId is not null)
                .ToDictionary(e => e.DeviceUserId!, e => e.Id);
            foreach (var p in punches)
            {
                var punch = AttendanceLog.FromDevice(device.SerialNumber, p.DeviceUserId, p.PunchedAt, p.Status, p.VerifyMode,
                    people.TryGetValue(p.DeviceUserId.Trim(), out var id) ? id : null);
                if (!already.Add((punch.DeviceUserId, punch.PunchedAt)))
                    continue;
                logs.Add(punch);
                kept++;
                // A clash with another upload of the same punches comes back as a conflict, and
                // the whole upload is read again: what was saved is then known, and not kept twice.
                if (kept % SaveEvery == 0)
                    await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (kept > 0)
            await notifier.AttendanceChangedAsync(cancellationToken);
        return kept;
    }

    public async Task KeepUsersAsync(AttendanceDevice device, IReadOnlyList<DeviceUserLine> lines, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var known = (await users.GetForDeviceAsync(device.SerialNumber, cancellationToken)).ToDictionary(u => u.DeviceUserId);
        foreach (var line in lines)
        {
            var number = line.DeviceUserId.Trim();
            if (known.TryGetValue(number, out var user))
            {
                user.Seen(line.Name, now);
            }
            else
            {
                user = new DeviceUser(device.SerialNumber, number, line.Name, now);
                users.Add(user);
                known[number] = user;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// A command left for an approved device by a manager: its users, or its attendance from one day
// to another read again. One of a kind waits at a time: asking again while it waits is the same.
public sealed record QueueDeviceCommandCommand(Guid DeviceId, DeviceCommandKind Kind, DateOnly? From, DateOnly? To, string StaffName)
    : IRequest<DeviceCommandDto>, IRetryOnConflict;

internal sealed class QueueDeviceCommandHandler(
    IAttendanceDeviceRepository devices, IDeviceCommandRepository commands, IRecordDescriptions clock, IUnitOfWork unitOfWork)
    : IRequestHandler<QueueDeviceCommandCommand, DeviceCommandDto>
{
    // Its whole log is a year or two at most.
    public const int MaxDays = 800;

    public async Task<DeviceCommandDto> Handle(QueueDeviceCommandCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetRequiredAsync(request.DeviceId, cancellationToken);
        if (!device.IsApproved)
            throw new DomainException("Approve the device first: until then it is not listened to.");
        if (await commands.GetNextAsync(device.SerialNumber, cancellationToken) is { } waiting && waiting.Kind == request.Kind)
            return DeviceCommandDto.From(waiting);

        var number = await commands.GetLastNumberAsync(device.SerialNumber, cancellationToken) + 1;
        DeviceCommand command;
        if (request.Kind == DeviceCommandKind.ReadUsers)
        {
            command = DeviceCommand.ReadUsers(device.SerialNumber, number, request.StaffName);
        }
        else
        {
            var to = request.To ?? DateOnly.FromDateTime(clock.LocalTimeOf(DateTime.UtcNow));
            if (request.From is not { } from)
                throw new DomainException("Say from which day to read the attendance again.");
            if (to.DayNumber - from.DayNumber > MaxDays)
                throw new DomainException($"Read {MaxDays} days of attendance at most at a time.");
            command = DeviceCommand.ReadAttendance(device.SerialNumber, number, from, to, request.StaffName);
        }

        commands.Add(command);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return DeviceCommandDto.From(command);
    }
}

// A device asking for a command (getrequest): the oldest waiting, handed over, or none. Null when
// the device is not approved.
public sealed record NextDeviceCommandCommand(string SerialNumber) : IRequest<string?>, IRetryOnConflict;

internal sealed class NextDeviceCommandHandler(IAttendanceDeviceRepository devices, IDeviceCommandRepository commands, IUnitOfWork unitOfWork)
    : IRequestHandler<NextDeviceCommandCommand, string?>
{
    public async Task<string?> Handle(NextDeviceCommandCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetBySerialNumberAsync(request.SerialNumber.Trim(), cancellationToken);
        if (device is not { IsApproved: true })
            return null;
        var command = await commands.GetNextAsync(device.SerialNumber, cancellationToken);
        if (command is null)
            return null;
        command.Sent(DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return command.Line;
    }
}

// A device's answers to its commands (devicecmd): each command's number and return code.
public sealed record DeviceCommandAnswersCommand(string SerialNumber, IReadOnlyList<(int Number, int ReturnCode)> Answers) : IRequest, IRetryOnConflict;

internal sealed class DeviceCommandAnswersHandler(IDeviceCommandRepository commands, IUnitOfWork unitOfWork) : IRequestHandler<DeviceCommandAnswersCommand>
{
    public async Task Handle(DeviceCommandAnswersCommand request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        foreach (var (number, code) in request.Answers)
            (await commands.GetByNumberAsync(request.SerialNumber.Trim(), number, cancellationToken))?.Answered(code, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// One user as a device lists them (USERINFO): their number and name. Nothing else is taken.
public sealed record DeviceUserLine(string DeviceUserId, string? Name);

// The users a device sent, kept or brought up to date; null when the device is not approved.
public sealed record RecordDeviceUsersCommand(string SerialNumber, IReadOnlyList<DeviceUserLine> Users) : IRequest<int?>, IRetryOnConflict;

internal sealed class RecordDeviceUsersHandler(IAttendanceDeviceRepository devices, DeviceIntake intake)
    : IRequestHandler<RecordDeviceUsersCommand, int?>
{
    public async Task<int?> Handle(RecordDeviceUsersCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetBySerialNumberAsync(request.SerialNumber.Trim(), cancellationToken);
        if (device is not { IsApproved: true })
            return null;
        await intake.KeepUsersAsync(device, request.Users, cancellationToken);
        return request.Users.Count;
    }
}

// A user on a device: who has their number on the till, if anyone, and how many of their punches
// wait for someone to have it.
public sealed record DeviceUserDto(
    Guid Id, string DeviceSerialNumber, string? DeviceName, string DeviceUserId, string Name, DateTime LastSeenAtUtc, Guid? EmployeeId,
    string? EmployeeName, int WaitingPunches);

public sealed record GetDeviceUsersQuery : IRequest<IReadOnlyList<DeviceUserDto>>;

internal sealed class GetDeviceUsersHandler(
    IDeviceUserRepository users, IAttendanceDeviceRepository devices, IEmployeeRepository employees, IAttendanceLogRepository logs)
    : IRequestHandler<GetDeviceUsersQuery, IReadOnlyList<DeviceUserDto>>
{
    public async Task<IReadOnlyList<DeviceUserDto>> Handle(GetDeviceUsersQuery request, CancellationToken cancellationToken)
    {
        var all = await users.GetAllAsync(cancellationToken);
        if (all.Count == 0)
            return [];
        var names = (await devices.GetAllAsync(cancellationToken)).ToDictionary(d => d.SerialNumber, d => d.Name);
        var people = (await employees.GetAllAsync(cancellationToken)).Where(e => e.DeviceUserId is not null).ToDictionary(e => e.DeviceUserId!);
        var waiting = await logs.CountUnassignedAsync(cancellationToken);
        return all
            .Select(u =>
            {
                var employee = people.GetValueOrDefault(u.DeviceUserId);
                return new DeviceUserDto(
                    u.Id, u.DeviceSerialNumber, names.GetValueOrDefault(u.DeviceSerialNumber), u.DeviceUserId, u.Name, u.LastSeenAtUtc, employee?.Id,
                    employee?.Name, waiting.GetValueOrDefault(u.DeviceUserId));
            })
            .OrderBy(u => u.EmployeeId is not null)
            .ThenBy(u => u.DeviceUserId.Length).ThenBy(u => u.DeviceUserId, StringComparer.Ordinal)
            .ToList();
    }
}
