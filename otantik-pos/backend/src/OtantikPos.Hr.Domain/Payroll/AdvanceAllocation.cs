using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Payroll;

// A share of an advance handed out with no one named (السلفيات المعلقة): a lump sum taken from a
// day's takings or the safe as "advances" (SourceTransactionId, the cash that moved), or paid
// outside the till and recorded in HR only (SourceAdvanceId), given out to employees later by a
// manager, a share each. A share is deducted from its employee's pay of PayrollMonth, as an
// advance paid to them is: the month the money was handed out, or the first after it whose pay is
// not finalized for them. A share moves no money: it only says whose it is.
public sealed class AdvanceAllocation : AggregateRoot
{
    public const int MaxNotesLength = 300;

    private AdvanceAllocation() { }

    // `outsideTill`: the source is an HR advance paid outside the till, not a safe entry.
    public AdvanceAllocation(
        Guid id, Guid sourceId, bool outsideTill, Guid employeeId, decimal amount, DateOnly date, DateOnly payrollMonth, string? notes, string allocatedBy)
    {
        if (amount <= 0)
            throw new DomainException("Give each share an amount more than nothing.");
        if (notes?.Trim().Length > MaxNotesLength)
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");
        if (Payroll.PayrollMonth.Required(payrollMonth) < Payroll.PayrollMonth.Of(date))
            throw new DomainException("A share is deducted from the pay of the month the advance was handed out in, or a later one.");
        Id = id;
        SourceTransactionId = outsideTill ? null : sourceId;
        SourceAdvanceId = outsideTill ? sourceId : null;
        EmployeeId = employeeId;
        Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        Date = date;
        PayrollMonth = payrollMonth;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        AllocatedBy = allocatedBy;
        AllocatedAtUtc = DateTime.UtcNow;
    }

    // The advance handed out with no one named: the safe entry the cash moved with, or the HR
    // advance paid outside the till. One or the other.
    public Guid? SourceTransactionId { get; private set; }
    public Guid? SourceAdvanceId { get; private set; }
    public Guid SourceId => SourceTransactionId ?? SourceAdvanceId!.Value;
    public bool OutsideTill => SourceAdvanceId is not null;
    public Guid EmployeeId { get; private set; }
    public decimal Amount { get; private set; }

    // The day the advance was handed out.
    public DateOnly Date { get; private set; }
    public DateOnly PayrollMonth { get; private set; }
    public string? Notes { get; private set; }
    public string AllocatedBy { get; private set; } = string.Empty;
    public DateTime AllocatedAtUtc { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }
    public string? VoidedBy { get; private set; }
    public bool IsVoided => VoidedAtUtc is not null;

    // Taken back: what it was goes back to be given out again. Only while the pay it is deducted
    // from is not finalized (the caller checks that).
    public void Void(string voidedBy)
    {
        if (IsVoided)
            throw new DomainException("This share has already been taken back.");
        VoidedAtUtc = DateTime.UtcNow;
        VoidedBy = voidedBy;
    }
}
