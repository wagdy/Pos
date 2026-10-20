using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Payroll;

// A payroll month is a calendar month, named by its first day.
public static class PayrollMonth
{
    public static DateOnly Of(DateOnly day) => new(day.Year, day.Month, 1);

    public static DateOnly Required(DateOnly month) =>
        month.Day == 1 ? month : throw new DomainException("A payroll month is named by its first day.");

    public static IEnumerable<DateOnly> Days(DateOnly month)
    {
        for (var day = Required(month); day.Month == month.Month; day = day.AddDays(1))
            yield return day;
    }
}

public enum AdjustmentKind
{
    // An incentive or bonus (حوافز).
    Incentive = 1,

    // Pay for leave or a holiday worked (بدل إجازات).
    LeaveAllowance = 2,
}

public enum AdjustmentSource
{
    // Entered by a manager.
    Manual = 1,

    // Made by the till from the timesheet: a day off worked, say.
    Automatic = 2,
}

// Something added to one employee's pay for one month: an incentive, or a leave allowance. Kept
// as entered; a wrong one is voided, which only a month not yet finalized allows (the payroll
// checks that).
public sealed class PayrollAdjustment : AggregateRoot
{
    public const int MaxReasonLength = 300;

    private PayrollAdjustment() { }

    public PayrollAdjustment(
        Guid id,
        Guid employeeId,
        DateOnly month,
        AdjustmentKind kind,
        decimal amount,
        string? reason,
        string recordedBy,
        AdjustmentSource source = AdjustmentSource.Manual,
        DateOnly? forDay = null,
        string? approvedBy = null)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("Say whether it is an incentive or a leave allowance.");
        if (!Enum.IsDefined(source))
            throw new DomainException("Say whether it was entered or worked out.");
        if (amount <= 0)
            throw new DomainException("Enter an amount more than nothing.");
        if (reason?.Trim().Length > MaxReasonLength)
            throw new DomainException($"Keep the reason under {MaxReasonLength} characters.");
        if (forDay is { } day && PayrollMonth.Of(day) != month)
            throw new DomainException("The day it is for is in its month.");
        Id = id;
        EmployeeId = employeeId;
        Month = PayrollMonth.Required(month);
        Kind = kind;
        Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Source = source;
        ForDay = forDay;
        RecordedBy = recordedBy;
        ApprovedBy = approvedBy;
        RecordedAtUtc = DateTime.UtcNow;
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Month { get; private set; }
    public AdjustmentKind Kind { get; private set; }
    public decimal Amount { get; private set; }
    public string? Reason { get; private set; }
    public AdjustmentSource Source { get; private set; }

    // The day it is for, when it is for one: the holiday worked.
    public DateOnly? ForDay { get; private set; }

    public string RecordedBy { get; private set; } = string.Empty;
    public DateTime RecordedAtUtc { get; private set; }
    public string? ApprovedBy { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }
    public string? VoidedBy { get; private set; }
    public bool IsVoided => VoidedAtUtc is not null;

    public void Void(string voidedBy)
    {
        if (IsVoided)
            throw new DomainException("This has already been voided.");
        VoidedAtUtc = DateTime.UtcNow;
        VoidedBy = voidedBy;
    }
}
