using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.Purchases;

// Returns invoices with their Lines loaded.
public interface IPurchaseInvoiceRepository : IRepository<PurchaseInvoice>
{
    Task<IReadOnlyList<PurchaseInvoice>> GetByStatusAsync(PurchaseInvoiceStatus status, CancellationToken cancellationToken = default);

    // For each supplier on an invoice: how many invoices (drafts too), and what the posted ones came to.
    Task<IReadOnlyDictionary<Guid, SupplierUse>> GetSupplierUseAsync(CancellationToken cancellationToken = default);

    Task<bool> AnyForSupplierAsync(Guid supplierId, CancellationToken cancellationToken = default);

    // For each expense item on an invoice: how many invoices (drafts too), and what it cost on the posted ones.
    Task<IReadOnlyDictionary<Guid, ExpenseItemUse>> GetExpenseItemUseAsync(CancellationToken cancellationToken = default);

    Task<bool> AnyForExpenseItemAsync(Guid expenseItemId, CancellationToken cancellationToken = default);

    // Only a draft is ever removed. A posted invoice is the record of what came in.
    void Remove(PurchaseInvoice draft);
}

public sealed record SupplierUse(int Invoices, decimal PostedTotal, DateTime? LastPostedAtUtc);

public sealed record ExpenseItemUse(int Invoices, decimal PostedTotal, DateTime? LastPostedAtUtc);

// The photo or scan of an invoice, kept apart from the invoice so a list of invoices never loads
// a megabyte each. Written in the same unit of work as the invoice.
public interface IPurchaseAttachmentStore
{
    void Put(Guid invoiceId, byte[] content);

    Task<byte[]?> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}
