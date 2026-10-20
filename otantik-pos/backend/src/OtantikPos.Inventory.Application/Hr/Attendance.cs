using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;

namespace OtantikPos.Inventory.Application.Hr;

// Attendance (الحضور والانصراف): the punches the devices send and managers enter, and each day as
// the timesheet works it out from them. The days are worked out when they are read, from the
// punches as they stand, so a punch entered or ignored, a day marked or a shift moved shows at
// once. A finalized month's slip keeps the days it was worked out from (DailyRecord).
//
// Correcting attendance (a manual punch, ignoring one, marking a day, resolving a suspended day,
// an administrative penalty) and the devices and their settings are a manager's; the API keeps
// them to managers.

// How punches are read, as a manager set it.
public interface IAttendanceSettings
{
    Task<AttendancePolicy> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(AttendancePolicy policy, CancellationToken cancellationToken);
}

public sealed record AttendancePolicyDto(int GraceMinutes, int DoubleScanMinutes)
{
    public static AttendancePolicyDto From(AttendancePolicy p) => new(p.GraceMinutes, p.DoubleScanMinutes);
}

public sealed record PunchDto(
    Guid Id,
    Guid? EmployeeId,
    PunchSource Source,
    string? DeviceSerialNumber,
    string? DeviceUserId,
    DateTime PunchedAt,
    int? DeviceStatus,
    int? VerifyMode,
    string? Reason,
    string? RecordedBy,
    bool IsIgnored,
    string? IgnoreReason,
    string? IgnoredBy)
{
    public static PunchDto From(AttendanceLog l) => new(
        l.Id, l.EmployeeId, l.Source, l.DeviceSerialNumber, l.DeviceUserId, l.PunchedAt, l.DeviceStatus, l.VerifyMode, l.Reason, l.RecordedBy, l.IsIgnored,
        l.IgnoreReason, l.IgnoredBy);
}

// One employee's day: its figures, the shift it was measured against, and every punch in it,
// ignored ones too.
public sealed record AttendanceDayDto(
    DateOnly Date,
    DayStatus Status,
    DateTime ShiftStartsAt,
    DateTime ShiftEndsAt,
    DateTime? FirstIn,
    DateTime? LastOut,
    int WorkedMinutes,
    int LateMinutes,
    int EarlyLeaveMinutes,
    int AwayMinutes,
    int OvertimeMinutes,
    int ShortMinutes,
    bool IsWeeklyDayOff,
    DayMarkKind? Mark,
    string? MarkNotes,
    IReadOnlyList<PunchDto> Punches,
    // A manager's administrative penalty on the day: its hours, why, and who set it.
    decimal PenaltyHours = 0,
    string? PenaltyReason = null,
    string? PenaltySetBy = null)
{
    public static AttendanceDayDto From(Employee employee, TimesheetDay day)
    {
        var e = day.Evaluation;
        var start = e.Day.ToDateTime(employee.ShiftStartsAt);
        return new AttendanceDayDto(
            e.Day, e.Status, start, start + Employee.ShiftLength, e.FirstIn, e.LastOut, e.WorkedMinutes, e.LateMinutes, e.EarlyLeaveMinutes, e.AwayMinutes,
            e.OvertimeMinutes, e.ShortMinutes, employee.IsDayOff(e.Day), day.Mark?.Kind, day.Mark?.Notes, day.Punches.Select(PunchDto.From).ToList(),
            e.PenaltyHours, day.Penalty?.Reason, day.Penalty?.SetBy);
    }
}

public sealed record EmployeeDayDto(EmployeeDto Employee, AttendanceDayDto Day);

public sealed record AttendanceTotals(int Present, int Late, int Absent, int MissingPunch, int OffOrOnLeave, int OvertimeMinutes);

// Everyone's day: who came, who is late, who is missing a punch or absent; and the punches under
// numbers nobody has yet.
public sealed record AttendanceDayReport(
    DateOnly Date, AttendancePolicyDto Policy, AttendanceTotals Totals, IReadOnlyList<EmployeeDayDto> Employees, IReadOnlyList<PunchDto> Unassigned);

public sealed record TimesheetTotals(
    int DaysWorked, int WorkedMinutes, int LateMinutes, int EarlyLeaveMinutes, int AwayMinutes, int OvertimeMinutes, int AbsentDays, int MissingPunchDays);

// One employee's month, day by day.
public sealed record TimesheetDto(EmployeeDto Employee, DateOnly Month, AttendancePolicyDto Policy, TimesheetTotals Totals, IReadOnlyList<AttendanceDayDto> Days);

public sealed record TimesheetDay(DayEvaluation Evaluation, IReadOnlyList<AttendanceLog> Punches, DayMark? Mark, AdministrativePenalty? Penalty = null);

public sealed record EmployeeSheet(Employee Employee, IReadOnlyList<TimesheetDay> Days);

// Works the days out from the punches, the marks and the settings, as they are now.
internal sealed class Timesheets(
    IAttendanceLogRepository logs, IDayMarkRepository marks, IAdministrativePenaltyRepository penalties, IAttendanceSettings settings, IRecordDescriptions clock)
{
    public async Task<(AttendancePolicy Policy, IReadOnlyList<EmployeeSheet> Sheets)> ReadAsync(
        IReadOnlyList<Employee> people, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var policy = await settings.GetAsync(cancellationToken);
        var now = clock.LocalTimeOf(DateTime.UtcNow);
        // A day's punches can be from the evening before it to the afternoon after it.
        var punches = await logs.GetBetweenAsync(
            from.AddDays(-1).ToDateTime(TimeOnly.MinValue), to.AddDays(2).ToDateTime(TimeOnly.MinValue), people.Count == 1 ? people[0].Id : null,
            cancellationToken);
        var dayMarks = (await marks.GetBetweenAsync(from, to, cancellationToken)).ToDictionary(m => (m.EmployeeId, m.Date));
        var dayPenalties = (await penalties.GetBetweenAsync(from, to, cancellationToken)).ToDictionary(p => (p.EmployeeId, p.Date));

        var sheets = new List<EmployeeSheet>();
        foreach (var person in people)
        {
            var theirs = punches.Where(p => p.EmployeeId == person.Id).OrderBy(p => p.PunchedAt).ToList();
            var days = new List<TimesheetDay>();
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                var (windowFrom, windowTo) = Timesheet.Window(person, day);
                var inDay = theirs.Where(p => p.PunchedAt >= windowFrom && p.PunchedAt < windowTo).ToList();
                var mark = dayMarks.GetValueOrDefault((person.Id, day));
                var penalty = dayPenalties.GetValueOrDefault((person.Id, day));
                var evaluation = Timesheet.Evaluate(person, day, inDay.Where(p => !p.IsIgnored).Select(p => p.PunchedAt), mark?.Kind, policy, now)
                    with { PenaltyHours = penalty is not null && person.IsEmployedOn(day) ? penalty.Hours : 0 };
                days.Add(new TimesheetDay(evaluation, inDay, mark, penalty));
            }

            sheets.Add(new EmployeeSheet(person, days));
        }

        return (policy, sheets);
    }

    public static TimesheetTotals Totals(IEnumerable<DayEvaluation> days)
    {
        var list = days.ToList();
        return new TimesheetTotals(
            list.Count(d => d.Status is DayStatus.Worked or DayStatus.WorkedOnDayOff), list.Sum(d => d.WorkedMinutes), list.Sum(d => d.LateMinutes),
            list.Sum(d => d.EarlyLeaveMinutes), list.Sum(d => d.AwayMinutes), list.Sum(d => d.OvertimeMinutes), list.Count(d => d.Status == DayStatus.Absent),
            list.Count(d => d.Status == DayStatus.Suspended));
    }
}

public sealed record GetAttendanceDayQuery(DateOnly Date) : IRequest<AttendanceDayReport>;

internal sealed class GetAttendanceDayHandler(IEmployeeRepository employees, IAttendanceLogRepository logs, Timesheets timesheets)
    : IRequestHandler<GetAttendanceDayQuery, AttendanceDayReport>
{
    public async Task<AttendanceDayReport> Handle(GetAttendanceDayQuery request, CancellationToken cancellationToken)
    {
        var people = (await employees.GetAllAsync(cancellationToken)).Where(e => e.IsEmployedOn(request.Date)).ToList();
        var (policy, sheets) = await timesheets.ReadAsync(people, request.Date, request.Date, cancellationToken);
        var rows = sheets.Select(s => new EmployeeDayDto(EmployeeDto.From(s.Employee), AttendanceDayDto.From(s.Employee, s.Days[0]))).ToList();
        var days = sheets.Select(s => s.Days[0].Evaluation).ToList();
        var unassigned = (await logs.GetBetweenAsync(
                request.Date.ToDateTime(TimeOnly.MinValue), request.Date.AddDays(1).ToDateTime(TimeOnly.MinValue), null, cancellationToken))
            .Where(p => p.EmployeeId is null)
            .Select(PunchDto.From)
            .ToList();
        var totals = new AttendanceTotals(
            days.Count(d => d.Punches > 0 && d.Status is not DayStatus.NotEmployed),
            days.Count(d => d.LateMinutes > 0),
            days.Count(d => d.Status == DayStatus.Absent),
            days.Count(d => d.Status == DayStatus.Suspended),
            days.Count(d => d.Status is DayStatus.DayOff or DayStatus.Leave),
            days.Sum(d => d.OvertimeMinutes));
        return new AttendanceDayReport(request.Date, AttendancePolicyDto.From(policy), totals, rows, unassigned);
    }
}

public sealed record GetTimesheetQuery(Guid EmployeeId, DateOnly Month) : IRequest<TimesheetDto>;

internal sealed class GetTimesheetHandler(IEmployeeRepository employees, Timesheets timesheets) : IRequestHandler<GetTimesheetQuery, TimesheetDto>
{
    public async Task<TimesheetDto> Handle(GetTimesheetQuery request, CancellationToken cancellationToken)
    {
        var month = PayrollMonth.Required(request.Month);
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        var (policy, sheets) = await timesheets.ReadAsync([employee], month, month.AddMonths(1).AddDays(-1), cancellationToken);
        var days = sheets[0].Days;
        return new TimesheetDto(
            EmployeeDto.From(employee), month, AttendancePolicyDto.From(policy), Timesheets.Totals(days.Select(d => d.Evaluation)),
            days.Select(d => AttendanceDayDto.From(employee, d)).ToList());
    }
}

// What a finalized month's pay was worked out from stays as it was: its days are not corrected
// after it.
internal static class FinalizedMonths
{
    public static async Task EnsureOpenAsync(this IPayrollSlipRepository slips, Employee employee, DateOnly day, CancellationToken cancellationToken)
    {
        var month = PayrollMonth.Of(day);
        if (await slips.GetAsync(employee.Id, month, cancellationToken) is { IsFinalized: true })
            throw new DomainException($"{employee.Name}'s pay for {month:MMMM yyyy} is finalized: its days are not changed after it.");
    }
}

// A punch the device missed, entered by a manager with the reason. PunchId is made by the till.
public sealed record AddManualPunchCommand(Guid PunchId, Guid EmployeeId, DateTime PunchedAt, string Reason, string StaffName)
    : IRequest<PunchDto>, IRetryOnConflict;

internal sealed class AddManualPunchHandler(
    IEmployeeRepository employees, IAttendanceLogRepository logs, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<AddManualPunchCommand, PunchDto>
{
    public async Task<PunchDto> Handle(AddManualPunchCommand request, CancellationToken cancellationToken)
    {
        if (await logs.GetByIdAsync(request.PunchId, cancellationToken) is { } made)
            return PunchDto.From(made);
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        var day = Timesheet.DayOf(employee, request.PunchedAt);
        if (!employee.IsEmployedOn(day))
            throw new DomainException($"{employee.Name} is not employed on {day:d MMM yyyy}.");
        await slips.EnsureOpenAsync(employee, day, cancellationToken);
        var punch = AttendanceLog.Manual(request.PunchId, employee.Id, request.PunchedAt, request.Reason, request.StaffName);
        logs.Add(punch);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PunchDto.From(punch);
    }
}

// A punch that should not count (a scan made twice, a test), or one counted again.
public sealed record IgnorePunchCommand(Guid PunchId, bool Ignore, string? Reason, string StaffName) : IRequest<PunchDto>;

internal sealed class IgnorePunchHandler(
    IEmployeeRepository employees, IAttendanceLogRepository logs, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<IgnorePunchCommand, PunchDto>
{
    public async Task<PunchDto> Handle(IgnorePunchCommand request, CancellationToken cancellationToken)
    {
        var punch = await logs.GetRequiredAsync(request.PunchId, cancellationToken);
        if (punch.EmployeeId is { } id)
        {
            var employee = await employees.GetRequiredAsync(id, cancellationToken);
            await slips.EnsureOpenAsync(employee, Timesheet.DayOf(employee, punch.PunchedAt), cancellationToken);
        }

        if (request.Ignore)
            punch.Ignore(request.Reason ?? string.Empty, request.StaffName);
        else
            punch.Restore();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PunchDto.From(punch);
    }
}

// A day marked as leave or a day off, a weekly day off marked as a working day (a swap), or the
// mark taken off (Kind null).
public sealed record SetDayMarkCommand(Guid EmployeeId, DateOnly Date, DayMarkKind? Kind, string? Notes, string StaffName) : IRequest, IRetryOnConflict;

internal sealed class SetDayMarkHandler(IEmployeeRepository employees, IDayMarkRepository marks, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<SetDayMarkCommand>
{
    public async Task Handle(SetDayMarkCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        if (!employee.IsEmployedOn(request.Date))
            throw new DomainException($"{employee.Name} is not employed on {request.Date:d MMM yyyy}.");
        await slips.EnsureOpenAsync(employee, request.Date, cancellationToken);
        if (request.Kind == DayMarkKind.WorkingDay && !employee.IsDayOff(request.Date))
            throw new DomainException($"{request.Date:dddd} is a working day for {employee.Name} already: only a weekly day off is marked as one.");
        var mark = await marks.GetAsync(employee.Id, request.Date, cancellationToken);
        if (request.Kind is not { } kind)
        {
            if (mark is not null)
                marks.Remove(mark);
        }
        else if (mark is null)
        {
            marks.Add(new DayMark(employee.Id, request.Date, kind, request.Notes, request.StaffName));
        }
        else
        {
            mark.Change(kind, request.Notes, request.StaffName);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed record GetAttendanceSettingsQuery : IRequest<AttendancePolicyDto>;

internal sealed class GetAttendanceSettingsHandler(IAttendanceSettings settings) : IRequestHandler<GetAttendanceSettingsQuery, AttendancePolicyDto>
{
    public async Task<AttendancePolicyDto> Handle(GetAttendanceSettingsQuery request, CancellationToken cancellationToken) =>
        AttendancePolicyDto.From(await settings.GetAsync(cancellationToken));
}

public sealed record SaveAttendanceSettingsCommand(int GraceMinutes, int DoubleScanMinutes) : IRequest<AttendancePolicyDto>;

internal sealed class SaveAttendanceSettingsHandler(IAttendanceSettings settings) : IRequestHandler<SaveAttendanceSettingsCommand, AttendancePolicyDto>
{
    public async Task<AttendancePolicyDto> Handle(SaveAttendanceSettingsCommand request, CancellationToken cancellationToken)
    {
        var policy = new AttendancePolicy(request.GraceMinutes, request.DoubleScanMinutes);
        await settings.SaveAsync(policy, cancellationToken);
        return AttendancePolicyDto.From(policy);
    }
}

// A manager's administrative penalty on a day (جزاء إداري): its hours and why, or taken off
// (Hours null), while the month's pay is not finalized.
public sealed record SetAdministrativePenaltyCommand(Guid EmployeeId, DateOnly Date, decimal? Hours, string? Reason, string StaffName)
    : IRequest, IRetryOnConflict;

internal sealed class SetAdministrativePenaltyHandler(
    IEmployeeRepository employees, IAdministrativePenaltyRepository penalties, IPayrollSlipRepository slips, IUnitOfWork unitOfWork)
    : IRequestHandler<SetAdministrativePenaltyCommand>
{
    public async Task Handle(SetAdministrativePenaltyCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        if (!employee.IsEmployedOn(request.Date))
            throw new DomainException($"{employee.Name} is not employed on {request.Date:d MMM yyyy}.");
        await slips.EnsureOpenAsync(employee, request.Date, cancellationToken);
        var penalty = await penalties.GetAsync(employee.Id, request.Date, cancellationToken);
        if (request.Hours is not { } hours)
        {
            if (penalty is not null)
                penalties.Remove(penalty);
        }
        else if (penalty is null)
        {
            penalties.Add(new AdministrativePenalty(employee.Id, request.Date, hours, request.Reason ?? string.Empty, request.StaffName));
        }
        else
        {
            penalty.Change(hours, request.Reason ?? string.Empty, request.StaffName);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// The days suspended for a manager (موقوف للمراجعة): a punch-in left 18 hours without its
// punch-out, everyone's, from `From` to `To`, oldest first.
public sealed record GetSuspendedDaysQuery(DateOnly From, DateOnly To) : IRequest<IReadOnlyList<EmployeeDayDto>>;

internal sealed class GetSuspendedDaysHandler(IEmployeeRepository employees, Timesheets timesheets)
    : IRequestHandler<GetSuspendedDaysQuery, IReadOnlyList<EmployeeDayDto>>
{
    public const int MaxDays = 400;

    public async Task<IReadOnlyList<EmployeeDayDto>> Handle(GetSuspendedDaysQuery request, CancellationToken cancellationToken)
    {
        if (request.To < request.From)
            throw new DomainException("The first day must come before the last.");
        if (request.To.DayNumber - request.From.DayNumber > MaxDays)
            throw new DomainException($"Look at {MaxDays} days at most.");
        var people = (await employees.GetAllAsync(cancellationToken))
            .Where(e => e.HiredOn <= request.To && (e.LeftOn is null || e.LeftOn >= request.From))
            .ToList();
        if (people.Count == 0)
            return [];
        var (_, sheets) = await timesheets.ReadAsync(people, request.From, request.To, cancellationToken);
        return sheets
            .SelectMany(s => s.Days.Where(d => d.Evaluation.Status == DayStatus.Suspended)
                .Select(d => new EmployeeDayDto(EmployeeDto.From(s.Employee), AttendanceDayDto.From(s.Employee, d))))
            .OrderBy(d => d.Day.Date).ThenBy(d => d.Employee.Name)
            .ToList();
    }
}

// A suspended day resolved: the manager's punch-out for the punch-in left without one, after it
// and within the day, and the day worked out again from it. PunchId is made by the till.
public sealed record ResolveSuspendedDayCommand(Guid PunchId, Guid EmployeeId, DateOnly Date, DateTime PunchedOutAt, string Reason, string StaffName)
    : IRequest<AttendanceDayDto>, IRetryOnConflict;

internal sealed class ResolveSuspendedDayHandler(
    IEmployeeRepository employees, IAttendanceLogRepository logs, IPayrollSlipRepository slips, Timesheets timesheets, IUnitOfWork unitOfWork)
    : IRequestHandler<ResolveSuspendedDayCommand, AttendanceDayDto>
{
    public async Task<AttendanceDayDto> Handle(ResolveSuspendedDayCommand request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetRequiredAsync(request.EmployeeId, cancellationToken);
        if (await logs.GetByIdAsync(request.PunchId, cancellationToken) is null)
        {
            var day = (await timesheets.ReadAsync([employee], request.Date, request.Date, cancellationToken)).Sheets[0].Days[0];
            if (day.Evaluation.Status != DayStatus.Suspended)
                throw new DomainException($"{employee.Name}'s {request.Date:d MMM} is not suspended: there is no punch-out to enter.");
            await slips.EnsureOpenAsync(employee, request.Date, cancellationToken);
            var punchedIn = day.Punches.Where(p => !p.IsIgnored).Max(p => p.PunchedAt);
            var (_, windowTo) = Timesheet.Window(employee, request.Date);
            if (request.PunchedOutAt <= punchedIn)
                throw new DomainException($"The punch-out comes after the punch-in at {punchedIn:HH:mm}.");
            if (request.PunchedOutAt >= windowTo || request.PunchedOutAt - punchedIn > Timesheet.AbandonedAfter)
                throw new DomainException($"The punch-out is within {Timesheet.AbandonedAfter.TotalHours:0} hours of the punch-in, on the same day's shift.");
            logs.Add(AttendanceLog.Manual(request.PunchId, employee.Id, request.PunchedOutAt, request.Reason, request.StaffName));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var resolved = (await timesheets.ReadAsync([employee], request.Date, request.Date, cancellationToken)).Sheets[0].Days[0];
        return AttendanceDayDto.From(employee, resolved);
    }
}
