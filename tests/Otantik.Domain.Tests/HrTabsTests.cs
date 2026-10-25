using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Hr.Domain.Employees;

namespace Otantik.Domain.Tests;

// Employee permissions (صلاحيات الموظفين): the HR & Payroll tabs, one bit each, given to an
// employee or to an accountant.
public class HrTabsTests
{
    [Fact]
    public void Tabs_are_named_one_by_one_in_the_order_shown_and_only_tabs_are_kept()
    {
        Assert.Equal(10, HrTabSet.Each.Count);
        Assert.Equal(HrTabs.All, HrTabSet.Join(HrTabSet.Each));
        Assert.Equal([HrTabs.Now, HrTabs.Attendance, HrTabs.Payroll], HrTabSet.Split(HrTabs.Payroll | HrTabs.Now | HrTabs.Attendance));
        Assert.Empty(HrTabSet.Split(HrTabs.None));
        // A bit that is no tab is left out, and none given is none.
        Assert.Equal(HrTabs.Now, HrTabSet.Join([HrTabs.Now, (HrTabs)(1 << 20)]));
        Assert.Equal(HrTabs.None, HrTabSet.Join(null));
        Assert.True((HrTabs.Now | HrTabs.Payroll).HasAny(HrTabs.Payroll | HrTabs.Advances));
        Assert.False(HrTabs.Now.HasAny(HrTabs.Payroll | HrTabs.Advances));
    }

    [Fact]
    public void An_employee_has_none_until_a_manager_gives_them_some()
    {
        var hassan = new Employee(Guid.NewGuid(), "Hassan", "Supervisor", 9_000, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 5));
        Assert.Equal(HrTabs.None, hassan.HrTabs);
        hassan.SetHrTabs(HrTabs.Now | HrTabs.Attendance | (HrTabs)(1 << 20));
        Assert.Equal(HrTabs.Now | HrTabs.Attendance, hassan.HrTabs);
    }

    [Fact]
    public void An_accountants_tabs_are_kept_by_their_account_with_who_set_them()
    {
        var mona = new StaffHrAccess(" staff-accountant ", HrTabs.Payroll | HrTabs.Advances, "Omar");
        Assert.Equal(("staff-accountant", HrTabs.Payroll | HrTabs.Advances, "Omar"), (mona.StaffId, mona.Tabs, mona.SetBy));
        mona.Set(HrTabs.All, "Sara");
        Assert.Equal((HrTabs.All, "Sara"), (mona.Tabs, mona.SetBy));
        Assert.Throws<DomainException>(() => new StaffHrAccess(" ", HrTabs.Now, "Omar"));
    }
}
