using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;

namespace OtantikPos.Hr.Domain.Payroll;

public enum PayrollSlipStatus
{
    // Worked out, and worked out again as the month's punches, marks and adjustments change.
    Draft = 1,

    // Closed: its figures stay as they are, and what comes after goes to the next month.
    Finalized = 2,
}

// One employee's pay for one month (مسير الرواتب), every figure it is made of. The minutes and days
// are the timesheet's; the money is rounded to the piastre.
//
// Net = base salary + incentives + leave allowances + days off worked + holidays worked
//     + overtime − advances − last month's shortfall − lateness − absence − administrative
//     penalties.
//
// Leave allowances are the ones entered; a day off worked earns its own, worked out from the
// timesheet (DaysOffWorkedPay): a day's pay for half a shift or more, its hours for less. So does
// a public holiday worked (HolidaysWorkedPay), paid once when it falls on a day off. Worked on a
// working day, a holiday's lateness and leaving early count as on any other, but never take more
// than its allowance gives: a holiday worked never pays less than one stayed home.
// When the deductions are more than the pay, nothing is paid and the rest is carried to the next
// month's slip (BroughtForward there).
//
// A daily employee (EmploymentType.Daily) has no salary: their base pay is their daily wage for
// each day worked on a working day or a holiday. A day not worked earns nothing and costs
// nothing, so nothing is absent. A day off worked is paid as anyone's (DaysOffWorkedPay): a day's
// wage for half a shift or more, its hours for less. A holiday worked earns its allowance on top,
// and lateness and overtime count as anyone's.
public sealed record PayrollFigures(
    decimal MonthlySalary,
    int DaysInMonth,
    int DaysEmployed,
    decimal BaseSalary,
    decimal Incentives,
    decimal LeaveAllowances,
    int OvertimeMinutes,
    decimal OvertimePay,
    int ShortMinutes,
    decimal LatenessPenalty,
    int AbsentDays,
    decimal AbsencePenalty,
    decimal Advances,
    decimal BroughtForward,
    int DaysWorked,
    int DaysOffWorked,
    // Days suspended: a punch-in with no punch-out, for a manager to put right.
    int MissingPunchDays,
    int OpenDays,
    decimal DaysOffWorkedPay = 0,
    // A manager's administrative penalties (جزاءات إدارية): in hours, and in money (a disciplinary
    // action's deductions among them, AdministrativePenaltyAmounts); their deduction, the hours at
    // the hourly rate and the amounts as they are.
    decimal AdministrativePenaltyHours = 0,
    decimal AdministrativePenalty = 0,
    // Public holidays worked (عطلات رسمية), and their holiday allowance.
    int HolidaysWorked = 0,
    decimal HolidaysWorkedPay = 0,
    // How the base pay was made: a monthly salary's share, or a daily wage for each day worked.
    EmploymentType EmploymentType = EmploymentType.Monthly,
    decimal DailyWage = 0,
    decimal AdministrativePenaltyAmounts = 0,
    // Days of approved unpaid leave (إجازة بدون راتب), and a day's pay off for each: not absences.
    // A daily worker is paid only the days they work, so theirs take nothing.
    int UnpaidLeaveDays = 0,
    decimal UnpaidLeaveDeduction = 0)
{
    public decimal Gross => BaseSalary + Incentives + LeaveAllowances + DaysOffWorkedPay + HolidaysWorkedPay + OvertimePay;
    public decimal Deductions => Advances + BroughtForward + LatenessPenalty + AbsencePenalty + AdministrativePenalty + UnpaidLeaveDeduction;
    public decimal Net => Gross - Deductions;
    public decimal NetPayable => Net > 0 ? Net : 0;
    public decimal CarriedForward => Net < 0 ? -Net : 0;

    // What the month's work cost the restaurant: the pay less what was deducted for time not
    // worked and for penalties. Advances and last month's shortfall settle what was owed; they
    // cost nothing.
    public decimal WagesCost => Gross - LatenessPenalty - AbsencePenalty - AdministrativePenalty - UnpaidLeaveDeduction;
}

public static class PayrollCalculator
{
    // From half a shift worked on a day off or a holiday, a day's pay; less earns its hours.
    public const int HalfShiftMinutes = Employee.ShiftHours * 60 / 2;

    // A day off or a public holiday worked, as its allowance: a day's pay from half a shift, its
    // minutes before.
    public static decimal DayOffWorkedPay(Employee employee, int workedMinutes) =>
        workedMinutes >= HalfShiftMinutes ? employee.DailyRate : workedMinutes * employee.MinuteRate;

    // The minutes a day's lateness, leaving early and time away take off the pay. A holiday worked
    // for less than half a shift earns only its minutes, so it takes off no more than those: a
    // holiday is everyone's, and coming in on one never costs. From half a shift, its day's pay
    // covers the shift's nine hours already.
    public static int CountedShortMinutes(DayEvaluation day) =>
        day.Status == DayStatus.WorkedOnHoliday && day.WorkedMinutes < HalfShiftMinutes
            ? Math.Min(day.ShortMinutes, day.WorkedMinutes)
            : day.ShortMinutes;

    // `days` are the month's days as the timesheet has them; an employed day without one is not
    // worked out yet, and counts as open. `advances` are what the treasury paid them against this
    // month, less voids; `broughtForward` what last month's slip carried over.
    public static PayrollFigures Calculate(
        Employee employee,
        DateOnly month,
        IEnumerable<DayEvaluation> days,
        IEnumerable<PayrollAdjustment> adjustments,
        decimal advances,
        decimal broughtForward)
    {
        PayrollMonth.Required(month);
        if (advances < 0 || broughtForward < 0)
            throw new DomainException("Advances and what was carried over are zero or more.");

        var byDay = new Dictionary<DateOnly, DayEvaluation>();
        foreach (var day in days)
        {
            if (PayrollMonth.Of(day.Day) != month)
                throw new DomainException("A month's slip is worked out from that month's days.");
            if (!byDay.TryAdd(day.Day, day))
                throw new DomainException("Each day is counted once.");
        }

        var employed = PayrollMonth.Days(month).Where(employee.IsEmployedOn).ToList();
        var counted = employed.Select(d => byDay.GetValueOrDefault(d)).ToList();
        int Count(DayStatus status) => counted.Count(d => d?.Status == status);

        var standing = adjustments.Where(a => !a.IsVoided).ToList();
        if (standing.Any(a => a.EmployeeId != employee.Id || a.Month != month))
            throw new DomainException("A slip counts its own employee's adjustments for its month.");

        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var daily = employee.IsDaily;
        var daysWorked = Count(DayStatus.Worked) + Count(DayStatus.WorkedOnDayOff) + Count(DayStatus.WorkedOnHoliday);
        var baseSalary = daily ? employee.DailyWage * (daysWorked - Count(DayStatus.WorkedOnDayOff))
            : employed.Count == daysInMonth ? employee.MonthlySalary
            : employee.MonthlySalary * employed.Count / daysInMonth;
        var overtime = counted.Sum(d => d?.OvertimeMinutes ?? 0);
        var shortMinutes = counted.Sum(d => d is null ? 0 : CountedShortMinutes(d));
        var absent = daily ? 0 : Count(DayStatus.Absent);
        var unpaidLeave = daily ? 0 : Count(DayStatus.UnpaidLeave);
        var penaltyHours = counted.Sum(d => d?.PenaltyHours ?? 0);
        var penaltyAmounts = counted.Sum(d => d?.PenaltyAmount ?? 0);
        var daysOffWorkedPay = counted
            .Where(d => d?.Status == DayStatus.WorkedOnDayOff)
            .Sum(d => DayOffWorkedPay(employee, d!.WorkedMinutes));
        var holidaysWorked = counted.Where(d => d?.Status == DayStatus.WorkedOnHoliday).ToList();
        var holidaysWorkedPay = holidaysWorked.Sum(d => DayOffWorkedPay(employee, d!.WorkedMinutes));

        return new PayrollFigures(
            employee.MonthlySalary,
            daysInMonth,
            employed.Count,
            Money(baseSalary),
            Money(standing.Where(a => a.Kind == AdjustmentKind.Incentive).Sum(a => a.Amount)),
            Money(standing.Where(a => a.Kind == AdjustmentKind.LeaveAllowance).Sum(a => a.Amount)),
            overtime,
            Money(overtime * employee.MinuteRate),
            shortMinutes,
            Money(shortMinutes * employee.MinuteRate),
            absent,
            Money(absent * employee.DailyRate),
            Money(advances),
            Money(broughtForward),
            daysWorked,
            Count(DayStatus.WorkedOnDayOff),
            Count(DayStatus.Suspended),
            counted.Count(d => d is null || d.Status == DayStatus.Open),
            Money(daysOffWorkedPay),
            penaltyHours,
            Money(penaltyHours * employee.HourlyRate + penaltyAmounts),
            holidaysWorked.Count,
            Money(holidaysWorkedPay),
            employee.EmploymentType,
            employee.DailyWage,
            Money(penaltyAmounts),
            unpaidLeave,
            Money(unpaidLeave * employee.DailyRate));
    }

    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

// An employee's payslip for a month: worked out as a draft, and finalized once the month is over
// and every day is settled. Finalized, it is what is paid: its net payable, and what it carries
// to the next month.
public sealed class PayrollSlip : AggregateRoot
{
    private PayrollSlip() { }

    public PayrollSlip(Guid id, Guid employeeId, DateOnly month, PayrollFigures figures)
    {
        Id = id;
        EmployeeId = employeeId;
        Month = PayrollMonth.Required(month);
        Status = PayrollSlipStatus.Draft;
        Recalculate(figures);
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Month { get; private set; }
    public PayrollSlipStatus Status { get; private set; }
    public PayrollFigures Figures { get; private set; } = null!;

    // Kept as columns, so a month's payroll adds up in the database.
    public decimal NetPayable { get; private set; }
    public decimal CarriedForward { get; private set; }
    public decimal WagesCost { get; private set; }

    public DateTime CalculatedAtUtc { get; private set; }
    public DateTime? FinalizedAtUtc { get; private set; }
    public string? FinalizedBy { get; private set; }
    public bool IsFinalized => Status == PayrollSlipStatus.Finalized;

    public void Recalculate(PayrollFigures figures)
    {
        if (IsFinalized)
            throw new DomainException("This month's slip is finalized: what changes now goes to next month's.");
        Figures = figures;
        NetPayable = figures.NetPayable;
        CarriedForward = figures.CarriedForward;
        WagesCost = figures.WagesCost;
        CalculatedAtUtc = DateTime.UtcNow;
    }

    public void Finalize(string finalizedBy)
    {
        if (IsFinalized)
            throw new DomainException("This slip is already finalized.");
        if (Figures.OpenDays > 0)
            throw new DomainException("The month is not over yet: a slip is finalized once all its days are.");
        if (Figures.MissingPunchDays > 0)
            throw new DomainException(
                $"{Figures.MissingPunchDays} {(Figures.MissingPunchDays == 1 ? "day is" : "days are")} suspended, a punch-in with no punch-out: enter the punch-out, or ignore the stray punch, first.");
        Status = PayrollSlipStatus.Finalized;
        FinalizedAtUtc = DateTime.UtcNow;
        FinalizedBy = finalizedBy;
    }
}
