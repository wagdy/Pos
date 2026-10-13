using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Treasury;

namespace Otantik.Domain.Tests;

// The treasury's rules: how an invoice may be paid, and safe and supplier entries that are kept
// as entered and undone by reversing ones.
public class TreasuryTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    [Fact]
    public void An_invoice_is_posted_only_once_it_says_how_it_is_paid()
    {
        var tomatoes = new RawMaterial("Tomatoes", UnitOfMeasure.Gram);
        var stock = new Dictionary<Guid, RawMaterial> { [tomatoes.Id] = tomatoes };
        PurchaseInvoice Invoice(Guid? supplier, PurchasePaymentMethod? method, DateOnly? revenueDate = null)
        {
            var invoice = new PurchaseInvoice(Guid.NewGuid(), "Mona");
            invoice.Record(supplier, null, null, [new PurchaseInvoiceLineEntry(tomatoes.Id, 5, "kg", 1000, 100)], method, revenueDate);
            return invoice;
        }

        Assert.Throws<DomainException>(() => Invoice(null, null).Post(stock, "Mona"));
        // On credit it is owed to someone: a supplier is required.
        Assert.Throws<DomainException>(() => Invoice(null, PurchasePaymentMethod.Credit).Post(stock, "Mona"));
        // From a day's takings: which day's.
        Assert.Throws<DomainException>(() => Invoice(null, PurchasePaymentMethod.DayRevenue).Post(stock, "Mona"));
        // Before the treasury is for the invoices from before it, not for a new one.
        Assert.Throws<DomainException>(() => Invoice(null, PurchasePaymentMethod.BeforeTreasury));

        var credit = Invoice(Guid.NewGuid(), PurchasePaymentMethod.Credit);
        Assert.Single(credit.Post(stock, "Mona"));
        var fromTakings = Invoice(null, PurchasePaymentMethod.DayRevenue, Today);
        fromTakings.Post(stock, "Mona");
        Assert.Equal((PurchasePaymentMethod.DayRevenue, (DateOnly?)Today), (fromTakings.PaymentMethod, fromTakings.RevenueDate));

        // A cash invoice whose payment was rejected is paid again, another way; one on credit is not.
        fromTakings.PayBy(PurchasePaymentMethod.Safe, null);
        Assert.Equal((PurchasePaymentMethod.Safe, (DateOnly?)null), (fromTakings.PaymentMethod, fromTakings.RevenueDate));
        Assert.Throws<DomainException>(() => credit.PayBy(PurchasePaymentMethod.Safe, null));
    }

    [Fact]
    public void Cash_in_is_positive_cash_out_negative_and_a_void_undoes_it_on_its_own_day()
    {
        var funding = SafeTransaction.Funding(Guid.NewGuid(), 5_000, Today.AddDays(-10), FundingSource.OwnerFinancing, " From Karim ", "Omar");
        var expense = SafeTransaction.ExpenseVoucher(Guid.NewGuid(), 1_250.555m, Today.AddDays(-3), Guid.NewGuid(), null, "Mona", "Omar");
        Assert.Equal((5_000m, "From Karim"), (funding.Amount, funding.Notes));
        Assert.Equal((-1_250.56m, CashFund.Safe, "Omar"), (expense.Amount, expense.Fund, expense.ApprovedBy));

        var reversal = expense.Void(Guid.NewGuid(), Today, "Omar");
        Assert.Equal((1_250.56m, Today, (Guid?)expense.Id, SafeTransactionKind.ExpenseVoucher), (reversal.Amount, reversal.BusinessDate, reversal.ReversalOf, reversal.Kind));
        Assert.True(expense.IsVoided);
        Assert.Throws<DomainException>(() => expense.Void(Guid.NewGuid(), Today, "Omar"));
        Assert.Throws<DomainException>(() => reversal.Void(Guid.NewGuid(), Today, "Omar"));

        Assert.Throws<DomainException>(() => SafeTransaction.Funding(Guid.NewGuid(), 0, Today, FundingSource.Other, null, "Omar"));
        Assert.Throws<DomainException>(() => SafeTransaction.OpeningBalance(Guid.NewGuid(), 100, Today, FundingSource.Other, null, "Omar"));
        // Takings are moved in on their day or after, never before.
        Assert.Throws<DomainException>(() => SafeTransaction.RevenueTransfer(Guid.NewGuid(), 100, Today, Today.AddDays(1), null, "Omar"));

        // Paid from a day's takings: out of that day's cash, not the safe.
        var fromTakings = SafeTransaction.InvoicePayment(Guid.NewGuid(), 300, Today, Guid.NewGuid(), null, Today.AddDays(-1), "Omar");
        Assert.Equal((CashFund.DayRevenue, -300m), (fromTakings.Fund, fromTakings.Amount));
    }

    [Fact]
    public void Takings_with_the_tax_in_them_go_into_the_safe_whole_with_the_tax_kept_apart()
    {
        // 1,140 at 14%: 1,000 before tax and 140 of tax, and all 1,140 in the safe.
        var takings = SafeTransaction.RevenueTransfer(Guid.NewGuid(), 1_140, Today, Today, null, "Omar", taxPercent: 14);
        Assert.Equal(
            (1_140m, true, (decimal?)14m, 140m, 1_000m),
            (takings.Amount, takings.IncludesTax, takings.TaxPercent, takings.TaxAmount, takings.NetAmount));

        // To the piastre, and the two always add back up: 100 is 87.72 before tax and 12.28 of tax.
        var odd = SafeTransaction.RevenueTransfer(Guid.NewGuid(), 100, Today, Today, null, "Omar", taxPercent: 14);
        Assert.Equal((12.28m, 87.72m), (odd.TaxAmount, odd.NetAmount));

        // Without the tax in them, all of it is takings.
        var plain = SafeTransaction.RevenueTransfer(Guid.NewGuid(), 500, Today, Today, null, "Omar");
        Assert.Equal((false, (decimal?)null, 0m, 500m), (plain.IncludesTax, plain.TaxPercent, plain.TaxAmount, plain.NetAmount));

        // A void takes the tax back with the cash.
        var reversal = takings.Void(Guid.NewGuid(), Today, "Omar");
        Assert.Equal((-1_140m, -140m, (decimal?)14m), (reversal.Amount, reversal.TaxAmount, reversal.TaxPercent));

        Assert.Throws<DomainException>(() => SafeTransaction.RevenueTransfer(Guid.NewGuid(), 100, Today, Today, null, "Omar", taxPercent: 100));
        Assert.Throws<DomainException>(() => SafeTransaction.RevenueTransfer(Guid.NewGuid(), 100, Today, Today, null, "Omar", taxPercent: -1));
    }

    [Fact]
    public void A_supplier_is_owed_what_was_bought_on_credit_less_what_was_paid()
    {
        var supplier = Guid.NewGuid();
        var opening = SupplierLedgerEntry.Opening(Guid.NewGuid(), supplier, 1_000, Today.AddDays(-30), "Omar");
        var invoice = SupplierLedgerEntry.CreditInvoice(supplier, Guid.NewGuid(), 2_500, Today.AddDays(-5), "Mona");
        var payment = SafeTransaction.SupplierPayment(Guid.NewGuid(), 1_500, Today, supplier, null, "Mona", "Omar");
        var paid = SupplierLedgerEntry.Payment(payment);

        Assert.Equal(2_000m, opening.Amount + invoice.Amount + paid.Amount);
        Assert.Equal((supplier, payment.Id, SupplierLedgerKind.Payment), (paid.SupplierId, paid.SourceId, paid.Kind));

        // Voided, the payment's line is undone: owed as before it.
        var undone = paid.Reverse(Today, "Omar");
        Assert.Equal((1_500m, (Guid?)paid.Id), (undone.Amount, undone.ReversalOf));
        Assert.Throws<DomainException>(() => undone.Reverse(Today, "Omar"));
        Assert.Throws<DomainException>(() => SupplierLedgerEntry.Payment(SafeTransaction.Funding(Guid.NewGuid(), 10, Today, FundingSource.Other, null, "Omar")));
    }
}
