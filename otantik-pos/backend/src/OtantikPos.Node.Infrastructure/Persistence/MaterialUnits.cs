using Microsoft.EntityFrameworkCore;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;

namespace OtantikPos.Node.Infrastructure.Persistence;

// The records naming a bought material, in the unit it is counted in: read to say what a unit
// change restates, and restated, row by row in the database, when it is made. Quantities are divided by
// how many of the old unit one of the new is, costs per unit multiplied, to the columns' places;
// totals of money are not touched.
internal sealed class MaterialUnits(NodeDbContext db) : IMaterialUnits
{
    public async Task<MaterialUsage> UsageAsync(RawMaterial material, Guid? exceptRequestId, CancellationToken cancellationToken)
    {
        var id = material.Id;
        var text = id.ToString();
        // A request waiting for a manager about it, or holding it in what it would make. They are
        // few, and the payload is JSON: read, and looked through here.
        var pending = await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.Status == ApprovalStatus.Pending && r.Id != exceptRequestId)
            .Select(r => new { r.RecordId, r.Payload })
            .ToListAsync(cancellationToken);
        return new MaterialUsage(
            await db.StockMovements.CountAsync(m => m.RawMaterialId == id, cancellationToken),
            await db.Set<PurchaseInvoiceLine>().CountAsync(l => l.RawMaterialId == id, cancellationToken),
            await db.Set<RecipeIngredient>().Where(i => i.RawMaterialId == id).Select(i => i.RecipeId).Distinct().CountAsync(cancellationToken),
            await db.Set<PrepIngredient>().Where(i => i.RawMaterialId == id).Select(i => i.PrepRecipeId).Distinct().CountAsync(cancellationToken),
            await db.Set<StockCountLine>().Where(l => l.RawMaterialId == id).Select(l => l.StockCountId).Distinct().CountAsync(cancellationToken),
            await db.Set<ProductionOrderLine>().Where(l => l.RawMaterialId == id).Select(l => l.ProductionOrderId).Distinct().CountAsync(cancellationToken),
            pending.Count(r => r.RecordId == id || r.Payload?.Contains(text, StringComparison.OrdinalIgnoreCase) == true));
    }

    public async Task RestateAndSaveAsync(RawMaterial material, decimal oneNewIsOld, CancellationToken cancellationToken)
    {
        var (id, r) = (material.Id, oneNewIsOld);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.StockMovements.Where(m => m.RawMaterialId == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Quantity, m => Math.Round(m.Quantity / r, 3))
            .SetProperty(m => m.UnitCost, m => m.UnitCost * r), cancellationToken);
        await db.Set<PurchaseInvoiceLine>().Where(l => l.RawMaterialId == id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.Quantity, l => Math.Round(l.Quantity / r, 3))
            .SetProperty(l => l.UnitSize, l => Math.Round(l.UnitSize / r, 3)), cancellationToken);
        await db.Set<RecipeIngredient>().Where(i => i.RawMaterialId == id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Quantity, i => Math.Round(i.Quantity / r, 3)), cancellationToken);
        await db.Set<PrepIngredient>().Where(i => i.RawMaterialId == id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Quantity, i => Math.Round(i.Quantity / r, 3)), cancellationToken);
        await db.Set<StockCountLine>().Where(l => l.RawMaterialId == id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.CountedQuantity, l => Math.Round(l.CountedQuantity / r, 3))
            .SetProperty(l => l.BookQuantity, l => l.BookQuantity == null ? null : Math.Round(l.BookQuantity.Value / r, 3))
            .SetProperty(l => l.UnitCost, l => l.UnitCost * r), cancellationToken);
        await db.Set<ProductionOrderLine>().Where(l => l.RawMaterialId == id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.Quantity, l => Math.Round(l.Quantity / r, 3))
            .SetProperty(l => l.UnitCost, l => l.UnitCost * r), cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
