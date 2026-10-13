using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Treasury;

public enum SupplierLedgerKind
{
    // What was owed to the supplier when the treasury started: invoices from before it.
    OpeningBalance = 1,

    // An invoice bought on credit (آجل): owed until paid.
    CreditInvoice = 2,

    // A payment voucher: paid from the safe.
    Payment = 3,
}

// One line of a supplier's account (كشف حساب): what the restaurant came to owe them and what it
// paid. Amount is signed: a credit invoice adds to the debt, a payment takes from it. Kept as
// entered; a voided invoice or payment is undone by a reversing line, dated the day it is voided.
//
// Each line is about one record (SourceId): the invoice, the payment voucher, or the opening
// balance's own id; so a retry writes it once.
public sealed class SupplierLedgerEntry : AggregateRoot
{
    private SupplierLedgerEntry() { }

    private SupplierLedgerEntry(Guid supplierId, SupplierLedgerKind kind, decimal amount, DateOnly businessDate, Guid sourceId, string recordedBy)
    {
        SupplierId = supplierId;
        Kind = kind;
        Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        BusinessDate = businessDate;
        SourceId = sourceId;
        RecordedBy = recordedBy;
        RecordedAtUtc = DateTime.UtcNow;
    }

    public Guid SupplierId { get; private set; }
    public SupplierLedgerKind Kind { get; private set; }

    // Positive: owed more; negative: owed less.
    public decimal Amount { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    // The invoice, the payment voucher (its safe transaction), or the opening balance.
    public Guid SourceId { get; private set; }

    public string RecordedBy { get; private set; } = string.Empty;
    public DateTime RecordedAtUtc { get; private set; }
    public Guid? ReversalOf { get; private set; }
    public bool IsReversal => ReversalOf is not null;

    public static SupplierLedgerEntry Opening(Guid id, Guid supplierId, decimal owed, DateOnly date, string recordedBy) =>
        owed > 0
            ? new SupplierLedgerEntry(supplierId, SupplierLedgerKind.OpeningBalance, owed, date, id, recordedBy) { Id = id }
            : throw new DomainException("Enter what is owed to the supplier, more than nothing.");

    public static SupplierLedgerEntry CreditInvoice(Guid supplierId, Guid invoiceId, decimal total, DateOnly date, string recordedBy) =>
        total > 0
            ? new SupplierLedgerEntry(supplierId, SupplierLedgerKind.CreditInvoice, total, date, invoiceId, recordedBy)
            : throw new DomainException("An invoice on credit needs a total more than nothing.");

    public static SupplierLedgerEntry Payment(SafeTransaction payment) =>
        payment is { Kind: SafeTransactionKind.SupplierPayment, SupplierId: { } supplierId, IsReversal: false }
            ? new SupplierLedgerEntry(supplierId, SupplierLedgerKind.Payment, payment.Amount, payment.BusinessDate, payment.Id, payment.RecordedBy)
            : throw new DomainException("Only a supplier payment is paid against a supplier's account.");

    // The opposite line, dated `today`, naming this one.
    public SupplierLedgerEntry Reverse(DateOnly today, string by) =>
        IsReversal
            ? throw new DomainException("A reversal is not reversed again.")
            : new SupplierLedgerEntry(SupplierId, Kind, -Amount, today, SourceId, by) { ReversalOf = Id };
}

public interface ISupplierLedgerRepository : IRepository<SupplierLedgerEntry>
{
    // What is owed to the supplier now: every line added up.
    Task<decimal> BalanceAsync(Guid supplierId, CancellationToken cancellationToken = default);

    // The lines about one record, reversals too.
    Task<IReadOnlyList<SupplierLedgerEntry>> GetBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
}
