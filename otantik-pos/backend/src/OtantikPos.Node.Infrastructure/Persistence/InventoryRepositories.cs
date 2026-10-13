using Microsoft.EntityFrameworkCore;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Node.Infrastructure.Common;

namespace OtantikPos.Node.Infrastructure.Persistence;

internal sealed class RawMaterialRepository(NodeDbContext db) : IRawMaterialRepository
{
    public Task<RawMaterial?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.RawMaterials.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(RawMaterial aggregate) => db.RawMaterials.Add(aggregate);

    public async Task<IReadOnlyList<RawMaterial>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.RawMaterials.OrderBy(m => m.Name).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RawMaterial>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        return await db.RawMaterials.Where(m => idList.Contains(m.Id)).ToListAsync(cancellationToken);
    }

    // Spelled out rather than RawMaterial.NeedsReorder, which is computed in C#.
    public async Task<IReadOnlyList<RawMaterial>> GetNeedingReorderAsync(CancellationToken cancellationToken = default) =>
        await db.RawMaterials.Where(m => m.QuantityOnHand <= m.ReorderLevel).OrderBy(m => m.Name).ToListAsync(cancellationToken);
}

internal sealed class RecipeRepository(NodeDbContext db) : IRecipeRepository
{
    private IQueryable<Recipe> WithIngredients => db.Recipes.Include(r => r.Ingredients);

    public Task<Recipe?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithIngredients.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(Recipe aggregate) => db.Recipes.Add(aggregate);

    // The ones sales go by: not deleted, and linked to the menu.
    private IQueryable<Recipe> Active => WithIngredients.Where(r => r.DeletedAtUtc == null && r.CatalogItemId != null);

    public Task<Recipe?> GetByTargetAsync(RecipeTarget target, CancellationToken cancellationToken = default) =>
        Active.SingleOrDefaultAsync(
            r => r.TargetKind == target.Kind && r.CatalogItemId == target.CatalogItemId && r.VariantId == target.VariantId,
            cancellationToken);

    // Narrowed in SQL by catalog id, then matched exactly in memory. A recipe target is three
    // columns, which an IN list cannot express, and the candidates are a handful.
    public async Task<IReadOnlyList<Recipe>> GetByTargetsAsync(IReadOnlyCollection<RecipeTarget> targets, CancellationToken cancellationToken = default)
    {
        var catalogIds = targets.Select(t => (int?)t.CatalogItemId).Distinct().ToList();
        var candidates = await Active.Where(r => catalogIds.Contains(r.CatalogItemId)).ToListAsync(cancellationToken);
        return candidates.Where(r => targets.Contains(r.Target!.Value)).ToList();
    }

    public async Task<IReadOnlyList<Recipe>> GetInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        await WithIngredients.Where(r => r.RecipeCategoryId == categoryId).ToListAsync(cancellationToken);

    public Task<int> CountUsingAsync(Guid rawMaterialId, CancellationToken cancellationToken = default) =>
        db.Recipes.AsNoTracking().CountAsync(r => r.DeletedAtUtc == null && r.Ingredients.Any(i => i.RawMaterialId == rawMaterialId), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountByCategoryAsync(CancellationToken cancellationToken = default) =>
        await db.Recipes.AsNoTracking()
            .Where(r => r.DeletedAtUtc == null && r.RecipeCategoryId != null)
            .GroupBy(r => r.RecipeCategoryId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
}

internal sealed class RecipeCategoryRepository(NodeDbContext db) : IRecipeCategoryRepository
{
    public Task<RecipeCategory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.RecipeCategories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(RecipeCategory aggregate) => db.RecipeCategories.Add(aggregate);

    public void Remove(RecipeCategory category) => db.RecipeCategories.Remove(category);

    public async Task<IReadOnlyList<RecipeCategory>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.RecipeCategories.AsNoTracking().ToListAsync(cancellationToken);

    public Task<RecipeCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        db.RecipeCategories.AsNoTracking().SingleOrDefaultAsync(c => c.NormalizedName == normalizedName, cancellationToken);
}

internal sealed class StockMovementRepository(NodeDbContext db) : IStockMovementRepository
{
    public Task<StockMovement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.StockMovements.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(StockMovement aggregate) => db.StockMovements.Add(aggregate);

    public Task<bool> ExistsAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default) =>
        db.StockMovements.AnyAsync(m => m.SourceId == sourceId && m.Reason == reason, cancellationToken);

    public async Task<IReadOnlyList<StockMovement>> GetBySourceAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default) =>
        await db.StockMovements.AsNoTracking().Where(m => m.SourceId == sourceId && m.Reason == reason).ToListAsync(cancellationToken);
}

internal sealed class StockCountRepository(NodeDbContext db) : IStockCountRepository
{
    private IQueryable<StockCount> WithLines => db.StockCounts.Include(c => c.Lines);

    public Task<StockCount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithLines.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(StockCount aggregate) => db.StockCounts.Add(aggregate);

    public void Remove(StockCount draft) => db.StockCounts.Remove(draft);

    public Task<StockCount?> GetDraftAsync(CancellationToken cancellationToken = default) =>
        WithLines.FirstOrDefaultAsync(c => c.Status == StockCountStatus.Draft, cancellationToken);

    public async Task<IReadOnlyList<StockCount>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await WithLines.AsNoTracking().ToListAsync(cancellationToken);
}

internal sealed class PurchaseInvoiceRepository(NodeDbContext db) : IPurchaseInvoiceRepository
{
    private IQueryable<PurchaseInvoice> WithLines => db.PurchaseInvoices.Include(i => i.Lines);

    public Task<PurchaseInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithLines.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    public void Add(PurchaseInvoice aggregate) => db.PurchaseInvoices.Add(aggregate);

    public void Remove(PurchaseInvoice draft) => db.PurchaseInvoices.Remove(draft);

    public async Task<IReadOnlyList<PurchaseInvoice>> GetByStatusAsync(PurchaseInvoiceStatus status, CancellationToken cancellationToken = default) =>
        await WithLines.AsNoTracking().Where(i => i.Status == status).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, SupplierUse>> GetSupplierUseAsync(CancellationToken cancellationToken = default)
    {
        var invoices = await db.PurchaseInvoices.AsNoTracking()
            .Where(i => i.SupplierId != null)
            .GroupBy(i => i.SupplierId!.Value)
            .Select(g => new { SupplierId = g.Key, Count = g.Count(), Last = g.Max(i => i.PostedAtUtc) })
            .ToListAsync(cancellationToken);
        var totals = await db.Set<PurchaseInvoiceLine>().AsNoTracking()
            .Join(db.PurchaseInvoices.Where(i => i.SupplierId != null && i.Status == PurchaseInvoiceStatus.Posted),
                l => l.InvoiceId, i => i.Id, (l, i) => new { SupplierId = i.SupplierId!.Value, l.Cost })
            .GroupBy(x => x.SupplierId)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.Cost ?? 0) })
            .ToDictionaryAsync(x => x.SupplierId, x => x.Total, cancellationToken);
        return invoices.ToDictionary(
            x => x.SupplierId,
            x => new SupplierUse(x.Count, totals.GetValueOrDefault(x.SupplierId), x.Last));
    }

    public Task<bool> AnyForSupplierAsync(Guid supplierId, CancellationToken cancellationToken = default) =>
        db.PurchaseInvoices.AnyAsync(i => i.SupplierId == supplierId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ExpenseItemUse>> GetExpenseItemUseAsync(CancellationToken cancellationToken = default)
    {
        var lines = await db.Set<PurchaseInvoiceLine>().AsNoTracking()
            .Where(l => l.ExpenseItemId != null)
            .Join(db.PurchaseInvoices, l => l.InvoiceId, i => i.Id, (l, i) => new { ExpenseItemId = l.ExpenseItemId!.Value, l.Cost, i.Status, i.PostedAtUtc })
            .ToListAsync(cancellationToken);
        return lines
            .GroupBy(l => l.ExpenseItemId)
            .ToDictionary(g => g.Key, g => new ExpenseItemUse(
                g.Count(),
                g.Where(l => l.Status == PurchaseInvoiceStatus.Posted).Sum(l => l.Cost ?? 0),
                g.Max(l => l.PostedAtUtc)));
    }

    public Task<bool> AnyForExpenseItemAsync(Guid expenseItemId, CancellationToken cancellationToken = default) =>
        db.Set<PurchaseInvoiceLine>().AnyAsync(l => l.ExpenseItemId == expenseItemId, cancellationToken);
}

internal sealed class ApprovalRequestRepository(NodeDbContext db) : IApprovalRequestRepository
{
    public Task<ApprovalRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ApprovalRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(ApprovalRequest aggregate) => db.ApprovalRequests.Add(aggregate);

    public async Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.Status == ApprovalStatus.Pending)
            .OrderBy(r => r.RequestedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ApprovalRequest>> GetDecidedAsync(int take, CancellationToken cancellationToken = default) =>
        await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.Status != ApprovalStatus.Pending)
            .OrderByDescending(r => r.DecidedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    // Tracked: a manager's own delete approves it.
    public Task<ApprovalRequest?> GetPendingForAsync(Guid recordId, CancellationToken cancellationToken = default) =>
        db.ApprovalRequests.SingleOrDefaultAsync(r => r.RecordId == recordId && r.Status == ApprovalStatus.Pending, cancellationToken);
}

internal sealed class SafeTransactionRepository(NodeDbContext db) : ISafeTransactionRepository
{
    public Task<SafeTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SafeTransactions.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public void Add(SafeTransaction aggregate) => db.SafeTransactions.Add(aggregate);

    public async Task<decimal> SafeBalanceAsync(DateOnly? through = null, CancellationToken cancellationToken = default) =>
        await db.SafeTransactions
            .Where(t => t.Fund == CashFund.Safe && (through == null || t.BusinessDate <= through))
            .SumAsync(t => t.Amount, cancellationToken);

    // Tracked: voiding it marks it.
    public Task<SafeTransaction?> GetPaymentOfInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        db.SafeTransactions.SingleOrDefaultAsync(
            t => t.PurchaseInvoiceId == invoiceId && t.Kind == SafeTransactionKind.InvoicePayment && t.ReversalOf == null && t.VoidedAtUtc == null,
            cancellationToken);

    public async Task<IReadOnlySet<Guid>> PaidInvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken = default) =>
        (await db.SafeTransactions.AsNoTracking()
            .Where(t => t.Kind == SafeTransactionKind.InvoicePayment && t.ReversalOf == null && t.VoidedAtUtc == null
                && t.PurchaseInvoiceId != null && invoiceIds.Contains(t.PurchaseInvoiceId.Value))
            .Select(t => t.PurchaseInvoiceId!.Value)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    public Task<bool> AnyOpeningBalanceAsync(CancellationToken cancellationToken = default) =>
        db.SafeTransactions.AnyAsync(t => t.Kind == SafeTransactionKind.OpeningBalance && t.ReversalOf == null && t.VoidedAtUtc == null, cancellationToken);
}

internal sealed class SupplierLedgerRepository(NodeDbContext db) : ISupplierLedgerRepository
{
    public Task<SupplierLedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SupplierLedgerEntries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(SupplierLedgerEntry aggregate) => db.SupplierLedgerEntries.Add(aggregate);

    public async Task<decimal> BalanceAsync(Guid supplierId, CancellationToken cancellationToken = default) =>
        await db.SupplierLedgerEntries.Where(e => e.SupplierId == supplierId).SumAsync(e => e.Amount, cancellationToken);

    public async Task<IReadOnlyList<SupplierLedgerEntry>> GetBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        await db.SupplierLedgerEntries.Where(e => e.SourceId == sourceId).ToListAsync(cancellationToken);
}

internal sealed class PurchaseCategoryRepository(NodeDbContext db) : IPurchaseCategoryRepository
{
    public Task<PurchaseCategory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.PurchaseCategories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(PurchaseCategory aggregate) => db.PurchaseCategories.Add(aggregate);

    public void Remove(PurchaseCategory category) => db.PurchaseCategories.Remove(category);

    public async Task<IReadOnlyList<PurchaseCategory>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.PurchaseCategories.AsNoTracking().ToListAsync(cancellationToken);

    public Task<PurchaseCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        db.PurchaseCategories.AsNoTracking().SingleOrDefaultAsync(c => c.NormalizedName == normalizedName, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, PurchaseCategoryUse>> GetUseAsync(CancellationToken cancellationToken = default)
    {
        var materials = await db.RawMaterials.AsNoTracking().GroupBy(m => m.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var expenses = await db.ExpenseItems.AsNoTracking().GroupBy(e => e.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return materials.Keys.Union(expenses.Keys)
            .ToDictionary(id => id, id => new PurchaseCategoryUse(materials.GetValueOrDefault(id), expenses.GetValueOrDefault(id)));
    }
}

internal sealed class ExpenseItemRepository(NodeDbContext db) : IExpenseItemRepository
{
    public Task<ExpenseItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ExpenseItems.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(ExpenseItem aggregate) => db.ExpenseItems.Add(aggregate);

    public void Remove(ExpenseItem item) => db.ExpenseItems.Remove(item);

    public async Task<IReadOnlyList<ExpenseItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.ExpenseItems.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExpenseItem>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await db.ExpenseItems.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);

    // Tracked: to be moved to another category.
    public async Task<IReadOnlyList<ExpenseItem>> GetInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        await db.ExpenseItems.Where(e => e.CategoryId == categoryId).ToListAsync(cancellationToken);

    public Task<ExpenseItem?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        db.ExpenseItems.AsNoTracking().SingleOrDefaultAsync(e => e.NormalizedName == normalizedName, cancellationToken);
}

internal sealed class SupplierRepository(NodeDbContext db) : ISupplierRepository
{
    public Task<Supplier?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Add(Supplier aggregate) => db.Suppliers.Add(aggregate);

    public void Remove(Supplier supplier) => db.Suppliers.Remove(supplier);

    public async Task<IReadOnlyList<Supplier>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.Suppliers.AsNoTracking().ToListAsync(cancellationToken);

    public Task<Supplier?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.NormalizedName == normalizedName, cancellationToken);
}

// An invoice's photo or scan: one row per invoice, its bytes alone.
public sealed class PurchaseInvoiceAttachment
{
    public Guid InvoiceId { get; init; }
    public byte[] Content { get; set; } = [];
}

internal sealed class PurchaseAttachmentStore(NodeDbContext db) : IPurchaseAttachmentStore
{
    // Replaces what a draft had; the invoice's own rules say when that is allowed.
    public void Put(Guid invoiceId, byte[] content)
    {
        var existing = db.PurchaseInvoiceAttachments.Local.FirstOrDefault(a => a.InvoiceId == invoiceId)
            ?? db.PurchaseInvoiceAttachments.Find(invoiceId);
        if (existing is null)
            db.PurchaseInvoiceAttachments.Add(new PurchaseInvoiceAttachment { InvoiceId = invoiceId, Content = content });
        else
            existing.Content = content;
    }

    public async Task<byte[]?> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await db.PurchaseInvoiceAttachments.AsNoTracking()
            .Where(a => a.InvoiceId == invoiceId)
            .Select(a => a.Content)
            .SingleOrDefaultAsync(cancellationToken);
}

internal sealed class PrepRecipeRepository(NodeDbContext db) : IPrepRecipeRepository
{
    private IQueryable<PrepRecipe> WithIngredients => db.PrepRecipes.Include(r => r.Ingredients);

    public Task<PrepRecipe?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithIngredients.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(PrepRecipe aggregate) => db.PrepRecipes.Add(aggregate);

    public async Task<IReadOnlyList<PrepRecipe>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await WithIngredients.AsNoTracking().ToListAsync(cancellationToken);
}

internal sealed class ProductionOrderRepository(NodeDbContext db) : IProductionOrderRepository
{
    private IQueryable<ProductionOrder> WithLines => db.ProductionOrders.Include(o => o.Lines);

    public Task<ProductionOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithLines.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public void Add(ProductionOrder aggregate) => db.ProductionOrders.Add(aggregate);

    public async Task<IReadOnlyList<ProductionOrder>> GetRecentAsync(Guid? prepItemId, int take, CancellationToken cancellationToken = default) =>
        await WithLines.AsNoTracking()
            .Where(o => prepItemId == null || o.PrepItemId == prepItemId)
            .OrderByDescending(o => o.ProducedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
}

// What a deletion request's summary needs from outside the inventory module: names from the
// menu the till holds (deleted items too, as an old recipe may name one), and the business date.
internal sealed class RecordDescriptions(NodeDbContext db, RestaurantClock clock) : IRecordDescriptions
{
    public async Task<string?> MenuItemNameAsync(RecipeTargetKind kind, int catalogItemId, int? variantId, CancellationToken cancellationToken)
    {
        if (kind == RecipeTargetKind.AddOn)
            return await db.AddOns.AsNoTracking().IgnoreQueryFilters().Where(a => a.Id == catalogItemId).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken);

        var item = await db.MenuItems.AsNoTracking().IgnoreQueryFilters().Where(m => m.Id == catalogItemId).Select(m => m.Name).SingleOrDefaultAsync(cancellationToken);
        if (item is null || variantId is null)
            return item;
        var variant = await db.MenuItemVariants.AsNoTracking().Where(v => v.Id == variantId).Select(v => v.Name).SingleOrDefaultAsync(cancellationToken);
        return variant is null ? item : $"{item} ({variant})";
    }

    public DateOnly BusinessDateOf(DateTime utc) => clock.BusinessDateOf(utc);

    public DateOnly LocalDateOf(DateTime utc) => clock.LocalDateOf(utc);
}
