using MediatR;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Inventory.Application.Approvals;

namespace OtantikPos.Inventory.Application.Hr;

// Who is in the restaurant now (حالة الموظفين الآن), from the punches as they come in: each
// employee's day is the one their own window puts now in, so a shift past midnight is still on
// and an early one has begun. Their scans are paired as the timesheet pairs them: an odd number
// is in, an even one out, as staff forget the device's In and Out keys.

// Told when punches come or change, so the screens showing who is in read it again at once.
public interface IAttendanceNotifier
{
    Task AttendanceChangedAsync(CancellationToken cancellationToken);
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

public sealed record GetPresenceQuery : IRequest<PresenceReport>;

internal sealed class GetPresenceHandler(IEmployeeRepository employees, Timesheets timesheets, IAttendanceSettings settings, IRecordDescriptions clock)
    : IRequestHandler<GetPresenceQuery, PresenceReport>
{
    public async Task<PresenceReport> Handle(GetPresenceQuery request, CancellationToken cancellationToken)
    {
        var now = clock.LocalTimeOf(DateTime.UtcNow);
        var policy = await settings.GetAsync(cancellationToken);
        var people = (await employees.GetAllAsync(cancellationToken)).Where(e => e.IsEmployedOn(Timesheet.DayOf(e, now))).ToList();
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
