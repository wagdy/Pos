using Microsoft.EntityFrameworkCore;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockMovements;

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

    public void Remove(Recipe recipe) => db.Recipes.Remove(recipe);

    public Task<Recipe?> GetByTargetAsync(RecipeTarget target, CancellationToken cancellationToken = default) =>
        WithIngredients.SingleOrDefaultAsync(
            r => r.TargetKind == target.Kind && r.CatalogItemId == target.CatalogItemId && r.VariantId == target.VariantId,
            cancellationToken);

    // Narrowed in SQL by catalog id, then matched exactly in memory. A recipe target is three
    // columns, which an IN list cannot express, and the candidates are a handful.
    public async Task<IReadOnlyList<Recipe>> GetByTargetsAsync(IReadOnlyCollection<RecipeTarget> targets, CancellationToken cancellationToken = default)
    {
        var catalogIds = targets.Select(t => t.CatalogItemId).Distinct().ToList();
        var candidates = await WithIngredients.Where(r => catalogIds.Contains(r.CatalogItemId)).ToListAsync(cancellationToken);
        return candidates.Where(r => targets.Contains(r.Target)).ToList();
    }
}

internal sealed class StockMovementRepository(NodeDbContext db) : IStockMovementRepository
{
    public Task<StockMovement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.StockMovements.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(StockMovement aggregate) => db.StockMovements.Add(aggregate);

    public Task<bool> ExistsAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default) =>
        db.StockMovements.AnyAsync(m => m.SourceId == sourceId && m.Reason == reason, cancellationToken);
}
