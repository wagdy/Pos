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
// no start time: nothing is late for them, and their day is the hours they punch. A month's pay
// is thirty days' whatever the month's length: a day costs the salary over 30, an hour a day's
// over 9, which is what lateness and absence are deducted at and overtime paid at.
public sealed partial class Employee : AggregateRoot
{
    public const int ShiftHours = 9;
    public const int PayDaysInMonth = 30;
    public const int MaxNameLength = 200;
    public const int MaxJobTitleLength = 100;
    public const int MaxDeviceUserIdLength = 24;
    public const decimal MaxMonthlySalary = 1_000_000;

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

    // The base monthly salary (الراتب الأساسي), before incentives, allowances and deductions.
    public decimal MonthlySalary { get; private set; }

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

    public decimal DailyRate => MonthlySalary / PayDaysInMonth;
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

    public void SetSalary(decimal monthlySalary)
    {
        if (monthlySalary < 0)
            throw new DomainException("A salary is zero or more.");
        if (monthlySalary > MaxMonthlySalary)
            throw new DomainException("That salary is more than a till can pay; check the amount.");
        MonthlySalary = Math.Round(monthlySalary, 2, MidpointRounding.AwayFromZero);
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
