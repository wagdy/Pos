using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Employees;

namespace OtantikPos.Hr.Domain.Leaves;

// An employee's days of a paid leave in a year (رصيد الإجازات), in half days: given
// (TotalAllocatedDays) and taken (UsedDays). A year opens at the type's days a year, a new
// hire's at their share of them for a type shared so (Open); it is a manager's to change after.
// Taken as requests are approved, whole days each; given back when one is cancelled. What is left
// is what can still be asked for, and nothing carries over: each year opens afresh on 1 January.
// One to an employee, a type and a year.
public sealed class EmployeeLeaveBalance : AggregateRoot
{
    public const decimal MaxDays = 365;
    public const int FirstYear = 2000;
    public const int LastYear = 2100;

    private EmployeeLeaveBalance() { }

    public EmployeeLeaveBalance(Guid employeeId, Guid leaveTypeId, int year, decimal totalAllocatedDays, string allocatedBy)
    {
        if (year is < FirstYear or > LastYear)
            throw new DomainException($"A balance is for a year from {FirstYear} to {LastYear}.");
        EmployeeId = employeeId;
        LeaveTypeId = leaveTypeId;
        Year = year;
        Allocate(totalAllocatedDays, allocatedBy);
    }

    // The year's balance as it opens: the type's days a year, or for one hired during it and a
    // type shared for new hires, their share by the days left from their first day, to the half
    // day (joining on 2 July: 183 days of 365, 10.5 of 21).
    public static EmployeeLeaveBalance Open(Employee employee, LeaveType type, int year)
    {
        if (!type.IsPaid)
            throw new DomainException($"{type.Name} is unpaid: it has no balance.");
        return new EmployeeLeaveBalance(employee.Id, type.Id, year, DaysFor(employee, type, year), "Opened with the year");
    }

    public static decimal DaysFor(Employee employee, LeaveType type, int year)
    {
        var first = new DateOnly(year, 1, 1);
        var last = new DateOnly(year, 12, 31);
        if (!type.ProratedForNewHires || employee.HiredOn <= first)
            return type.DefaultDaysPerYear;
        if (employee.HiredOn > last)
            return 0;
        var left = last.DayNumber - employee.HiredOn.DayNumber + 1;
        var daysInYear = last.DayNumber - first.DayNumber + 1;
        return Math.Round(type.DefaultDaysPerYear * (decimal)left / daysInYear * 2, MidpointRounding.AwayFromZero) / 2;
    }

    public Guid EmployeeId { get; private set; }
    public Guid LeaveTypeId { get; private set; }
    public int Year { get; private set; }
    public decimal TotalAllocatedDays { get; private set; }
    public decimal UsedDays { get; private set; }
    public string AllocatedBy { get; private set; } = string.Empty;
    public DateTime AllocatedAtUtc { get; private set; }

    public decimal RemainingDays => TotalAllocatedDays - UsedDays;

    public bool Covers(decimal days) => days <= RemainingDays;

    // A manager's: in half days, never fewer than are taken already.
    public void Allocate(decimal days, string allocatedBy)
    {
        if (days is < 0 or > MaxDays)
            throw new DomainException($"A year's balance is 0 to {MaxDays:0} days.");
        if (days * 2 != Math.Floor(days * 2))
            throw new DomainException("Give a balance in whole or half days: 21, 10.5.");
        if (days < UsedDays)
            throw new DomainException($"{Days(UsedDays)} of its days are taken already: give at least that many.");
        TotalAllocatedDays = days;
        AllocatedBy = allocatedBy;
        AllocatedAtUtc = DateTime.UtcNow;
    }

    // An approved request's days.
    public void Use(int days)
    {
        if (days <= 0)
            throw new DomainException("A leave takes a day at least.");
        if (!Covers(days))
            throw new DomainException(RemainingDays <= 0 ? "No days of it are left this year." : $"Only {Days(RemainingDays)} of its days are left this year.");
        UsedDays += days;
    }

    // Days given back, as when an approved leave is cancelled.
    public void Release(int days)
    {
        if (days <= 0 || days > UsedDays)
            throw new DomainException($"Only {Days(UsedDays)} of its days are taken.");
        UsedDays -= days;
    }

    // 10.5, 21: as many as there are, without a needless ".0".
    public static string Days(decimal days) => days.ToString(days == Math.Floor(days) ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
}
