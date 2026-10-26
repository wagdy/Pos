using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Attendance;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Hr.Domain.Leaves;

namespace Otantik.Domain.Tests;

// Leaves (الإجازات): their types, each employee's balance of them a year, and the requests a
// manager approves or rejects, counted in the working days they take.
public class LeaveTests
{
    private static readonly LeaveType Annual = new(Guid.NewGuid(), " Annual ", isPaid: true, defaultDaysPerYear: 21);
    private static readonly LeaveType Unpaid = new(Guid.NewGuid(), "Unpaid", isPaid: false, defaultDaysPerYear: 0);
    private static readonly Guid Hassan = Guid.NewGuid();

    // Sunday 1 November 2026 to Saturday 7 November.
    private static readonly DateOnly Sunday = new(2026, 11, 1);

    [Fact]
    public void A_leave_type_is_named_paid_with_its_days_a_year_or_unpaid_with_none()
    {
        Assert.Equal(("Annual", true, 21), (Annual.Name, Annual.IsPaid, Annual.DefaultDaysPerYear));
        Assert.Throws<DomainException>(() => new LeaveType(Guid.NewGuid(), " ", true, 21));
        Assert.Throws<DomainException>(() => new LeaveType(Guid.NewGuid(), new string('x', LeaveType.MaxNameLength + 1), true, 21));
        Assert.Throws<DomainException>(() => new LeaveType(Guid.NewGuid(), "Annual", true, LeaveType.MaxDaysPerYear + 1));
        Assert.Contains("no days a year", Assert.Throws<DomainException>(() => new LeaveType(Guid.NewGuid(), "Unpaid", false, 5)).Message);

        var sick = new LeaveType(Guid.NewGuid(), "Sick", true, 15);
        sick.Change(isPaid: true, defaultDaysPerYear: 30);
        Assert.Equal(30, sick.DefaultDaysPerYear);
    }

    private static Employee HiredOn(DateOnly day) => new(Hassan, "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, day);

    [Fact]
    public void A_balance_opens_at_the_types_days_and_is_taken_from_never_past_what_is_left()
    {
        var balance = EmployeeLeaveBalance.Open(HiredOn(new DateOnly(2025, 3, 1)), Annual, 2026);
        Assert.Equal((21m, 0m, 21m, 2026), (balance.TotalAllocatedDays, balance.UsedDays, balance.RemainingDays, balance.Year));
        Assert.Throws<DomainException>(() => EmployeeLeaveBalance.Open(HiredOn(new DateOnly(2025, 3, 1)), Unpaid, 2026));

        balance.Use(5);
        Assert.Equal((5m, 16m), (balance.UsedDays, balance.RemainingDays));
        Assert.True(balance.Covers(16));
        Assert.False(balance.Covers(17));
        Assert.Equal("Only 16 of its days are left this year.", Assert.Throws<DomainException>(() => balance.Use(17)).Message);
        Assert.Throws<DomainException>(() => balance.Use(0));

        // A manager's to change, never below what is taken.
        balance.Allocate(30.5m, "Omar");
        Assert.Equal((30.5m, 25.5m, "Omar"), (balance.TotalAllocatedDays, balance.RemainingDays, balance.AllocatedBy));
        Assert.Equal("Give a balance in whole or half days: 21, 10.5.", Assert.Throws<DomainException>(() => balance.Allocate(20.25m, "Omar")).Message);
        Assert.Equal("5 of its days are taken already: give at least that many.", Assert.Throws<DomainException>(() => balance.Allocate(4, "Omar")).Message);
        Assert.Throws<DomainException>(() => balance.Allocate(EmployeeLeaveBalance.MaxDays + 1, "Omar"));

        balance.Release(2);
        Assert.Equal(3m, balance.UsedDays);
        Assert.Throws<DomainException>(() => balance.Release(4));
        Assert.Throws<DomainException>(() => new EmployeeLeaveBalance(Hassan, Annual.Id, 1999, 21, "Omar"));
    }

    [Fact]
    public void A_request_runs_within_a_year_takes_a_day_at_least_and_waits_for_a_manager()
    {
        var asked = new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday.AddDays(4), 5, "  Family wedding  ", "Hassan");
        Assert.Equal((LeaveRequestStatus.Pending, "Family wedding", 2026, true), (asked.Status, asked.Reason, asked.Year, asked.IsPending));
        Assert.Null(asked.DecidedById);

        Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday.AddDays(-1), 1, null, "Hassan"));
        Assert.Contains("within one year",
            Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 2), 3, null, "Hassan")).Message);
        Assert.Contains("no working day",
            Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday, 0, null, "Hassan")).Message);
        Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday.AddDays(1), 3, null, "Hassan"));
        Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), 90, null, "Hassan"));
        Assert.True(asked.Overlaps(Sunday.AddDays(4), Sunday.AddDays(9)));
        Assert.False(asked.Overlaps(Sunday.AddDays(5), Sunday.AddDays(9)));

        asked.Approve("staff-omar", "Omar", " Enjoy it ");
        Assert.Equal((LeaveRequestStatus.Approved, "staff-omar", "Omar", "Enjoy it"), (asked.Status, asked.DecidedById, asked.DecidedBy, asked.ManagerNotes));
        Assert.NotNull(asked.DecidedAtUtc);
        // Decided once.
        Assert.Equal("This leave was approved already.", Assert.Throws<DomainException>(() => asked.Reject("staff-omar", "Omar", null)).Message);

        var other = new LeaveRequest(Guid.NewGuid(), Hassan, Unpaid.Id, Sunday, Sunday, 1, null, "Hassan");
        other.Reject("staff-omar", "Omar", "Busy week");
        Assert.Equal((LeaveRequestStatus.Rejected, "Busy week"), (other.Status, other.ManagerNotes));
        Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday, 1, null, "Hassan").Approve(" ", "Omar", null));
    }

    [Fact]
    public void A_leave_takes_only_the_days_they_would_work_while_employed()
    {
        // Off on Fridays and Saturdays, employed from Monday 2 November.
        var hassan = new Employee(Hassan, "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday | WeekDays.Saturday, Sunday.AddDays(1));
        var week = LeaveDays.Of(hassan, Sunday, Sunday.AddDays(6), []);
        Assert.Equal([Sunday.AddDays(1), Sunday.AddDays(2), Sunday.AddDays(3), Sunday.AddDays(4)], week);

        // A public holiday on the Tuesday is no day of it either.
        var holiday = new PublicHoliday(Guid.NewGuid(), "A holiday", Sunday.AddDays(2), Sunday.AddDays(2), "Omar");
        Assert.Equal(3, LeaveDays.Of(hassan, Sunday, Sunday.AddDays(6), [holiday]).Count);
        Assert.Empty(LeaveDays.Of(hassan, Sunday.AddDays(5), Sunday.AddDays(6), []));
    }

    [Fact]
    public void A_new_hire_has_their_share_of_annual_leave_to_the_half_day_and_each_year_opens_afresh()
    {
        var annual = new LeaveType(Guid.NewGuid(), "Annual", isPaid: true, defaultDaysPerYear: 21, proratedForNewHires: true);
        var sick = new LeaveType(Guid.NewGuid(), "Sick", isPaid: true, defaultDaysPerYear: 7);
        Assert.True(annual.ProratedForNewHires);
        Assert.False(new LeaveType(Guid.NewGuid(), "Unpaid", false, 0, proratedForNewHires: true).ProratedForNewHires);

        // Joining on 2 July: 183 days of 365 left, 10.5 of 21; their sick days in full.
        var july = HiredOn(new DateOnly(2026, 7, 2));
        Assert.Equal(10.5m, EmployeeLeaveBalance.Open(july, annual, 2026).TotalAllocatedDays);
        Assert.Equal(7m, EmployeeLeaveBalance.Open(july, sick, 2026).TotalAllocatedDays);
        // On 1 January, or the years after: all of it; nothing carried over from before.
        Assert.Equal(21m, EmployeeLeaveBalance.DaysFor(HiredOn(new DateOnly(2026, 1, 1)), annual, 2026));
        Assert.Equal(21m, EmployeeLeaveBalance.DaysFor(july, annual, 2027));
        // In November: 61 days left, 3.5.
        Assert.Equal(3.5m, EmployeeLeaveBalance.DaysFor(HiredOn(new DateOnly(2026, 11, 1)), annual, 2026));
        // Not employed that year yet: none.
        Assert.Equal(0m, EmployeeLeaveBalance.DaysFor(july, annual, 2025));
        // A half kept, a whole day taken from it.
        var balance = EmployeeLeaveBalance.Open(july, annual, 2026);
        balance.Use(10);
        Assert.Equal("Only 0.5 of its days are left this year.", Assert.Throws<DomainException>(() => balance.Use(1)).Message);
    }

    [Fact]
    public void An_approved_leave_is_cancelled_from_a_day_on_or_whole_before_it_begins()
    {
        LeaveRequest Approved()
        {
            var r = new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday.AddDays(4), 5, null, "Hassan");
            r.Approve("staff-omar", "Omar", null);
            return r;
        }

        // Recalled on the Wednesday: Sunday to Tuesday stay taken, two days given back.
        var recalled = Approved();
        recalled.Cancel(Sunday.AddDays(3), 2, "staff-omar", "Omar", " Recalled ");
        Assert.Equal((LeaveRequestStatus.Approved, (DateOnly?)Sunday.AddDays(3), 2, 3, "Recalled"),
            (recalled.Status, recalled.CancelledFrom, recalled.CancelledDays, recalled.DaysTaken, recalled.CancellationNotes));
        Assert.Equal("This leave was cut short already.", Assert.Throws<DomainException>(() => recalled.Cancel(Sunday.AddDays(4), 1, "staff-omar", "Omar", null)).Message);

        // Before it began: none of it stands.
        var off = Approved();
        off.Cancel(Sunday.AddDays(-2), 5, "staff-omar", "Omar", "Trip off");
        Assert.Equal((LeaveRequestStatus.Cancelled, (DateOnly?)Sunday, 0), (off.Status, off.CancelledFrom, off.DaysTaken));
        Assert.Throws<DomainException>(() => Approved().Cancel(Sunday, 3, "staff-omar", "Omar", null));
        Assert.Contains("nothing of it left", Assert.Throws<DomainException>(() => Approved().Cancel(Sunday.AddDays(5), 0, "staff-omar", "Omar", null)).Message);
        Assert.Contains("reject it instead",
            Assert.Throws<DomainException>(() => new LeaveRequest(Guid.NewGuid(), Hassan, Annual.Id, Sunday, Sunday, 1, null, "Hassan").Cancel(Sunday, 1, "staff-omar", "Omar", null)).Message);
    }
}
