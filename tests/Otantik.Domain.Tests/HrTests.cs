using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Payroll;
using OtantikPos.Inventory.Domain.Treasury;

namespace Otantik.Domain.Tests;

// HR's rules: who is on the payroll, how their punches become a day's hours, lateness and
// overtime, and how a month's pay is made of it.
public class HrTests
{
    // Monday 12 October 2026, and Friday the 16th.
    private static readonly DateOnly Monday = new(2026, 10, 12);
    private static readonly DateOnly Friday = new(2026, 10, 16);
    private static readonly DateTime Later = new(2026, 10, 25, 12, 0, 0);
    private static readonly AttendancePolicy TenMinutesGrace = new(10, 3);

    // 5,400 a month: 180 a day, 20 an hour.
    private static Employee Chef(TimeOnly? shift = null, DateOnly? hiredOn = null) =>
        new(Guid.NewGuid(), " Hassan ", " Chef ", 5_400, shift ?? new TimeOnly(10, 0), WeekDays.Friday, hiredOn ?? new DateOnly(2026, 1, 1), "7");

    private static DateTime At(DateOnly day, int hour, int minute = 0) => day.ToDateTime(new TimeOnly(hour, minute));

    private static DayEvaluation Day(Employee employee, DateOnly day, params DateTime[] punches) =>
        Timesheet.Evaluate(employee, day, punches, null, TenMinutesGrace, Later);

    [Fact]
    public void An_employee_has_a_nine_hour_shift_and_rates_from_a_thirty_day_month()
    {
        var chef = Chef(new TimeOnly(10, 0, 42));

        Assert.Equal(("Hassan", "Chef", "7", new TimeOnly(10, 0)), (chef.Name, chef.JobTitle, chef.DeviceUserId, chef.ShiftStartsAt));
        Assert.Equal((180m, 20m), (chef.DailyRate, chef.HourlyRate));
        Assert.Equal(9, Employee.ShiftHours);
        Assert.True(chef.IsDayOff(Friday));
        Assert.False(chef.IsDayOff(Monday));

        Assert.Throws<DomainException>(() => chef.Rename(" "));
        Assert.Throws<DomainException>(() => chef.SetSalary(-1));
        Assert.Throws<DomainException>(() => chef.SetDaysOff(WeekDaysExtensions.All));
        Assert.Throws<DomainException>(() => chef.LinkDevice("7 b"));
        chef.LinkDevice(" ");
        Assert.Null(chef.DeviceUserId);
    }

    [Fact]
    public void One_who_leaves_is_employed_to_their_last_day_and_again_from_their_return()
    {
        var chef = Chef(hiredOn: new DateOnly(2026, 10, 5));
        Assert.False(chef.IsEmployedOn(new DateOnly(2026, 10, 4)));

        Assert.Throws<DomainException>(() => chef.Leave(new DateOnly(2026, 10, 4)));
        chef.Leave(new DateOnly(2026, 10, 20));
        Assert.False(chef.IsActive);
        Assert.True(chef.IsEmployedOn(new DateOnly(2026, 10, 20)));
        Assert.False(chef.IsEmployedOn(new DateOnly(2026, 10, 21)));

        Assert.Throws<DomainException>(() => chef.Return(new DateOnly(2026, 10, 20)));
        chef.Return(new DateOnly(2026, 11, 1));
        Assert.Equal((true, (DateOnly?)null, new DateOnly(2026, 11, 1)), (chef.IsActive, chef.LeftOn, chef.HiredOn));
    }

    [Fact]
    public void A_day_on_time_within_the_grace_is_worked_with_nothing_short()
    {
        var chef = Chef();

        var early = Day(chef, Monday, At(Monday, 9, 55), At(Monday, 19));
        Assert.Equal(new DayEvaluation(Monday, DayStatus.Worked, At(Monday, 9, 55), At(Monday, 19), 2, 545, 0, 0, 0, 0), early);

        var withinGrace = Day(chef, Monday, At(Monday, 10, 8), At(Monday, 19));
        Assert.Equal((0, 0), (withinGrace.LateMinutes, withinGrace.ShortMinutes));
    }

    [Fact]
    public void Late_past_the_grace_counts_from_the_start_and_time_after_the_shift_is_overtime()
    {
        var day = Day(Chef(), Monday, At(Monday, 10, 25), At(Monday, 19, 30));

        Assert.Equal((25, 0, 30, 545), (day.LateMinutes, day.EarlyLeaveMinutes, day.OvertimeMinutes, day.WorkedMinutes));
    }

    [Fact]
    public void Time_out_between_punches_and_leaving_early_are_short_of_the_shift()
    {
        var day = Day(Chef(), Monday, At(Monday, 10), At(Monday, 14), At(Monday, 15), At(Monday, 18, 30));

        Assert.Equal((0, 30, 60, 0, 450), (day.LateMinutes, day.EarlyLeaveMinutes, day.AwayMinutes, day.OvertimeMinutes, day.WorkedMinutes));
        Assert.Equal(90, day.ShortMinutes);
    }

    [Fact]
    public void A_finger_put_on_twice_is_one_scan()
    {
        var day = Day(Chef(), Monday, At(Monday, 10), At(Monday, 10, 2), At(Monday, 19));

        Assert.Equal((DayStatus.Worked, 2, 540), (day.Status, day.Punches, day.WorkedMinutes));
    }

    [Fact]
    public void A_shift_past_midnight_is_one_day()
    {
        var evening = Chef(new TimeOnly(16, 0));
        var next = Monday.AddDays(1);

        var day = Day(evening, Monday, At(Monday, 15, 50), At(next, 1, 30));

        Assert.Equal((DayStatus.Worked, 580, 0, 30), (day.Status, day.WorkedMinutes, day.LateMinutes, day.OvertimeMinutes));
        Assert.Equal((At(Monday, 10), At(next, 10)), Timesheet.Window(evening, Monday));
        // Nor is the punch after midnight the next day's.
        var tuesday = Day(evening, next, At(next, 1, 30));
        Assert.Equal((DayStatus.Absent, 0), (tuesday.Status, tuesday.Punches));
    }

    [Fact]
    public void Coming_after_the_shift_has_ended_is_the_whole_shift_short()
    {
        var day = Day(Chef(), Monday, At(Monday, 20), At(Monday, 22));

        Assert.Equal((540, 0, 120, 540), (day.LateMinutes, day.EarlyLeaveMinutes, day.OvertimeMinutes, day.ShortMinutes));
    }

    [Fact]
    public void A_day_without_punches_is_an_absence_unless_it_is_a_day_off_or_marked()
    {
        var chef = Chef();

        Assert.Equal(DayStatus.Absent, Day(chef, Monday).Status);
        Assert.Equal(DayStatus.DayOff, Day(chef, Friday).Status);
        Assert.Equal(DayStatus.Leave, Timesheet.Evaluate(chef, Monday, [], DayMarkKind.Leave, TenMinutesGrace, Later).Status);
        Assert.Equal(DayStatus.DayOff, Timesheet.Evaluate(chef, Monday, [], DayMarkKind.DayOff, TenMinutesGrace, Later).Status);
        // Not over yet: not absent yet.
        Assert.Equal(DayStatus.Open, Timesheet.Evaluate(chef, Monday, [], null, TenMinutesGrace, At(Monday, 12)).Status);
        // Before they started.
        Assert.Equal(DayStatus.NotEmployed, Day(Chef(hiredOn: Friday), Monday, At(Monday, 10), At(Monday, 19)).Status);
    }

    [Fact]
    public void A_day_off_worked_is_hours_with_no_shift_to_be_late_for()
    {
        var day = Day(Chef(), Friday, At(Friday, 12), At(Friday, 16));

        Assert.Equal(new DayEvaluation(Friday, DayStatus.WorkedOnDayOff, At(Friday, 12), At(Friday, 16), 2, 240, 0, 0, 0, 0), day);
    }

    [Fact]
    public void An_odd_number_of_punches_is_missing_one_once_the_day_is_over()
    {
        var chef = Chef();

        var over = Day(chef, Monday, At(Monday, 10, 30));
        Assert.Equal((DayStatus.MissingPunch, 30, (DateTime?)null), (over.Status, over.LateMinutes, over.LastOut));
        Assert.Equal(DayStatus.Open, Timesheet.Evaluate(chef, Monday, [At(Monday, 10)], null, TenMinutesGrace, At(Monday, 15)).Status);
    }

    [Fact]
    public void A_punch_is_kept_to_the_second_on_the_wall_clock_and_ignored_rather_than_removed()
    {
        var at = new DateTime(2026, 10, 12, 10, 0, 7, 450, DateTimeKind.Utc);
        var punch = AttendanceLog.FromDevice("CQZ7232060112", "7", at, 0, 1, null);

        Assert.Equal((new DateTime(2026, 10, 12, 10, 0, 7), DateTimeKind.Unspecified), (punch.PunchedAt, punch.PunchedAt.Kind));
        Assert.False(punch.Counts);
        var employee = Guid.NewGuid();
        punch.AssignTo(employee);
        Assert.True(punch.Counts);
        Assert.Throws<DomainException>(() => punch.AssignTo(Guid.NewGuid()));

        Assert.Throws<DomainException>(() => punch.Ignore(" ", "Omar"));
        punch.Ignore("Scanned twice", "Omar");
        Assert.False(punch.Counts);
        punch.Restore();
        Assert.True(punch.Counts);

        Assert.Throws<DomainException>(() => AttendanceLog.Manual(Guid.NewGuid(), employee, at, "", "Omar"));
        Assert.Equal(PunchSource.Manual, AttendanceLog.Manual(Guid.NewGuid(), employee, at, "The device was down", "Omar").Source);
    }

    [Fact]
    public void A_device_first_seen_waits_for_a_manager()
    {
        var device = AttendanceDevice.FirstSeen(" CQZ7232060112 ", DateTime.UtcNow, "192.168.1.40");
        Assert.Equal(("CQZ7232060112", false), (device.SerialNumber, device.IsApproved));

        Assert.Throws<DomainException>(() => device.Approve(" ", "Omar"));
        device.Approve("Kitchen door", "Omar");
        Assert.True(device.IsApproved);
        device.Revoke();
        Assert.False(device.IsApproved);

        Assert.Throws<DomainException>(() => AttendanceDevice.FirstSeen("SN 1; drop", DateTime.UtcNow, null));
    }

    [Fact]
    public void A_months_pay_is_its_salary_and_additions_less_its_advances_lateness_and_absence()
    {
        var chef = Chef();
        var october = new DateOnly(2026, 10, 1);
        var days = PayrollMonth.Days(october).Select(d =>
            chef.IsDayOff(d) ? Nothing(d, DayStatus.DayOff)
            : d.Day is 5 or 6 ? Nothing(d, DayStatus.Absent)
            : d.Day == 12 ? new DayEvaluation(d, DayStatus.Worked, null, null, 2, 480, 60, 30, 0, 0)
            : d.Day == 13 ? new DayEvaluation(d, DayStatus.Worked, null, null, 2, 660, 0, 0, 0, 120)
            : new DayEvaluation(d, DayStatus.Worked, null, null, 2, 540, 0, 0, 0, 0)).ToList();
        var voided = new PayrollAdjustment(Guid.NewGuid(), chef.Id, october, AdjustmentKind.Incentive, 999, null, "Omar");
        voided.Void("Omar");
        PayrollAdjustment[] adjustments =
        [
            new(Guid.NewGuid(), chef.Id, october, AdjustmentKind.Incentive, 500, "Best month", "Omar"),
            new(Guid.NewGuid(), chef.Id, october, AdjustmentKind.LeaveAllowance, 180, "Worked the 6th of October", "Omar", forDay: new DateOnly(2026, 10, 6)),
            voided,
        ];

        var figures = PayrollCalculator.Calculate(chef, october, days, adjustments, advances: 1_000, broughtForward: 0);

        Assert.Equal(
            (5_400m, 31, 31, 5_400m, 500m, 180m, 120, 40m, 90, 30m, 2, 360m, 1_000m, 0m),
            (figures.MonthlySalary, figures.DaysInMonth, figures.DaysEmployed, figures.BaseSalary, figures.Incentives, figures.LeaveAllowances,
                figures.OvertimeMinutes, figures.OvertimePay, figures.ShortMinutes, figures.LatenessPenalty, figures.AbsentDays, figures.AbsencePenalty,
                figures.Advances, figures.BroughtForward));
        // 5,400 + 500 + 180 + 40 = 6,120, less 1,000 + 30 + 360.
        Assert.Equal((6_120m, 1_390m, 4_730m, 4_730m, 0m), (figures.Gross, figures.Deductions, figures.Net, figures.NetPayable, figures.CarriedForward));
        Assert.Equal(5_730m, figures.WagesCost);
        Assert.Equal((0, 0, 0), (figures.MissingPunchDays, figures.OpenDays, figures.DaysOffWorked));
        Assert.Equal(31 - 5 - 2, figures.DaysWorked);

        var slip = new PayrollSlip(Guid.NewGuid(), chef.Id, october, figures);
        slip.Finalize("Omar");
        Assert.Equal((PayrollSlipStatus.Finalized, 4_730m), (slip.Status, slip.NetPayable));
        Assert.Throws<DomainException>(() => slip.Recalculate(figures));
    }

    [Fact]
    public void A_month_started_part_way_pays_its_share_and_one_that_owes_more_carries_it_over()
    {
        var chef = Chef(hiredOn: new DateOnly(2026, 10, 22));
        var october = new DateOnly(2026, 10, 1);
        var days = PayrollMonth.Days(october).Where(chef.IsEmployedOn)
            .Select(d => chef.IsDayOff(d) ? Nothing(d, DayStatus.DayOff) : new DayEvaluation(d, DayStatus.Worked, null, null, 2, 540, 0, 0, 0, 0));

        var figures = PayrollCalculator.Calculate(chef, october, days, [], advances: 2_000, broughtForward: 150);

        // 10 of October's 31 days.
        Assert.Equal((10, 1_741.94m), (figures.DaysEmployed, figures.BaseSalary));
        Assert.Equal((0m, 408.06m), (figures.NetPayable, figures.CarriedForward));
    }

    [Fact]
    public void A_slip_is_finalized_only_once_every_day_is_settled()
    {
        var chef = Chef();
        var october = new DateOnly(2026, 10, 1);
        var allButOne = PayrollMonth.Days(october).Where(d => d.Day != 31).Select(d => Nothing(d, chef.IsDayOff(d) ? DayStatus.DayOff : DayStatus.Absent)).ToList();

        var open = new PayrollSlip(Guid.NewGuid(), chef.Id, october, PayrollCalculator.Calculate(chef, october, allButOne, [], 0, 0));
        Assert.Equal(1, open.Figures.OpenDays);
        Assert.Throws<DomainException>(() => open.Finalize("Omar"));

        var missing = PayrollCalculator.Calculate(chef, october, [.. allButOne, Nothing(new DateOnly(2026, 10, 31), DayStatus.MissingPunch)], [], 0, 0);
        var slip = new PayrollSlip(Guid.NewGuid(), chef.Id, october, missing);
        Assert.Equal("1 day is missing a punch: enter it, or ignore the extra one, first.", Assert.Throws<DomainException>(() => slip.Finalize("Omar")).Message);

        Assert.Throws<DomainException>(() => PayrollCalculator.Calculate(chef, october, [Nothing(new DateOnly(2026, 11, 1), DayStatus.Absent)], [], 0, 0));
        Assert.Throws<DomainException>(() => PayrollCalculator.Calculate(
            chef, october, [], [new PayrollAdjustment(Guid.NewGuid(), Guid.NewGuid(), october, AdjustmentKind.Incentive, 1, null, "Omar")], 0, 0));
        Assert.Throws<DomainException>(() => new PayrollAdjustment(Guid.NewGuid(), chef.Id, new DateOnly(2026, 10, 2), AdjustmentKind.Incentive, 1, null, "Omar"));
        Assert.Throws<DomainException>(() => new PayrollAdjustment(Guid.NewGuid(), chef.Id, october, AdjustmentKind.Incentive, 0, null, "Omar"));
    }

    [Fact]
    public void An_advance_leaves_the_safe_or_the_takings_at_once_and_counts_in_a_payroll_month()
    {
        var employee = Guid.NewGuid();
        var october = new DateOnly(2026, 10, 1);

        var fromSafe = SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, employee, october, null, "Omar");
        Assert.Equal((SafeTransactionKind.EmployeeAdvance, CashFund.Safe, -300m, (Guid?)employee, (DateOnly?)october),
            (fromSafe.Kind, fromSafe.Fund, fromSafe.Amount, fromSafe.EmployeeId, fromSafe.PayrollMonth));
        Assert.Null(fromSafe.ExpenseItemId);

        var fromTakings = SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, employee, october, null, "Omar", revenueDate: Monday);
        Assert.Equal(CashFund.DayRevenue, fromTakings.Fund);
        // Next month's pay, when October's payroll is closed; never an earlier month's.
        Assert.Equal(new DateOnly(2026, 11, 1),
            SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, employee, new DateOnly(2026, 11, 1), null, "Omar").PayrollMonth);
        Assert.Throws<DomainException>(() => SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, employee, new DateOnly(2026, 9, 1), null, "Omar"));
        Assert.Throws<DomainException>(() => SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 300, Monday, employee, Monday, null, "Omar"));
        Assert.Throws<DomainException>(() => SafeTransaction.EmployeeAdvance(Guid.NewGuid(), 0, Monday, employee, october, null, "Omar"));

        var reversal = fromSafe.Void(Guid.NewGuid(), Friday, "Omar");
        Assert.Equal((300m, (Guid?)employee, (DateOnly?)october), (reversal.Amount, reversal.EmployeeId, reversal.PayrollMonth));

        Assert.Equal(-4_730m, SafeTransaction.SalaryPayment(Guid.NewGuid(), 4_730, new DateOnly(2026, 11, 1), employee, october, null, "Omar").Amount);
        Assert.Throws<DomainException>(() => SafeTransaction.SalaryPayment(Guid.NewGuid(), 4_730, new DateOnly(2026, 9, 30), employee, october, null, "Omar"));
    }

    private static DayEvaluation Nothing(DateOnly day, DayStatus status) => new(day, status, null, null, 0, 0, 0, 0, 0, 0);
}
