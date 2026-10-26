using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

// A manager's administrative penalty on one employee's day (جزاء إداري), and why: a number of
// hours, deducted from that month's pay at the employee's hourly rate, or an amount of money,
// deducted as it is. One of a manager's own a day, changed or taken off as they decide, until the
// month's pay is finalized. A disciplinary action's penalty deduction (DisciplinaryActionId) is a
// penalty of its own beside it, an amount, made with the action and going with it.
public sealed class AdministrativePenalty : AggregateRoot
{
    public const decimal MaxHours = 72;
    public const decimal MaxAmount = 100_000;
    public const int MaxReasonLength = 300;

    private AdministrativePenalty() { }

    public AdministrativePenalty(Guid employeeId, DateOnly date, decimal hours, string reason, string setBy)
    {
        EmployeeId = employeeId;
        Date = date;
        Change(hours, reason, setBy);
    }

    // An amount of money instead of hours; and the disciplinary action it is the deduction of.
    public static AdministrativePenalty ForAmount(Guid employeeId, DateOnly date, decimal amount, string reason, string setBy, Guid? disciplinaryActionId = null)
    {
        var penalty = new AdministrativePenalty { EmployeeId = employeeId, Date = date, DisciplinaryActionId = disciplinaryActionId };
        penalty.ChangeAmount(amount, reason, setBy);
        return penalty;
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    // None (0) for an amount.
    public decimal Hours { get; private set; }
    public decimal? Amount { get; private set; }
    public Guid? DisciplinaryActionId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string SetBy { get; private set; } = string.Empty;
    public DateTime SetAtUtc { get; private set; }

    // What it takes off their pay.
    public decimal DeductionAt(decimal hourlyRate) => Amount ?? Hours * hourlyRate;

    public void Change(decimal hours, string reason, string setBy)
    {
        if (Amount is not null)
            throw new DomainException("This penalty is an amount of money, not hours.");
        if (hours <= 0 || hours > MaxHours)
            throw new DomainException($"A penalty is more than no hours and at most {MaxHours:0}.");
        if (decimal.Round(hours, 2) != hours)
            throw new DomainException("Give the penalty's hours to two decimal places at most: 1.5, 0.25.");
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Say why the penalty is given.");
        if (trimmed.Length > MaxReasonLength)
            throw new DomainException($"Keep the reason under {MaxReasonLength} characters.");
        Hours = hours;
        Reason = trimmed;
        SetBy = setBy;
        SetAtUtc = DateTime.UtcNow;
    }

    public void ChangeAmount(decimal amount, string reason, string setBy)
    {
        if (Hours != 0)
            throw new DomainException("This penalty is hours, not an amount of money.");
        if (amount <= 0 || amount > MaxAmount)
            throw new DomainException($"A penalty's amount is more than nothing and at most L.E {MaxAmount:N0}.");
        if (decimal.Round(amount, 2) != amount)
            throw new DomainException("Give the penalty's amount to the piastre at most: 150.50.");
        Amount = amount;
        Reason = Reasoned(reason);
        SetBy = setBy;
        SetAtUtc = DateTime.UtcNow;
    }

    private static string Reasoned(string reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Say why the penalty is given.");
        if (trimmed.Length > MaxReasonLength)
            throw new DomainException($"Keep the reason under {MaxReasonLength} characters.");
        return trimmed;
    }
}
