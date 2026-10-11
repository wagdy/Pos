using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.Production;

// Prep items: made in the kitchen (dough, house sauces), kept in stock, used by dishes. Each is a
// raw material of the Prep kind, with a recipe for one batch. Quantities are in each material's
// own unit (grams, millilitres, pieces); costs per its display unit (kg, L), as prices are written.

// One ingredient of a batch, costed at its average now. BatchCost is what it adds to one batch;
// Share is that against the batch.
public sealed record PrepIngredientCostDto(
    Guid RawMaterialId,
    string? Code,
    string Name,
    bool IsPrepItem,
    UnitOfMeasure Unit,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal Quantity,
    decimal YieldPercent,
    decimal AsPurchasedQuantity,
    decimal? CostPerPurchaseUnit,
    decimal? BatchCost,
    decimal? SharePercent);

// A prep item's profile. The standard cost is what a batch costs by its recipe at today's
// averages; the average cost is what the batches on the shelf actually cost, moved by each
// production order as a delivery moves a bought material's.
public sealed record PrepItemDto(
    Guid Id,
    string? Code,
    string Name,
    // Its purchase category's name, as a material's: the count sheet groups it there.
    string Category,
    UnitOfMeasure Unit,
    string DisplayUnit,
    decimal DisplayUnitSize,
    decimal ReorderLevel,
    decimal BatchQuantity,
    IReadOnlyList<PrepIngredientCostDto> Ingredients,
    decimal? StandardBatchCost,
    decimal? StandardCostPerDisplayUnit,
    IReadOnlyList<string> MissingPrices,
    decimal QuantityOnHand,
    decimal? AverageCostPerDisplayUnit,
    decimal? StockValue,
    DateTime? LastProducedAtUtc,
    decimal? LastBatchCostPerDisplayUnit,
    IReadOnlyList<ProductionOrderDto> Recent,
    Guid CategoryId);

public sealed record PrepIngredientEntry(Guid RawMaterialId, decimal Quantity, decimal? YieldPercent = null);

internal static class PrepItemCosting
{
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static PrepItemDto Dto(
        RawMaterial item,
        PrepRecipe? recipe,
        IReadOnlyDictionary<Guid, RawMaterial> materials,
        IReadOnlyList<ProductionOrder> orders,
        IReadOnlyDictionary<Guid, PurchaseCategory> categories)
    {
        var lines = (recipe?.Ingredients ?? [])
            .Select(i => (Ingredient: i, Material: materials.GetValueOrDefault(i.RawMaterialId)))
            .Where(x => x.Material is not null)
            .Select(x => (x.Ingredient, Material: x.Material!, Cost: x.Ingredient.AsPurchasedQuantity * x.Material!.AverageCost))
            .ToList();
        var batchCost = lines.Sum(l => l.Cost ?? 0);
        var missing = lines.Where(l => l.Cost is null).Select(l => l.Material.Name).ToList();
        var batch = recipe?.BatchQuantity ?? 0;
        var last = orders.Where(o => o.PrepItemId == item.Id).MaxBy(o => o.ProducedAtUtc);

        return new PrepItemDto(
            item.Id, item.Code, item.Name, categories.GetValueOrDefault(item.CategoryId)?.Name ?? PurchaseCategory.GeneralName, item.Unit, item.PurchaseUnit, item.PurchaseUnitSize, item.ReorderLevel, batch,
            lines
                .Select(l => new PrepIngredientCostDto(
                    l.Material.Id, l.Material.Code, l.Material.Name, l.Material.IsPrepItem, l.Material.Unit, l.Material.PurchaseUnit,
                    l.Material.PurchaseUnitSize, l.Ingredient.Quantity, l.Ingredient.YieldPercent, Math.Round(l.Ingredient.AsPurchasedQuantity, 3),
                    l.Material.CostPerPurchaseUnit is { } c ? Money(c) : null,
                    l.Cost is { } cost ? Money(cost) : null,
                    l.Cost is { } part && batchCost > 0 ? Math.Round(part * 100 / batchCost, 1) : null))
                .OrderByDescending(l => l.BatchCost ?? 0)
                .ThenBy(l => l.Name)
                .ToList(),
            lines.Count > 0 ? Money(batchCost) : null,
            lines.Count > 0 && batch > 0 ? Money(batchCost / batch * item.PurchaseUnitSize) : null,
            missing,
            item.QuantityOnHand,
            item.CostPerPurchaseUnit is { } average ? Money(average) : null,
            item.AverageCost is { } unit ? Money(unit * Math.Max(item.QuantityOnHand, 0)) : null,
            last?.ProducedAtUtc,
            last is null ? null : Money(last.UnitCost * item.PurchaseUnitSize),
            orders.Where(o => o.PrepItemId == item.Id).Select(o => ProductionOrderDto.From(o, materials)).ToList(),
            item.CategoryId);
    }

    // Would `prepItemId` end up made from itself, through the ingredients given and the other
    // prep items' recipes? Dough from a sauce from the dough.
    public static string? Loop(Guid prepItemId, IEnumerable<Guid> ingredientIds, IReadOnlyCollection<PrepRecipe> recipes, IReadOnlyDictionary<Guid, RawMaterial> materials)
    {
        var uses = recipes.ToDictionary(r => r.PrepItemId, r => r.Ingredients.Select(i => i.RawMaterialId).ToList());
        uses[prepItemId] = ingredientIds.ToList();
        foreach (var start in uses[prepItemId])
        {
            var seen = new HashSet<Guid>();
            var stack = new Stack<Guid>([start]);
            while (stack.TryPop(out var id))
            {
                if (id == prepItemId)
                    return materials.GetValueOrDefault(start)?.Name;
                if (seen.Add(id) && uses.TryGetValue(id, out var next))
                    next.ForEach(stack.Push);
            }
        }

        return null;
    }
}

// Every prep item, by name.
public sealed record GetPrepItemsQuery : IRequest<IReadOnlyList<PrepItemDto>>;

internal sealed class GetPrepItemsQueryHandler(
    IRawMaterialRepository materials, IPrepRecipeRepository recipes, IProductionOrderRepository orders, IPurchaseCategoryRepository categories)
    : IRequestHandler<GetPrepItemsQuery, IReadOnlyList<PrepItemDto>>
{
    public async Task<IReadOnlyList<PrepItemDto>> Handle(GetPrepItemsQuery request, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        var byCategory = (await categories.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id);
        var byItem = (await recipes.GetAllAsync(cancellationToken)).ToDictionary(r => r.PrepItemId);
        // The last batch of each, for its last cost; the full history is on its own page.
        var lastOrders = (await orders.GetRecentAsync(null, 500, cancellationToken))
            .GroupBy(o => o.PrepItemId)
            .Select(g => g.MaxBy(o => o.ProducedAtUtc)!)
            .ToList();
        return all.Values
            .Where(m => m.IsPrepItem)
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => PrepItemCosting.Dto(m, byItem.GetValueOrDefault(m.Id), all, lastOrders, byCategory) with { Recent = [] })
            .ToList();
    }
}

// One prep item's profile, with its last batches.
public sealed record GetPrepItemQuery(Guid PrepItemId) : IRequest<PrepItemDto?>;

internal sealed class GetPrepItemQueryHandler(
    IRawMaterialRepository materials, IPrepRecipeRepository recipes, IProductionOrderRepository orders, IPurchaseCategoryRepository categories)
    : IRequestHandler<GetPrepItemQuery, PrepItemDto?>
{
    public async Task<PrepItemDto?> Handle(GetPrepItemQuery request, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        if (all.GetValueOrDefault(request.PrepItemId) is not { IsPrepItem: true } item)
            return null;
        return PrepItemCosting.Dto(
            item,
            await recipes.GetByIdAsync(item.Id, cancellationToken),
            all,
            await orders.GetRecentAsync(item.Id, 20, cancellationToken),
            (await categories.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id));
    }
}

// Adds a prep item (no id) or changes one: what it is, how a batch is written, and the whole list
// of ingredients. Its unit cannot change once it exists: its stock and its batches are in it.
public sealed record SavePrepItemCommand(
    Guid? PrepItemId,
    string Name,
    UnitOfMeasure Unit,
    decimal BatchQuantity,
    IReadOnlyList<PrepIngredientEntry> Ingredients,
    string? Code = null,
    Guid? CategoryId = null,
    string? DisplayUnit = null,
    decimal? DisplayUnitSize = null,
    decimal ReorderLevel = 0) : IRequest<PrepItemDto>;

internal sealed class SavePrepItemCommandHandler(
    IRawMaterialRepository materials,
    IPrepRecipeRepository recipes,
    IProductionOrderRepository orders,
    IPurchaseCategoryRepository categories,
    IUnitOfWork unitOfWork) : IRequestHandler<SavePrepItemCommand, PrepItemDto>
{
    public async Task<PrepItemDto> Handle(SavePrepItemCommand request, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        RawMaterial item;
        PrepRecipe? recipe = null;
        if (request.PrepItemId is { } id)
        {
            // GetAll reads without tracking: the one to change is loaded to be changed.
            item = await materials.GetRequiredAsync(id, cancellationToken);
            if (!item.IsPrepItem)
                throw new DomainException($"{item.Name} is a bought material, not a prep item.");
            if (item.Unit != request.Unit)
                throw new DomainException($"{item.Name} is kept in {item.Unit.ToString().ToLowerInvariant()}s: its stock and batches are in them. Add a new prep item for another unit.");
            item.Rename(request.Name);
            recipe = await recipes.GetByIdAsync(item.Id, cancellationToken);
        }
        else
        {
            materials.Add(item = new RawMaterial(request.Name, request.Unit, kind: MaterialKind.Prep));
        }

        item.SetReorderLevel(request.ReorderLevel);
        var category = await categories.ResolveAsync(request.CategoryId, cancellationToken);
        item.Describe(request.Code, category.Id);
        if (request.DisplayUnit is { } unit && request.DisplayUnitSize is { } size)
            item.SetPurchaseUnit(unit, size);

        var ingredients = request.Ingredients
            .Select(i => (Material: all.GetValueOrDefault(i.RawMaterialId) ?? throw new NotFoundException(nameof(RawMaterial), i.RawMaterialId), i.Quantity, i.YieldPercent))
            .ToList();
        if (PrepItemCosting.Loop(item.Id, ingredients.Select(i => i.Material.Id), await recipes.GetAllAsync(cancellationToken), all) is { } through)
            throw new DomainException($"{through} is made with {item.Name}, so {item.Name} cannot be made with {through}.");

        if (recipe is null)
            recipes.Add(recipe = new PrepRecipe(item, request.BatchQuantity));
        else
            recipe.SetBatch(request.BatchQuantity);
        recipe.Replace(ingredients);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        all[item.Id] = item;
        return PrepItemCosting.Dto(
            item, recipe, all, await orders.GetRecentAsync(item.Id, 20, cancellationToken),
            new Dictionary<Guid, PurchaseCategory> { [category.Id] = category });
    }
}
