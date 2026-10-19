using Microsoft.EntityFrameworkCore;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Node.Infrastructure.Persistence;

// Raw materials made expense items, in the database: what would stop it, what it restates, and
// the restating itself. A material that was only ever bought, counted or thrown away can be; one
// that recipes, batches or sales have used is stock, and stays so.
internal sealed class ItemConversions(NodeDbContext db) : IItemConversions
{
    // What a material's ledger may hold to be made an expense: its purchases, counts and waste.
    private static readonly StockMovementReason[] Kept = [StockMovementReason.Purchase, StockMovementReason.CountAdjustment, StockMovementReason.Spoilage];

    public async Task<IReadOnlyList<string>> ExpenseBlockersAsync(
        IReadOnlyList<RawMaterial> materials, IReadOnlyDictionary<Guid, Guid> namesakes, Guid? exceptRequestId, CancellationToken cancellationToken)
    {
        var ids = materials.Select(m => m.Id).ToList();
        var names = materials.ToDictionary(m => m.Id, m => m.Name);
        var found = new List<(string Name, string Why)>();
        void Add(IEnumerable<Guid> which, string why) => found.AddRange(which.Distinct().Select(id => (names[id], why)));

        Add(await db.Set<RecipeIngredient>().Where(i => ids.Contains(i.RawMaterialId)).Select(i => i.RawMaterialId).ToListAsync(cancellationToken),
            "is in a recipe");
        Add(await db.Set<PrepIngredient>().Where(i => ids.Contains(i.RawMaterialId)).Select(i => i.RawMaterialId).ToListAsync(cancellationToken),
            "is in a prep recipe");
        Add(await db.Set<ProductionOrderLine>().Where(l => ids.Contains(l.RawMaterialId)).Select(l => l.RawMaterialId).ToListAsync(cancellationToken),
            "went into a batch");
        Add(await db.StockMovements.Where(m => ids.Contains(m.RawMaterialId) && !Kept.Contains(m.Reason)).Select(m => m.RawMaterialId).ToListAsync(cancellationToken),
            "has been sold or used through a recipe");
        Add(await db.StockMovements
                .Where(m => ids.Contains(m.RawMaterialId) && m.Reason == StockMovementReason.Purchase && !db.PurchaseInvoices.Any(i => i.Id == m.SourceId))
                .Select(m => m.RawMaterialId).ToListAsync(cancellationToken),
            "was received with no invoice");
        Add(await db.Set<PurchaseInvoiceLine>().Where(l => l.RawMaterialId != null && ids.Contains(l.RawMaterialId.Value) && l.Cost == null)
                .Select(l => l.RawMaterialId!.Value).ToListAsync(cancellationToken),
            "is on an invoice with no price: an expense line has its amount, so price it first");
        Add(await db.SharedCosts.Where(c => c.RawMaterialId != null && ids.Contains(c.RawMaterialId.Value)).Select(c => c.RawMaterialId!.Value)
                .ToListAsync(cancellationToken),
            "is a shared cost");

        // An invoice holding it beside the expense item of its name would hold that item twice.
        foreach (var (material, expense) in namesakes)
            if (await db.Set<PurchaseInvoiceLine>().AnyAsync(
                    l => l.RawMaterialId == material
                        && db.Set<PurchaseInvoiceLine>().Any(other => other.InvoiceId == l.InvoiceId && other.ExpenseItemId == expense),
                    cancellationToken))
                Add([material], "is on an invoice beside the expense item of its name");

        foreach (var id in ids)
            if (await PendingNamingAsync([id], exceptRequestId, cancellationToken) > 0)
                Add([id], "is named by a request waiting for a manager");

        return found.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Why).Select(f => $"{f.Name} {f.Why}.").ToList();
    }

    public async Task<ExpenseRestatement> RestatementAsync(IReadOnlyList<Guid> materialIds, CancellationToken cancellationToken)
    {
        var lines = db.Set<PurchaseInvoiceLine>().Where(l => l.RawMaterialId != null && materialIds.Contains(l.RawMaterialId.Value));
        return new ExpenseRestatement(
            await lines.CountAsync(cancellationToken),
            await lines.SumAsync(l => l.Cost ?? 0, cancellationToken),
            await db.StockMovements.CountAsync(m => materialIds.Contains(m.RawMaterialId), cancellationToken),
            await db.Set<StockCountLine>().CountAsync(l => materialIds.Contains(l.RawMaterialId), cancellationToken));
    }

    // Read, and looked through here: the payload is JSON, and they are few.
    public async Task<int> PendingNamingAsync(IReadOnlyCollection<Guid> ids, Guid? exceptRequestId, CancellationToken cancellationToken)
    {
        var pending = await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.Status == ApprovalStatus.Pending && r.Id != exceptRequestId)
            .Select(r => new { r.RecordId, r.Payload })
            .ToListAsync(cancellationToken);
        var texts = ids.Select(id => id.ToString()).ToList();
        return pending.Count(r => ids.Contains(r.RecordId) || texts.Any(t => r.Payload?.Contains(t, StringComparison.OrdinalIgnoreCase) == true));
    }

    // As if each had always been its expense item: its invoice lines that item's, each as entered
    // (5 kg, at what it cost), and nothing of it in the stock ledger or on the counts.
    public async Task RestateAndSaveAsync(IReadOnlyList<(Guid MaterialId, Guid ExpenseItemId)> pairs, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var (material, expense) in pairs)
        {
            await db.Set<PurchaseInvoiceLine>().Where(l => l.RawMaterialId == material).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ItemType, PurchaseItemType.Expense)
                .SetProperty(l => l.ExpenseItemId, (Guid?)expense)
                .SetProperty(l => l.RawMaterialId, (Guid?)null)
                .SetProperty(l => l.UnitSize, 1m)
                .SetProperty(l => l.Quantity, l => l.EnteredQuantity), cancellationToken);
            await db.StockMovements.Where(m => m.RawMaterialId == material).ExecuteDeleteAsync(cancellationToken);
            await db.Set<StockCountLine>().Where(l => l.RawMaterialId == material).ExecuteDeleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
