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
    public const int MaxNationalIdLength = 20;
    public const int MaxMobileNumberLength = 16;
    public const int MaxIdPhotoBytes = 5 * 1024 * 1024;
    public const int MaxIdPhotoFileNameLength = 200;

    // The self-service portal's PIN: 4 to 6 digits. A PIN has few possibilities, so guessing has
    // to be slow: five misses lock it for five minutes, as a till PIN does.
    public const int MinPinLength = 4;
    public const int MaxPinLength = 6;
    public const int MaxFailedPinAttempts = 5;
    public static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(5);

    // A photo (JPEG, PNG or WebP) or a PDF of their ID card.
    public static readonly IReadOnlySet<string> IdPhotoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "application/pdf",
    };

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

    // Their full name (الاسم بالكامل), as on their ID card.
    public string Name { get; private set; } = string.Empty;

    // The number on their ID card (رقم البطاقة): an Egyptian national ID's 14 digits, or a
    // passport's letters and digits. None until it is entered.
    public string? NationalId { get; private set; }

    // Their mobile (رقم الموبايل), digits only, a leading + for an international one.
    public string? MobileNumber { get; private set; }

    // A photo or scan of their ID card (صورة البطاقة); its bytes are kept apart
    // (IEmployeeIdPhotoStore), so a list of employees never loads them.
    public string? IdPhotoFileName { get; private set; }
    public string? IdPhotoContentType { get; private set; }
    public int? IdPhotoBytes { get; private set; }
    public DateTime? IdPhotoUploadedAtUtc { get; private set; }
    public string? IdPhotoUploadedBy { get; private set; }
    public bool HasIdPhoto => IdPhotoContentType is not null;

    // What they do (الوظيفة): chef, waiter, cashier; their role's name once they hold one.
    public string JobTitle { get; private set; } = string.Empty;

    // Their role in their department; none only before roles were kept.
    public Guid? JobRoleId { get; private set; }

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

    // Their PIN for the self-service portal (البوابة), hashed: never kept, nor shown, as typed. They
    // sign in with their mobile number and it. None until a manager sets one.
    public string? PinHash { get; private set; }
    public DateTime? PinSetAtUtc { get; private set; }
    public int FailedPinAttempts { get; private set; }
    public DateTime? PinLockedUntilUtc { get; private set; }
    public bool HasPin => PinHash is not null;

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

    // One of their department's roles, and titled by it.
    public void AssignRole(JobRole role)
    {
        if (role.DepartmentId != DepartmentId)
            throw new DomainException($"The {role.Name} role is another department's: choose one of theirs.");
        JobRoleId = role.Id;
        SetJobTitle(role.Name);
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

    // Arabic-Indic digits as their ASCII ones, spaces and dashes aside. Fourteen digits is an
    // Egyptian national ID; letters and digits, a passport's number. Null: not entered.
    public void SetNationalId(string? nationalId)
    {
        var number = Digits(nationalId)?.ToUpperInvariant();
        if (number is null)
        {
            NationalId = null;
            return;
        }

        if (number.All(char.IsAsciiDigit) ? number.Length != 14 : number.Length is < 5 or > MaxNationalIdLength || !PassportPattern().IsMatch(number))
            throw new DomainException("A national ID is the card's 14 digits; a passport's number, its letters and digits.");
        NationalId = number;
    }

    // Digits, a leading + for an international number: 01012345678, or +201012345678.
    public void SetMobileNumber(string? mobileNumber)
    {
        var number = Digits(mobileNumber, "()");
        if (number is null)
        {
            MobileNumber = null;
            return;
        }

        if (!MobilePattern().IsMatch(number))
            throw new DomainException("A mobile number is its digits: 01012345678, or +201012345678 from abroad.");
        MobileNumber = number;
    }

    // Their ID card, photographed or scanned; replacing one there was.
    public void AttachIdPhoto(string fileName, string contentType, int bytes, string uploadedBy)
    {
        if (!IdPhotoTypes.Contains(contentType))
            throw new DomainException("Upload a photo (JPEG, PNG or WebP) or a PDF of the ID card.");
        if (bytes is <= 0 or > MaxIdPhotoBytes)
            throw new DomainException("The file must be under 5 MB.");
        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        IdPhotoFileName = name.Length == 0 ? "id-card" : name[..Math.Min(name.Length, MaxIdPhotoFileNameLength)];
        IdPhotoContentType = contentType.ToLowerInvariant();
        IdPhotoBytes = bytes;
        IdPhotoUploadedAtUtc = DateTime.UtcNow;
        IdPhotoUploadedBy = uploadedBy;
    }

    public void RemoveIdPhoto()
    {
        if (!HasIdPhoto)
            throw new DomainException("There is no photo of their ID card to take off.");
        IdPhotoFileName = null;
        IdPhotoContentType = null;
        IdPhotoBytes = null;
        IdPhotoUploadedAtUtc = null;
        IdPhotoUploadedBy = null;
    }

    // A mobile number as typed, as it is kept (01012345678); null when it cannot be one. What the
    // portal looks them up by.
    public static string? Mobile(string? typed) => Digits(typed, "()") is { } number && MobilePattern().IsMatch(number) ? number : null;

    // A PIN as typed, Arabic digits as ASCII ones: what is hashed. Refused unless 4 to 6 digits.
    public static string Pin(string? typed)
    {
        var pin = Digits(typed) ?? string.Empty;
        if (pin.Length is < MinPinLength or > MaxPinLength || !pin.All(char.IsAsciiDigit))
            throw new DomainException($"A PIN is {MinPinLength} to {MaxPinLength} digits.");
        return pin;
    }

    // A new PIN, hashed by the caller; a lockout cleared with it, so a manager setting one is how
    // someone locked out gets back in before the five minutes are up. They need a mobile number
    // to sign in with.
    public void SetPinHash(string pinHash)
    {
        if (string.IsNullOrWhiteSpace(pinHash))
            throw new DomainException("A PIN is kept hashed.");
        if (MobileNumber is null)
            throw new DomainException("Enter their mobile number first: they sign in to the portal with it.");
        PinHash = pinHash;
        PinSetAtUtc = DateTime.UtcNow;
        FailedPinAttempts = 0;
        PinLockedUntilUtc = null;
    }

    public void ClearPin()
    {
        PinHash = null;
        PinSetAtUtc = null;
        FailedPinAttempts = 0;
        PinLockedUntilUtc = null;
    }

    public bool IsPinLockedAt(DateTime utcNow) => PinLockedUntilUtc > utcNow;

    // A wrong PIN: the fifth in a row locks it for five minutes.
    public void PinRefused(DateTime utcNow)
    {
        if (++FailedPinAttempts < MaxFailedPinAttempts)
            return;
        FailedPinAttempts = 0;
        PinLockedUntilUtc = utcNow + PinLockout;
    }

    // Signed in: the misses forgotten. With a rehash, the PIN kept again under a newer hash.
    public void PinAccepted(string? rehashed = null)
    {
        FailedPinAttempts = 0;
        PinLockedUntilUtc = null;
        if (rehashed is not null)
            PinHash = rehashed;
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

    // "٠١٠ ١٢٣٤-٥٦٧٨" as "01012345678": Arabic-Indic and Persian digits as ASCII ones, without the
    // spaces, dashes and dots written between them, nor the characters in `also`. Null when
    // nothing is left.
    private static string? Digits(string? value, string also = "")
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var kept = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (c is >= '٠' and <= '٩')
                kept.Append((char)('0' + (c - '٠')));
            else if (c is >= '۰' and <= '۹')
                kept.Append((char)('0' + (c - '۰')));
            else if (!char.IsWhiteSpace(c) && c is not ('-' or '.') && !also.Contains(c))
                kept.Append(c);
        }

        return kept.Length == 0 ? null : kept.ToString();
    }

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex DeviceUserIdPattern();

    [GeneratedRegex("^[A-Z0-9]+$")]
    private static partial Regex PassportPattern();

    [GeneratedRegex("^\\+?[0-9]{7,15}$")]
    private static partial Regex MobilePattern();
}
