using MediatR;
using Microsoft.Extensions.Logging;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Inventory.Application.Approvals;

namespace OtantikPos.Inventory.Application.Hr;

// Who is in the restaurant now (حالة الموظفين الآن), from the punches as they come in: each
// employee's day is the one their own window puts now in, so a shift past midnight is still on
// and an early one has begun. Their scans are paired as the timesheet pairs them: an odd number
// is in, an even one out, as staff forget the device's In and Out keys.

// Told when punches come or a day is put right: the employees it touched, as they stand now, for
// the screens showing who is in to change their cards at once.
public interface IAttendanceNotifier
{
    Task StatusChangedAsync(IReadOnlyList<EmployeeStatusDto> statuses, CancellationToken cancellationToken);
}

public enum PresenceState
{
    // An odd number of scans today: in since the last.
    In = 1,

    // Scans, an even number: left at the last.
    Left = 2,

    // None yet, before their shift starts (and its grace), or on flexible hours.
    NotArrived = 3,

    // None yet, and their shift (and its grace) has started.
    Late = 4,

    // A day off, leave or a public holiday, with no scans.
    Off = 5,
}

// One employee now: the day it is for them, their state, since when (the last scan), and when
// their shift starts, if it has a start.
public sealed record PresenceDto(
    EmployeeDto Employee, DateOnly Day, PresenceState State, DateTime? Since, DateTime? DueAt, DayStatus DayStatus, string? HolidayName);

public sealed record PresenceReport(DateTime Now, IReadOnlyList<PresenceDto> Employees);

// One employee's card as it changed: the day it is for them, their state, since when, when their
// shift starts, and the time it was worked out (At).
public sealed record EmployeeStatusDto(
    Guid EmployeeId, DateOnly Day, PresenceState State, DateTime? Since, DateTime? DueAt, DayStatus DayStatus, string? HolidayName, DateTime At)
{
    public static EmployeeStatusDto From(PresenceDto p, DateTime at) => new(p.Employee.Id, p.Day, p.State, p.Since, p.DueAt, p.DayStatus, p.HolidayName, at);
}

public sealed record GetPresenceQuery : IRequest<PresenceReport>;

internal sealed class GetPresenceHandler(Presence presence) : IRequestHandler<GetPresenceQuery, PresenceReport>
{
    public Task<PresenceReport> Handle(GetPresenceQuery request, CancellationToken cancellationToken) => presence.ReadAsync(null, cancellationToken);
}

// The employees whose punches or days changed, told as they stand now. Never throws: what changed
// is saved, and a screen that missed it reads again on its minute.
internal sealed class AttendanceBroadcast(Presence presence, IAttendanceNotifier notifier, ILogger<AttendanceBroadcast> logger)
{
    public async Task ChangedAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken)
    {
        var ids = employeeIds.ToHashSet();
        if (ids.Count == 0)
            return;
        try
        {
            var report = await presence.ReadAsync(ids, cancellationToken);
            if (report.Employees.Count > 0)
                await notifier.StatusChangedAsync(report.Employees.Select(p => EmployeeStatusDto.From(p, report.Now)).ToList(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not work out who is in for the HR screens; they read again within a minute");
        }
    }
}

// Who is in now: everyone employed on their own day, or only some of them.
internal sealed class Presence(IEmployeeRepository employees, Timesheets timesheets, IAttendanceSettings settings, IRecordDescriptions clock)
{
    public async Task<PresenceReport> ReadAsync(IReadOnlySet<Guid>? only, CancellationToken cancellationToken)
    {
        var now = clock.LocalTimeOf(DateTime.UtcNow);
        var policy = await settings.GetAsync(cancellationToken);
        var people = (await employees.GetAllAsync(cancellationToken))
            .Where(e => (only is null || only.Contains(e.Id)) && e.IsEmployedOn(Timesheet.DayOf(e, now))).ToList();
        var list = new List<PresenceDto>();
        // Most share a day: read a day's together.
        foreach (var group in people.GroupBy(e => Timesheet.DayOf(e, now)))
        {
            var (_, sheets) = await timesheets.ReadAsync(group.ToList(), group.Key, group.Key, cancellationToken);
            foreach (var sheet in sheets)
            {
                var day = sheet.Days[0];
                var e = day.Evaluation;
                var since = day.Punches.Where(p => !p.IsIgnored).Select(p => (DateTime?)p.PunchedAt).Max();
                DateTime? due = sheet.Employee.ShiftStartsAt is { } start ? group.Key.ToDateTime(start) : null;
                var state = e.Punches > 0 ? e.Punches % 2 == 1 ? PresenceState.In : PresenceState.Left
                    : e.Status is DayStatus.DayOff or DayStatus.Leave or DayStatus.Holiday ? PresenceState.Off
                    : due is { } d && now > d.AddMinutes(policy.GraceMinutes) ? PresenceState.Late
                    : PresenceState.NotArrived;
                list.Add(new PresenceDto(EmployeeDto.From(sheet.Employee), group.Key, state, e.Punches > 0 ? since : null, due, e.Status, day.Holiday?.Name));
            }
        }

        return new PresenceReport(now, list.OrderBy(p => p.Employee.Name).ToList());
    }
}
