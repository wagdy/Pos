namespace OtantikPos.Hr.Domain.Access;

// The tabs of HR & Payroll (صلاحيات الموظفين), one bit each: what a manager gives an employee, or
// an accountant, to see and work. A manager has them all, always. Kept as a number, the bits
// together; sent and read as their names, ["Now", "Attendance"].
[Flags]
public enum HrTabs
{
    None = 0,
    Now = 1,
    Employees = 1 << 1,
    Departments = 1 << 2,
    Attendance = 1 << 3,
    Corrections = 1 << 4,
    Payroll = 1 << 5,
    Advances = 1 << 6,
    Holidays = 1 << 7,
    Devices = 1 << 8,
    All = Now | Employees | Departments | Attendance | Corrections | Payroll | Advances | Holidays | Devices,
}

public static class HrTabSet
{
    // In the order the tabs are shown.
    public static readonly IReadOnlyList<HrTabs> Each =
    [
        HrTabs.Now, HrTabs.Employees, HrTabs.Departments, HrTabs.Attendance, HrTabs.Corrections, HrTabs.Payroll, HrTabs.Advances,
        HrTabs.Holidays, HrTabs.Devices,
    ];

    // Each tab on its own, for the API: ["Now", "Attendance"].
    public static IReadOnlyList<HrTabs> Split(HrTabs tabs) => Each.Where(t => tabs.HasFlag(t)).ToList();

    // The tabs named, any not a tab left out.
    public static HrTabs Join(IEnumerable<HrTabs>? tabs) => (tabs ?? []).Aggregate(HrTabs.None, (all, t) => all | t) & HrTabs.All;

    public static bool HasAny(this HrTabs held, HrTabs anyOf) => (held & anyOf) != HrTabs.None;
}
