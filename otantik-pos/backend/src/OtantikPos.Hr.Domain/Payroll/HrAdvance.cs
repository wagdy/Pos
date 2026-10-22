using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Payroll;

// An advance paid outside the till (from the owner's own pocket, say), recorded in HR only: no
// cash leaves the safe or a day's takings, and Financials never sees it. Paid to an employee, it
// is deducted from their pay of PayrollMonth: the month it was paid in, or the first after it
// whose pay is not finalized for them. With no one named, it waits to be given out a share each
// (AdvanceAllocation), as a lump sum from the till does.
public sealed class HrAdvance : AggregateRoot
{
    public const int MaxNotesLength = 300;

    private HrAdvance() { }

    public HrAdvance(
        Guid id, decimal amount, DateOnly date, Guid? employeeId, DateOnly? payrollMonth, string? notes, string recordedBy, string? approvedBy = null)
    {
        if (amount <= 0)
            throw new DomainException("Enter an amount more than nothing.");
        if (notes?.Trim().Length > MaxNotesLength)
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");
        if (employeeId is null != payrollMonth is null)
            throw new DomainException("An advance paid to an employee is deducted from a month's pay; one paid to no one yet, from none.");
        if (payrollMonth is { } month && Payroll.PayrollMonth.Required(month) < Payroll.PayrollMonth.Of(date))
            throw new DomainException("An advance is deducted from the pay of the month it is paid in, or a later one.");
        Id = id;
        Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        Date = date;
        EmployeeId = employeeId;
        PayrollMonth = payrollMonth;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        RecordedBy = recordedBy;
        ApprovedBy = approvedBy;
        RecordedAtUtc = DateTime.UtcNow;
    }

    public decimal Amount { get; private set; }
    public DateOnly Date { get; private set; }
    public Guid? EmployeeId { get; private set; }
    public DateOnly? PayrollMonth { get; private set; }
    public string? Notes { get; private set; }
    public string RecordedBy { get; private set; } = string.Empty;
    public string? ApprovedBy { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }
    public string? VoidedBy { get; private set; }
    public bool IsVoided => VoidedAtUtc is not null;

    // Paid to no one yet: given out a share each.
    public bool IsPending => EmployeeId is null;

    // Voided while the pay it is deducted from is not finalized, and, paid to no one yet, while no
    // share of it stands (the caller checks both).
    public void Void(string voidedBy)
    {
        if (IsVoided)
            throw new DomainException("This advance has already been voided.");
        VoidedAtUtc = DateTime.UtcNow;
        VoidedBy = voidedBy;
    }
}
