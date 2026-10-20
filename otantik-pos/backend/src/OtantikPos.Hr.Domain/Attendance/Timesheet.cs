using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Employees;

namespace OtantikPos.Hr.Domain.Attendance;

public enum DayStatus
{
    // Not over yet: what it says so far.
    Open = 1,

    Worked = 2,

    // An odd number of punches once the day is over: one is missing. A manager enters it, or
    // ignores the extra one; the month's pay waits for that.
    MissingPunch = 3,

    // A working day with no punches and no mark.
    Absent = 4,

    // A weekly day off, or one marked, with no punches.
    DayOff = 5,

    // Marked as leave, with no punches.
    Leave = 6,

    // Punched on a day off or on leave: worked, with no shift to be late for.
    WorkedOnDayOff = 7,

    // Before they started or after they left.
    NotEmployed = 8,
}

// How punches are read: the grace for arriving late (up to that many minutes after the shift
// starts is on time), and how close two scans are to be one (a finger put on twice).
public sealed record AttendancePolicy
{
    public const int MaxGraceMinutes = 120;
    public const int MaxDoubleScanMinutes = 30;

    public static readonly AttendancePolicy Default = new(0, 3);

    public AttendancePolicy(int graceMinutes, int doubleScanMinutes)
    {
        if (graceMinutes is < 0 or > MaxGraceMinutes)
            throw new DomainException($"The grace for arriving late is 0 to {MaxGraceMinutes} minutes.");
        if (doubleScanMinutes is < 0 or > MaxDoubleScanMinutes)
            throw new DomainException($"Scans that close together count as one: 0 to {MaxDoubleScanMinutes} minutes.");
        GraceMinutes = graceMinutes;
        DoubleScanMinutes = doubleScanMinutes;
    }

    public int GraceMinutes { get; }
    public int DoubleScanMinutes { get; }
}

// One employee's day, from their punches. The minutes are whole minutes.
public sealed record DayEvaluation(
    DateOnly Day,
    DayStatus Status,
    DateTime? FirstIn,
    DateTime? LastOut,
    int Punches,
    int WorkedMinutes,
    int LateMinutes,
    int EarlyLeaveMinutes,
    int AwayMinutes,
    int OvertimeMinutes)
{
    // Of the shift's nine hours, the minutes not worked: what is deducted.
    public int ShortMinutes => LateMinutes + EarlyLeaveMinutes + AwayMinutes;
}

// How a day's punches become hours worked, lateness and overtime.
//
// An employee's day runs from six hours before their shift starts to the same time the next day,
// so a shift that runs past midnight stays one day. Its punches, a double scan taken as one, are
// paired in order: in, out, in, out. Against the shift (its start, and nine hours after):
// - late: from the shift's start to the first punch, unless that is within the grace;
// - leaving early: from the last punch to the shift's end;
// - away: time punched out between pairs while the shift ran;
// - overtime: time worked after the shift's end. Coming in early is not overtime.
// So late, early, away and the shift's time worked add up to its nine hours. On a day off or
// leave there is no shift: the time is worked, and nothing is late or overtime.
public static class Timesheet
{
    public static readonly TimeSpan DayStartsBeforeShift = TimeSpan.FromHours(6);

    // The punches of `day` are the ones from From, up to but not including To.
    public static (DateTime From, DateTime To) Window(Employee employee, DateOnly day)
    {
        var from = day.ToDateTime(employee.ShiftStartsAt) - DayStartsBeforeShift;
        return (from, from.AddDays(1));
    }

    // The day a punch at `time` belongs to: the one whose window holds it.
    public static DateOnly DayOf(Employee employee, DateTime time) =>
        DateOnly.FromDateTime(time - (employee.ShiftStartsAt.ToTimeSpan() - DayStartsBeforeShift));

    // `now` is local time, as the punches are: a day is over once its window has closed.
    public static DayEvaluation Evaluate(
        Employee employee, DateOnly day, IEnumerable<DateTime> punches, DayMarkKind? mark, AttendancePolicy policy, DateTime now)
    {
        if (!employee.IsEmployedOn(day))
            return Nothing(day, DayStatus.NotEmployed);

        var (from, to) = Window(employee, day);
        var over = now >= to;
        var times = Scans(punches.Where(p => p >= from && p < to), policy.DoubleScanMinutes);
        var off = mark is not null || employee.IsDayOff(day);
        if (times.Count == 0)
            return Nothing(day, mark == DayMarkKind.Leave ? DayStatus.Leave : off ? DayStatus.DayOff : over ? DayStatus.Absent : DayStatus.Open);

        var complete = times.Count % 2 == 0;
        var pairs = Enumerable.Range(0, times.Count / 2).Select(i => (In: times[2 * i], Out: times[2 * i + 1])).ToList();
        var worked = Minutes(pairs.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.Out - p.In)));
        var status = !complete ? (over ? DayStatus.MissingPunch : DayStatus.Open)
            : !over ? DayStatus.Open
            : off ? DayStatus.WorkedOnDayOff
            : DayStatus.Worked;
        var first = times[0];
        DateTime? last = complete ? times[^1] : null;
        if (off)
            return new DayEvaluation(day, status, first, last, times.Count, worked, 0, 0, 0, 0);

        var start = day.ToDateTime(employee.ShiftStartsAt);
        var end = start + Employee.ShiftLength;
        var late = first <= start.AddMinutes(policy.GraceMinutes) ? TimeSpan.Zero : Clip(first, start, end) - start;
        if (last is not { } lastOut)
            return new DayEvaluation(day, status, first, null, times.Count, worked, Minutes(late), 0, 0, 0);

        var early = end - Clip(lastOut, start, end);
        var away = TimeSpan.Zero;
        for (var i = 0; i < pairs.Count - 1; i++)
            away += Overlap(pairs[i].Out, pairs[i + 1].In, start, end);
        var overtime = pairs.Aggregate(TimeSpan.Zero, (sum, p) => sum + Overlap(p.In, p.Out, end, DateTime.MaxValue));
        return new DayEvaluation(day, status, first, lastOut, times.Count, worked, Minutes(late), Minutes(early), Minutes(away), Minutes(overtime));
    }

    // In order, a scan within that many minutes of the one before it dropped.
    private static List<DateTime> Scans(IEnumerable<DateTime> punches, int doubleScanMinutes)
    {
        var window = TimeSpan.FromMinutes(doubleScanMinutes);
        var kept = new List<DateTime>();
        foreach (var punch in punches.Order())
        {
            if (kept.Count == 0 || punch - kept[^1] > window)
                kept.Add(punch);
        }

        return kept;
    }

    private static DayEvaluation Nothing(DateOnly day, DayStatus status) => new(day, status, null, null, 0, 0, 0, 0, 0, 0);

    private static DateTime Clip(DateTime time, DateTime from, DateTime to) => time < from ? from : time > to ? to : time;

    private static TimeSpan Overlap(DateTime from, DateTime to, DateTime windowFrom, DateTime windowTo)
    {
        var overlap = (to < windowTo ? to : windowTo) - (from > windowFrom ? from : windowFrom);
        return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
    }

    private static int Minutes(TimeSpan span) => (int)Math.Floor(span.TotalMinutes);
}

// An employee's day as worked out from their punches (الحضور اليومي): kept, so a month reads
// without pairing every punch again, and worked out again whenever a punch or a mark of the day
// changes.
public sealed class DailyRecord : AggregateRoot
{
    private DailyRecord() { }

    public DailyRecord(Guid employeeId, DayEvaluation evaluation)
    {
        EmployeeId = employeeId;
        Date = evaluation.Day;
        Update(evaluation);
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public DayStatus Status { get; private set; }
    public DateTime? FirstIn { get; private set; }
    public DateTime? LastOut { get; private set; }
    public int Punches { get; private set; }
    public int WorkedMinutes { get; private set; }
    public int LateMinutes { get; private set; }
    public int EarlyLeaveMinutes { get; private set; }
    public int AwayMinutes { get; private set; }
    public int OvertimeMinutes { get; private set; }
    public DateTime CalculatedAtUtc { get; private set; }

    public void Update(DayEvaluation evaluation)
    {
        if (evaluation.Day != Date)
            throw new DomainException("A day's record is worked out again for that same day.");
        Status = evaluation.Status;
        FirstIn = evaluation.FirstIn;
        LastOut = evaluation.LastOut;
        Punches = evaluation.Punches;
        WorkedMinutes = evaluation.WorkedMinutes;
        LateMinutes = evaluation.LateMinutes;
        EarlyLeaveMinutes = evaluation.EarlyLeaveMinutes;
        AwayMinutes = evaluation.AwayMinutes;
        OvertimeMinutes = evaluation.OvertimeMinutes;
        CalculatedAtUtc = DateTime.UtcNow;
    }

    public DayEvaluation ToEvaluation() =>
        new(Date, Status, FirstIn, LastOut, Punches, WorkedMinutes, LateMinutes, EarlyLeaveMinutes, AwayMinutes, OvertimeMinutes);
}
