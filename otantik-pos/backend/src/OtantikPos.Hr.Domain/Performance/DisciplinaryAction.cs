using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Attendance;

namespace OtantikPos.Hr.Domain.Performance;

// How far a disciplinary action goes (درجة الجزاء), in the order they escalate.
public enum SeverityLevel
{
    // لفت نظر شفهي
    VerbalWarning = 1,

    // إنذار كتابي أول
    FirstWritten = 2,

    // إنذار كتابي ثان
    SecondWritten = 3,

    // خصم من الراتب: an amount, deducted from the month's pay through Corrections.
    PenaltyDeduction = 4,

    // فصل: recorded here; ending their employment is the Employees tab's leaving, as any other.
    Termination = 5,
}

// A manager's disciplinary action against an employee (إجراء تأديبي) on a day: the violation
// (negligence, absence, behaviour: as written), how far it goes, and their notes. A penalty
// deduction has its amount, which it deducts from its day's month's pay as an administrative
// penalty in money of its own (Penalty), listed on Corrections; no other level has an amount. A
// record of the employee's: kept as issued. One issued by mistake is withdrawn by a manager while
// its month's pay is open: kept, marked withdrawn with why, counted for nothing, and its penalty
// taken off Corrections and the pay.
public sealed class DisciplinaryAction : AggregateRoot
{
    public const int MaxViolationLength = 100;
    public const int MaxNotesLength = 1000;

    private DisciplinaryAction() { }

    public DisciplinaryAction(
        Guid id, Guid employeeId, DateOnly date, string violationType, SeverityLevel severityLevel, string? notes, decimal? penaltyAmount,
        string issuerId, string issuerName)
    {
        var violation = violationType?.Trim() ?? string.Empty;
        if (violation.Length == 0)
            throw new DomainException("Say what the violation was.");
        if (violation.Length > MaxViolationLength)
            throw new DomainException($"Keep the violation under {MaxViolationLength} characters.");
        if (!Enum.IsDefined(severityLevel))
            throw new DomainException("Choose how far it goes: a warning, a penalty deduction or termination.");
        if (severityLevel == SeverityLevel.PenaltyDeduction)
        {
            if (penaltyAmount is not > 0)
                throw new DomainException("A penalty deduction has its amount.");
            if (penaltyAmount > AdministrativePenalty.MaxAmount)
                throw new DomainException($"A penalty deduction is at most L.E {AdministrativePenalty.MaxAmount:N0}.");
            if (decimal.Round(penaltyAmount.Value, 2) != penaltyAmount)
                throw new DomainException("Give the penalty's amount to the piastre at most: 150.50.");
        }
        else if (penaltyAmount is not null)
        {
            throw new DomainException("Only a penalty deduction has an amount.");
        }

        if (string.IsNullOrWhiteSpace(issuerId))
            throw new DomainException("Say who issued it.");
        var trimmed = notes?.Trim();
        if (trimmed is { Length: > MaxNotesLength })
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");

        Id = id;
        EmployeeId = employeeId;
        Date = date;
        ViolationType = violation;
        SeverityLevel = severityLevel;
        Notes = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        PenaltyAmount = penaltyAmount;
        IssuerId = issuerId;
        IssuerName = issuerName;
        IssuedAtUtc = DateTime.UtcNow;
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public string ViolationType { get; private set; } = string.Empty;
    public SeverityLevel SeverityLevel { get; private set; }
    public string? Notes { get; private set; }
    public decimal? PenaltyAmount { get; private set; }
    // The manager's till account, and their name.
    public string IssuerId { get; private set; } = string.Empty;
    public string IssuerName { get; private set; } = string.Empty;
    public DateTime IssuedAtUtc { get; private set; }
    public bool IsWithdrawn { get; private set; }
    public string? WithdrawnById { get; private set; }
    public string? WithdrawnBy { get; private set; }
    public DateTime? WithdrawnAtUtc { get; private set; }
    public string? WithdrawalReason { get; private set; }

    public void Withdraw(string withdrawnById, string withdrawnBy, string reason)
    {
        if (IsWithdrawn)
            throw new DomainException("It was withdrawn already.");
        if (string.IsNullOrWhiteSpace(withdrawnById))
            throw new DomainException("Say who withdrew it.");
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Say why it is withdrawn.");
        if (trimmed.Length > MaxNotesLength)
            throw new DomainException($"Keep the reason under {MaxNotesLength} characters.");
        IsWithdrawn = true;
        WithdrawnById = withdrawnById;
        WithdrawnBy = withdrawnBy;
        WithdrawnAtUtc = DateTime.UtcNow;
        WithdrawalReason = trimmed;
    }

    // A warning, verbal or written: what the next one escalates from.
    public bool IsWarning => SeverityLevel is SeverityLevel.VerbalWarning or SeverityLevel.FirstWritten or SeverityLevel.SecondWritten;

    // The penalty it deducts, on its day, linked to it: none but for a penalty deduction.
    public AdministrativePenalty? Penalty() =>
        SeverityLevel == SeverityLevel.PenaltyDeduction
            ? AdministrativePenalty.ForAmount(EmployeeId, Date, PenaltyAmount!.Value, $"Penalty deduction: {ViolationType}", IssuerName, Id)
            : null;
}

// What an employee has had (سجل الجزاءات), for a manager to escalate rightly: each level's count,
// and the level that comes next. After a second written warning, a penalty deduction. Withdrawn
// ones count for nothing.
public sealed record DisciplinaryRecord(int VerbalWarnings, int FirstWritten, int SecondWritten, int PenaltyDeductions, int Terminations)
{
    public static DisciplinaryRecord Of(IEnumerable<DisciplinaryAction> actions)
    {
        var list = actions.Where(a => !a.IsWithdrawn).ToList();
        int Count(SeverityLevel level) => list.Count(a => a.SeverityLevel == level);
        return new DisciplinaryRecord(
            Count(SeverityLevel.VerbalWarning), Count(SeverityLevel.FirstWritten), Count(SeverityLevel.SecondWritten), Count(SeverityLevel.PenaltyDeduction),
            Count(SeverityLevel.Termination));
    }

    public int Warnings => VerbalWarnings + FirstWritten + SecondWritten;

    public SeverityLevel Next =>
        SecondWritten > 0 || PenaltyDeductions > 0 ? SeverityLevel.PenaltyDeduction
        : FirstWritten > 0 ? SeverityLevel.SecondWritten
        : VerbalWarnings > 0 ? SeverityLevel.FirstWritten
        : SeverityLevel.VerbalWarning;
}
