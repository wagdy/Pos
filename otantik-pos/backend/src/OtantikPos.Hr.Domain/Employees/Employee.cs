using System.Text.RegularExpressions;
using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Employees;

// The days of the week as flags: the days an employee is off every week. Fixed values, so a row
// keeps its meaning whatever is added.
[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
}

// How an employee is paid (نوع العمالة).
public enum EmploymentType
{
    // A fixed monthly salary (عمالة شهرية): a day costs it over 30, and a day absent is deducted.
    Monthly = 1,

    // A daily wage (عمالة يومية): paid for the days worked, nothing for a day not worked, never
    // an absence.
    Daily = 2,
}

public static class WeekDaysExtensions
{
    public const WeekDays All = (WeekDays)127;

    public static WeekDays Of(DayOfWeek day) => (WeekDays)(1 << (int)day);
}

// Someone on the payroll (الموظفين): the kitchen, the floor, cleaning, the managers. Not a till
// account: most never sign in to a till, and the ones who do are linked to theirs
// (StaffMemberId). Punches from the attendance device find them by the user number they have on
// it (DeviceUserId).
//
// A shift is nine hours, from the time it starts (ShiftStartsAt). Someone on flexible hours has
// no start time: nothing is late for them, and their day is the hours they punch. They are paid
// monthly or daily (EmploymentType): a monthly salary is thirty days' whatever the month's
// length, a day costing it over 30; a daily wage is a day's. An hour is a day's over 9, which is
// what lateness is deducted at and overtime paid at. Each is in a department (DepartmentId).
public sealed partial class Employee : AggregateRoot
{
    public const int ShiftHours = 9;
    public const int PayDaysInMonth = 30;
    public const int MaxNameLength = 200;
    public const int MaxJobTitleLength = 100;
    public const int MaxDeviceUserIdLength = 24;
    public const decimal MaxMonthlySalary = 1_000_000;
    public const decimal MaxDailyWage = 50_000;

    public static readonly TimeSpan ShiftLength = TimeSpan.FromHours(ShiftHours);

    private Employee() { }

    public Employee(
        Guid id,
        string name,
        string jobTitle,
        decimal monthlySalary,
        TimeOnly? shiftStartsAt,
        WeekDays daysOff,
        DateOnly hiredOn,
        string? deviceUserId = null,
        string? staffMemberId = null)
    {
        Id = id;
        Rename(name);
        SetJobTitle(jobTitle);
        SetSalary(monthlySalary);
        SetShift(shiftStartsAt);
        SetDaysOff(daysOff);
        LinkDevice(deviceUserId);
        LinkStaff(staffMemberId);
        HiredOn = hiredOn;
        IsActive = true;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Name { get; private set; } = string.Empty;

    // What they do (الوظيفة): chef, waiter, cashier.
    public string JobTitle { get; private set; } = string.Empty;

    public EmploymentType EmploymentType { get; private set; } = EmploymentType.Monthly;

    // A monthly employee's base salary (الراتب الأساسي), before incentives, allowances and
    // deductions; nothing for a daily one.
    public decimal MonthlySalary { get; private set; }

    // A daily employee's wage for a day worked (الأجر اليومي); nothing for a monthly one.
    public decimal DailyWage { get; private set; }

    public bool IsDaily => EmploymentType == EmploymentType.Daily;

    public Guid DepartmentId { get; private set; } = Department.GeneralId;

    // When the shift starts, local time. It ends nine hours later, past midnight if need be. None
    // for flexible hours.
    public TimeOnly? ShiftStartsAt { get; private set; }

    public bool HasFlexibleHours => ShiftStartsAt is null;

    public WeekDays DaysOff { get; private set; }

    // Their user number on the attendance device (رقم البصمة). Null until they are enrolled.
    public string? DeviceUserId { get; private set; }

    // Their till account, when they have one.
    public string? StaffMemberId { get; private set; }

    // Employed from this day; to LeftOn, their last day, once they have left.
    public DateOnly HiredOn { get; private set; }
    public DateOnly? LeftOn { get; private set; }
    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public decimal DailyRate => IsDaily ? DailyWage : MonthlySalary / PayDaysInMonth;
    public decimal HourlyRate => DailyRate / ShiftHours;
    public decimal MinuteRate => HourlyRate / 60;

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Enter the employee's name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the name under {MaxNameLength} characters.");
        Name = trimmed;
    }

    public void SetJobTitle(string jobTitle)
    {
        var trimmed = jobTitle?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Enter what the employee does: chef, waiter, cashier.");
        if (trimmed.Length > MaxJobTitleLength)
            throw new DomainException($"Keep the job title under {MaxJobTitleLength} characters.");
        JobTitle = trimmed;
    }

    public void SetSalary(decimal monthlySalary) => SetPay(EmploymentType.Monthly, monthlySalary);

    // Paid monthly, at a salary; or daily, at a wage. The other is nothing.
    public void SetPay(EmploymentType type, decimal amount)
    {
        if (!Enum.IsDefined(type))
            throw new DomainException("Choose monthly or daily pay.");
        if (amount < 0)
            throw new DomainException(type == EmploymentType.Daily ? "A daily wage is zero or more." : "A salary is zero or more.");
        if (amount > (type == EmploymentType.Daily ? MaxDailyWage : MaxMonthlySalary))
            throw new DomainException($"That {(type == EmploymentType.Daily ? "wage" : "salary")} is more than a till can pay; check the amount.");
        var money = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        EmploymentType = type;
        MonthlySalary = type == EmploymentType.Monthly ? money : 0;
        DailyWage = type == EmploymentType.Daily ? money : 0;
    }

    public void MoveTo(Guid departmentId)
    {
        if (departmentId == Guid.Empty)
            throw new DomainException("Choose the employee's department.");
        DepartmentId = departmentId;
    }

    // To the minute: the device's punches are, and lateness is counted in minutes. None: flexible
    // hours.
    public void SetShift(TimeOnly? startsAt) => ShiftStartsAt = startsAt is { } start ? new TimeOnly(start.Hour, start.Minute) : null;

    public void SetDaysOff(WeekDays daysOff)
    {
        if ((daysOff & ~WeekDaysExtensions.All) != 0)
            throw new DomainException("Choose the days off from the days of the week.");
        if (daysOff == WeekDaysExtensions.All)
            throw new DomainException("Leave at least one working day in the week.");
        DaysOff = daysOff;
    }

    // Letters and digits, as the device has them; null when they are not on it (yet).
    public void LinkDevice(string? deviceUserId)
    {
        var trimmed = deviceUserId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            DeviceUserId = null;
            return;
        }

        if (trimmed.Length > MaxDeviceUserIdLength || !DeviceUserIdPattern().IsMatch(trimmed))
            throw new DomainException("The attendance device's user number is its letters and digits, as it shows them.");
        DeviceUserId = trimmed;
    }

    public void LinkStaff(string? staffMemberId) =>
        StaffMemberId = string.IsNullOrWhiteSpace(staffMemberId) ? null : staffMemberId.Trim();

    // The day they started, put right.
    public void ChangeStart(DateOnly hiredOn)
    {
        if (LeftOn is { } left && hiredOn > left)
            throw new DomainException("They start on or before their last day.");
        HiredOn = hiredOn;
    }

    // Left on `lastDay`: paid to it, and absent from none of the days after it.
    public void Leave(DateOnly lastDay)
    {
        if (!IsActive)
            throw new DomainException("They have already left.");
        if (lastDay < HiredOn)
            throw new DomainException("Their last day is on or after the day they started.");
        LeftOn = lastDay;
        IsActive = false;
    }

    // Back from `returnsOn`: employed again from that day; the days away are not counted.
    public void Return(DateOnly returnsOn)
    {
        if (IsActive)
            throw new DomainException("They are still employed.");
        if (returnsOn <= LeftOn)
            throw new DomainException("They come back after their last day.");
        HiredOn = returnsOn;
        LeftOn = null;
        IsActive = true;
    }

    public bool IsEmployedOn(DateOnly day) => day >= HiredOn && (LeftOn is null || day <= LeftOn);

    public bool IsDayOff(DateOnly day) => (DaysOff & WeekDaysExtensions.Of(day.DayOfWeek)) != 0;

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex DeviceUserIdPattern();
}
