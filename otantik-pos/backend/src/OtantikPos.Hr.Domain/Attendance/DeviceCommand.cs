using System.Globalization;
using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

public enum DeviceCommandKind
{
    // Its enrolled users: their numbers and names.
    ReadUsers = 1,

    // Its attendance log from one day to another, sent again; what is kept already is not kept twice.
    ReadAttendance = 2,
}

// A command the till leaves for an attendance device (ZKTeco ADMS). The device asks for one every
// few seconds (getrequest) and is handed the oldest waiting, as "C:<number>:<command>"; it answers
// on devicecmd with that number and a return code, 0 when done. What it brings back comes as an
// upload like any other. Numbered per device, as the device knows them only by number.
public sealed class DeviceCommand : AggregateRoot
{
    private DeviceCommand() { }

    private DeviceCommand(string serialNumber, int number, DeviceCommandKind kind, string text, string queuedBy)
    {
        if (number <= 0)
            throw new DomainException("A device's commands are numbered from 1.");
        DeviceSerialNumber = serialNumber;
        Number = number;
        Kind = kind;
        Text = text;
        QueuedBy = queuedBy;
        QueuedAtUtc = DateTime.UtcNow;
    }

    public string DeviceSerialNumber { get; private set; } = string.Empty;
    public int Number { get; private set; }
    public DeviceCommandKind Kind { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public string QueuedBy { get; private set; } = string.Empty;
    public DateTime QueuedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public DateTime? AnsweredAtUtc { get; private set; }
    public int? ReturnCode { get; private set; }

    // What the device is handed.
    public string Line => $"C:{Number}:{Text}";

    public static DeviceCommand ReadUsers(string serialNumber, int number, string queuedBy) =>
        new(serialNumber, number, DeviceCommandKind.ReadUsers, "DATA QUERY USERINFO", queuedBy);

    // Every punch from the start of `from` to the end of `to`, on the device's clock.
    public static DeviceCommand ReadAttendance(string serialNumber, int number, DateOnly from, DateOnly to, string queuedBy)
    {
        if (to < from)
            throw new DomainException("Read the attendance from a day to the same day or a later one.");
        string At(DateOnly day, TimeOnly time) => day.ToDateTime(time).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new(serialNumber, number, DeviceCommandKind.ReadAttendance,
            $"DATA QUERY ATTLOG StartTime={At(from, TimeOnly.MinValue)}\tEndTime={At(to, new TimeOnly(23, 59, 59))}", queuedBy);
    }

    public void Sent(DateTime now) => SentAtUtc ??= now;

    // Its first answer is kept: a device sends one again when it missed the till's reply.
    public void Answered(int returnCode, DateTime now)
    {
        if (AnsweredAtUtc is not null)
            return;
        AnsweredAtUtc = now;
        ReturnCode = returnCode;
    }
}
