using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Attendance;

// A manager's administrative penalty on one employee's day (جزاء إداري): a number of hours, and
// why. Deducted from that month's pay at the employee's hourly rate. One a day; changed or taken
// off as the manager decides, until the month's pay is finalized.
public sealed class AdministrativePenalty : AggregateRoot
{
    public const decimal MaxHours = 72;
    public const int MaxReasonLength = 300;

    private AdministrativePenalty() { }

    public AdministrativePenalty(Guid employeeId, DateOnly date, decimal hours, string reason, string setBy)
    {
        EmployeeId = employeeId;
        Date = date;
        Change(hours, reason, setBy);
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Hours { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string SetBy { get; private set; } = string.Empty;
    public DateTime SetAtUtc { get; private set; }

    public void Change(decimal hours, string reason, string setBy)
    {
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
}
