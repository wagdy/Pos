using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Treasury;

// Where cash is kept. The safe (الخزنة) holds the restaurant's cash; each business day's cash
// takings are a fund of their own, the drawer, until they are entered, and a purchase can be paid
// straight out of them. Once a day's takings are in the safe, what is paid out of them, or put
// back by a void, moves the safe.
public enum CashFund
{
    Safe = 1,

    // One business day's cash sales (RevenueDate), before they go into the safe.
    DayRevenue = 2,
}

public enum SafeTransactionKind
{
    // The cash in the safe when the treasury starts.
    OpeningBalance = 1,

    // Money put into the safe from outside the sales: the owners', a loan.
    Funding = 2,

    // A day's cash takings entered (توريد إيراد): the POS's gross for the day, into the safe less
    // what was paid straight out of them.
    RevenueTransfer = 3,

    // A cash expense: a bill, wages, petty cash. On the profit and loss under its expense item.
    ExpenseVoucher = 4,

    // Paid to a supplier against what the restaurant owes them (سداد مورد), from the safe or
    // straight from a day's takings.
    SupplierPayment = 5,

    // A purchase invoice paid in cash when it was posted, from the safe or a day's takings.
    InvoicePayment = 6,

    // A cash advance to an employee (سلفة), from the safe or a day's takings. Not an expense: it
    // is owed by the employee until the payslip of its month (PayrollMonth) deducts it, and the
    // wages are counted there.
    EmployeeAdvance = 7,

    // An employee's salary paid (صرف راتب): their payslip's net for a month, or part of it.
    SalaryPayment = 8,
}

// Where money put into the safe came from (مصدر التمويل).
public enum FundingSource
{
    // Cash already there, carried over from before (رصيد مرحل).
    CarriedOver = 1,

    // The owners' money (تمويل جاري / جاري الشركاء).
    OwnerFinancing = 2,

    Other = 3,
}

// One movement of cash into or out of the safe, or out of a day's takings: the treasury's ledger.
// Amount is signed (in is positive, out negative). Kept as entered: a wrong one is voided by a
// reversing entry dated the day it is voided, so a closed month's balance never moves.
//
// Its id is made by the till, so a retried voucher is recorded once.
public sealed class SafeTransaction : AggregateRoot
{
    private SafeTransaction() { }

    private SafeTransaction(
        Guid id,
        SafeTransactionKind kind,
        CashFund fund,
        decimal amount,
        DateOnly businessDate,
        string? notes,
        string recordedBy,
        string? approvedBy)
    {
        if (notes?.Trim().Length > 300)
            throw new DomainException("Keep the notes under 300 characters.");
        Id = id;
        Kind = kind;
        Fund = fund;
        Amount = amount;
        BusinessDate = businessDate;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        RecordedBy = recordedBy;
        ApprovedBy = approvedBy;
        RecordedAtUtc = DateTime.UtcNow;
    }

    public SafeTransactionKind Kind { get; private set; }
    public CashFund Fund { get; private set; }

    // In (positive) or out (negative) of the fund.
    public decimal Amount { get; private set; }

    // The business day it belongs to: the voucher's date.
    public DateOnly BusinessDate { get; private set; }

    // The day's takings it comes from: a transfer's, or a payment out of a day's takings.
    public DateOnly? RevenueDate { get; private set; }

    public FundingSource? Source { get; private set; }
    public Guid? SupplierId { get; private set; }
    public Guid? PurchaseInvoiceId { get; private set; }
    public Guid? ExpenseItemId { get; private set; }

    // An advance's or a salary's: whose, and the payroll month it counts in (its first day).
    public Guid? EmployeeId { get; private set; }
    public DateOnly? PayrollMonth { get; private set; }

    public string? Notes { get; private set; }

    // A day's takings: the POS's gross for the day, as entered (إجمالي الإيراد). Amount is what
    // reached the safe: the gross less what had been paid straight out of the day's takings by
    // then (PaidOut). Signed as Amount is; null for every other entry.
    public decimal? GrossAmount { get; private set; }
    public decimal PaidOut => GrossAmount is { } gross ? gross - Amount : 0;

    // A day's takings entered as including tax (هل إجمالي الإيراد يشمل الضريبة؟): the rate taken off
    // and the tax in the gross, signed as it is. NetAmount is the takings before tax, as the
    // menu's prices are.
    public bool IncludesTax { get; private set; }
    public decimal? TaxPercent { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal NetAmount => (GrossAmount ?? Amount) - TaxAmount;

    public string RecordedBy { get; private set; } = string.Empty;
    public DateTime RecordedAtUtc { get; private set; }

    // The manager who approved it, when it was asked of one.
    public string? ApprovedBy { get; private set; }

    public Guid? ReversalOf { get; private set; }
    public bool IsReversal => ReversalOf is not null;
    public DateTime? VoidedAtUtc { get; private set; }
    public string? VoidedBy { get; private set; }
    public bool IsVoided => VoidedAtUtc is not null;

    public static SafeTransaction OpeningBalance(
        Guid id, decimal amount, DateOnly date, FundingSource source, string? notes, string recordedBy, string? approvedBy = null)
    {
        if (amount < 0)
            throw new DomainException("The opening balance is what is in the safe: zero or more.");
        if (source == FundingSource.Other)
            throw new DomainException("Say where the opening balance came from: carried over, or the owners' financing.");
        return new SafeTransaction(
            id, SafeTransactionKind.OpeningBalance, CashFund.Safe, Math.Round(amount, 2, MidpointRounding.AwayFromZero), date, notes, recordedBy, approvedBy)
        {
            Source = Defined(source),
        };
    }

    public static SafeTransaction Funding(
        Guid id, decimal amount, DateOnly date, FundingSource source, string? notes, string recordedBy, string? approvedBy = null) =>
        new(id, SafeTransactionKind.Funding, CashFund.Safe, Positive(amount), date, notes, recordedBy, approvedBy) { Source = Defined(source) };

    // A day's cash takings entered: gross, the POS's total for the day, which may differ from what
    // the till says, so a shortfall shows on that day. Into the safe goes the gross less paidOut,
    // what was paid straight out of the day's takings before they were entered; when that is more
    // than the gross, the rest comes out of the safe. With taxPercent, the gross includes tax at
    // that rate, and the tax in it is kept apart.
    public static SafeTransaction RevenueTransfer(
        Guid id, decimal gross, DateOnly date, DateOnly revenueDate, string? notes, string recordedBy, string? approvedBy = null,
        decimal? taxPercent = null, decimal paidOut = 0)
    {
        if (revenueDate > date)
            throw new DomainException("A day's takings are moved into the safe on that day or after it.");
        if (paidOut < 0)
            throw new DomainException("What was paid out of a day's takings is zero or more.");
        var entered = Positive(gross);
        var entry = new SafeTransaction(
            id, SafeTransactionKind.RevenueTransfer, CashFund.Safe, entered - Math.Round(paidOut, 2, MidpointRounding.AwayFromZero), date, notes,
            recordedBy, approvedBy)
        {
            RevenueDate = revenueDate,
            GrossAmount = entered,
        };
        if (taxPercent is { } rate)
        {
            entry.IncludesTax = true;
            entry.TaxPercent = rate;
            entry.TaxAmount = TaxIn(entered, rate);
        }

        return entry;
    }

    // The tax in a sum that includes it: the sum less the sum before tax, which is the sum over
    // (1 + rate), to the piastre. So the two always add back up to the sum.
    public static decimal TaxIn(decimal gross, decimal percent)
    {
        if (percent is < 0 or >= 100)
            throw new DomainException("A tax rate is a percentage from 0 up to 100.");
        return gross - Math.Round(gross / (1 + percent / 100m), 2, MidpointRounding.AwayFromZero);
    }

    public static SafeTransaction ExpenseVoucher(
        Guid id, decimal amount, DateOnly date, Guid expenseItemId, string? notes, string recordedBy, string? approvedBy = null) =>
        new(id, SafeTransactionKind.ExpenseVoucher, CashFund.Safe, -Positive(amount), date, notes, recordedBy, approvedBy)
        {
            ExpenseItemId = expenseItemId,
        };

    // From the safe, or from a day's takings (revenueDate), as an invoice is: out of the drawer
    // until they are entered, out of the safe once they are in it (takingsInSafe).
    public static SafeTransaction SupplierPayment(
        Guid id, decimal amount, DateOnly date, Guid supplierId, string? notes, string recordedBy, string? approvedBy = null,
        DateOnly? revenueDate = null, bool takingsInSafe = false)
    {
        if (revenueDate > date)
            throw new DomainException("A supplier is paid from the takings of the payment's day or a day before it.");
        return new SafeTransaction(
            id, SafeTransactionKind.SupplierPayment, revenueDate is null || takingsInSafe ? CashFund.Safe : CashFund.DayRevenue, -Positive(amount),
            date, notes, recordedBy, approvedBy)
        {
            SupplierId = supplierId,
            RevenueDate = revenueDate,
        };
    }

    // From the safe, or from a day's takings (revenueDate): out of the drawer until they are
    // entered, out of the safe once they are in it (takingsInSafe).
    public static SafeTransaction InvoicePayment(
        Guid id, decimal amount, DateOnly date, Guid invoiceId, Guid? supplierId, DateOnly? revenueDate, string recordedBy, string? approvedBy = null,
        bool takingsInSafe = false)
    {
        if (revenueDate > date)
            throw new DomainException("An invoice is paid from the takings of its day or a day before it.");
        return new SafeTransaction(
            id, SafeTransactionKind.InvoicePayment, revenueDate is null || takingsInSafe ? CashFund.Safe : CashFund.DayRevenue, -Positive(amount),
            date, null, recordedBy, approvedBy)
        {
            PurchaseInvoiceId = invoiceId,
            SupplierId = supplierId,
            RevenueDate = revenueDate,
        };
    }

    // Out of the safe, or of a day's takings, as a supplier is paid. Deducted from the pay of
    // payrollMonth: the month it is paid in, or a later one when that month's payroll is closed.
    public static SafeTransaction EmployeeAdvance(
        Guid id, decimal amount, DateOnly date, Guid employeeId, DateOnly payrollMonth, string? notes, string recordedBy,
        string? approvedBy = null, DateOnly? revenueDate = null, bool takingsInSafe = false)
    {
        if (revenueDate > date)
            throw new DomainException("An advance is paid from the takings of its day or a day before it.");
        if (Month(payrollMonth) < new DateOnly(date.Year, date.Month, 1))
            throw new DomainException("An advance is deducted from the pay of the month it is paid in, or a later one.");
        return new SafeTransaction(
            id, SafeTransactionKind.EmployeeAdvance, revenueDate is null || takingsInSafe ? CashFund.Safe : CashFund.DayRevenue, -Positive(amount),
            date, notes, recordedBy, approvedBy)
        {
            EmployeeId = employeeId,
            PayrollMonth = payrollMonth,
            RevenueDate = revenueDate,
        };
    }

    // A month's pay, on or after the month starts.
    public static SafeTransaction SalaryPayment(
        Guid id, decimal amount, DateOnly date, Guid employeeId, DateOnly payrollMonth, string? notes, string recordedBy,
        string? approvedBy = null, DateOnly? revenueDate = null, bool takingsInSafe = false)
    {
        if (revenueDate > date)
            throw new DomainException("A salary is paid from the takings of its day or a day before it.");
        if (Month(payrollMonth) > date)
            throw new DomainException("A month's salary is paid once the month has started.");
        return new SafeTransaction(
            id, SafeTransactionKind.SalaryPayment, revenueDate is null || takingsInSafe ? CashFund.Safe : CashFund.DayRevenue, -Positive(amount),
            date, notes, recordedBy, approvedBy)
        {
            EmployeeId = employeeId,
            PayrollMonth = payrollMonth,
            RevenueDate = revenueDate,
        };
    }

    // Voids it: the opposite amount, entered on `today`, naming it. Voiding again is refused. A
    // payment out of a day's takings made before they were entered goes back into the safe when
    // they are in it now (takingsInSafe): it was taken off what went in.
    public SafeTransaction Void(Guid reversalId, DateOnly today, string voidedBy, bool takingsInSafe = false)
    {
        if (IsReversal)
            throw new DomainException("A reversal is not voided again.");
        if (IsVoided)
            throw new DomainException("This has already been voided.");
        VoidedAtUtc = DateTime.UtcNow;
        VoidedBy = voidedBy;
        var fund = Fund == CashFund.DayRevenue && takingsInSafe ? CashFund.Safe : Fund;
        return new SafeTransaction(reversalId, Kind, fund, -Amount, today, "Voided", voidedBy, null)
        {
            GrossAmount = -GrossAmount,
            IncludesTax = IncludesTax,
            TaxPercent = TaxPercent,
            TaxAmount = -TaxAmount,
            RevenueDate = RevenueDate,
            Source = Source,
            SupplierId = SupplierId,
            PurchaseInvoiceId = PurchaseInvoiceId,
            ExpenseItemId = ExpenseItemId,
            EmployeeId = EmployeeId,
            PayrollMonth = PayrollMonth,
            ReversalOf = Id,
        };
    }

    private static decimal Positive(decimal amount) =>
        amount > 0 ? Math.Round(amount, 2, MidpointRounding.AwayFromZero) : throw new DomainException("Enter an amount more than nothing.");

    private static DateOnly Month(DateOnly month) =>
        month.Day == 1 ? month : throw new DomainException("A payroll month is named by its first day.");

    private static FundingSource Defined(FundingSource source) =>
        Enum.IsDefined(source) ? source : throw new DomainException("Say where the money came from.");
}

public interface ISafeTransactionRepository : IRepository<SafeTransaction>
{
    // The safe's balance: everything in and out of it, up to and including `through` when given.
    Task<decimal> SafeBalanceAsync(DateOnly? through = null, CancellationToken cancellationToken = default);

    // A day's takings as entered, if they are and are not voided since.
    Task<SafeTransaction?> GetTakingsAsync(DateOnly revenueDate, CancellationToken cancellationToken = default);

    // What was paid straight out of a day's takings while they were not in the safe: what is taken
    // off them when they are entered.
    Task<decimal> PaidOutOfTakingsAsync(DateOnly revenueDate, CancellationToken cancellationToken = default);

    // What an invoice's cash payment is, if it was paid in cash and not voided since.
    Task<SafeTransaction?> GetPaymentOfInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    // Of these invoices, the ones paid in cash and not voided since.
    Task<IReadOnlySet<Guid>> PaidInvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken = default);

    Task<bool> AnyOpeningBalanceAsync(CancellationToken cancellationToken = default);
}
