using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Application.Production;

// Production orders: the kitchen's prep work, logged. A prep item, how much was planned (which
// sets the ingredients) and how much came out; posting takes the ingredients off stock at their
// average cost and puts the prep item in at their total over what came out. OrderId is made by
// the till. Quantities are in each material's own unit; costs per its display unit.

// One ingredient of a batch. OnHand and Short only on a preview: what the records show now, and
// how much more the batch takes than that.
public sealed record ProductionLineDto(
    Guid RawMaterialId,
    string? Code,
    string Material,
    bool IsPrepItem,
    UnitOfMeasure Unit,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal Quantity,
    decimal? OnHand,
    decimal? Short,
    decimal? CostPerPurchaseUnit,
    decimal? Cost);

public sealed record ProductionOrderDto(
    Guid Id,
    Guid PrepItemId,
    string PrepItem,
    UnitOfMeasure Unit,
    string DisplayUnit,
    decimal DisplayUnitSize,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    // Produced against planned: under 100 is what was lost in the making.
    decimal YieldPercent,
    decimal TotalCost,
    decimal UnitCostPerDisplayUnit,
    DateTime ProducedAtUtc,
    string ProducedBy,
    string? Notes,
    IReadOnlyList<ProductionLineDto> Lines)
{
    public static ProductionOrderDto From(ProductionOrder order, IReadOnlyDictionary<Guid, RawMaterial> materials)
    {
        var item = materials.GetValueOrDefault(order.PrepItemId);
        var size = item?.PurchaseUnitSize ?? 1;
        return new ProductionOrderDto(
            order.Id, order.PrepItemId, item?.Name ?? "A removed prep item", item?.Unit ?? UnitOfMeasure.Gram, item?.PurchaseUnit ?? "unit", size,
            order.PlannedQuantity, order.ProducedQuantity, Math.Round(order.ProducedQuantity * 100 / order.PlannedQuantity, 1),
            order.TotalCost, PrepItemCosting.Money(order.UnitCost * size), order.ProducedAtUtc, order.ProducedBy, order.Notes,
            order.Lines
                .Select(l => materials.GetValueOrDefault(l.RawMaterialId) is { } m
                    ? new ProductionLineDto(m.Id, m.Code, m.Name, m.IsPrepItem, m.Unit, m.PurchaseUnit, m.PurchaseUnitSize, l.Quantity, null, null,
                        PrepItemCosting.Money(l.UnitCost * m.PurchaseUnitSize), l.Cost)
                    : new ProductionLineDto(l.RawMaterialId, null, "A removed material", false, UnitOfMeasure.Piece, "unit", 1, l.Quantity, null, null, null, l.Cost))
                .OrderByDescending(l => l.Cost)
                .ToList());
    }
}

// What a batch would take and cost if made now: the review before it is posted.
public sealed record ProductionPreviewDto(
    Guid PrepItemId,
    string PrepItem,
    UnitOfMeasure Unit,
    string DisplayUnit,
    decimal DisplayUnitSize,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    IReadOnlyList<ProductionLineDto> Lines,
    decimal? TotalCost,
    decimal? UnitCostPerDisplayUnit,
    // No cost yet: the batch cannot be posted until they have one.
    IReadOnlyList<string> MissingPrices,
    // Less on record than the batch takes: it can still be posted, and goes below zero.
    IReadOnlyList<string> Shortages);

public sealed record PreviewProductionQuery(Guid PrepItemId, decimal PlannedQuantity, decimal? ProducedQuantity = null)
    : IRequest<ProductionPreviewDto>;

internal sealed class PreviewProductionQueryHandler(IRawMaterialRepository materials, IPrepRecipeRepository recipes)
    : IRequestHandler<PreviewProductionQuery, ProductionPreviewDto>
{
    public async Task<ProductionPreviewDto> Handle(PreviewProductionQuery request, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        if (all.GetValueOrDefault(request.PrepItemId) is not { IsPrepItem: true } item)
            throw new NotFoundException("Prep item", request.PrepItemId);
        var recipe = await recipes.GetByIdAsync(item.Id, cancellationToken);
        if (recipe is null || recipe.Ingredients.Count == 0)
            throw new DomainException($"{item.Name} has no ingredients yet: write its recipe first.");
        var produced = request.ProducedQuantity ?? request.PlannedQuantity;
        if (produced <= 0)
            throw new DomainException("Enter how much was made, more than nothing.");

        var lines = recipe.RequirementsFor(request.PlannedQuantity)
            .Select(r =>
            {
                var m = all.GetValueOrDefault(r.RawMaterialId) ?? throw new NotFoundException(nameof(RawMaterial), r.RawMaterialId);
                return new ProductionLineDto(
                    m.Id, m.Code, m.Name, m.IsPrepItem, m.Unit, m.PurchaseUnit, m.PurchaseUnitSize, r.Quantity, m.QuantityOnHand,
                    r.Quantity > m.QuantityOnHand ? r.Quantity - Math.Max(m.QuantityOnHand, 0) : null,
                    m.CostPerPurchaseUnit is { } c ? PrepItemCosting.Money(c) : null,
                    m.AverageCost is { } unit ? PrepItemCosting.Money(r.Quantity * unit) : null);
            })
            .OrderByDescending(l => l.Cost ?? 0)
            .ToList();
        var missing = lines.Where(l => l.Cost is null).Select(l => l.Material).ToList();
        decimal? total = missing.Count == 0 ? lines.Sum(l => l.Cost!.Value) : null;

        return new ProductionPreviewDto(
            item.Id, item.Name, item.Unit, item.PurchaseUnit, item.PurchaseUnitSize, request.PlannedQuantity, produced, lines,
            total, total is { } t ? PrepItemCosting.Money(t / produced * item.PurchaseUnitSize) : null,
            missing, lines.Where(l => l.Short is not null).Select(l => l.Material).ToList());
    }
}

// Makes the batch. Posting again, after an answer was lost, returns the order and changes nothing.
public sealed record MakeProductionOrderCommand(
    Guid OrderId, Guid PrepItemId, decimal PlannedQuantity, decimal ProducedQuantity, string? Notes = null, string StaffName = "")
    : IRequest<ProductionOrderDto>, IRetryOnConflict;

internal sealed class MakeProductionOrderCommandHandler(
    IProductionOrderRepository orders,
    IPrepRecipeRepository recipes,
    IRawMaterialRepository materials,
    IStockMovementRepository movements,
    IUnitOfWork unitOfWork) : IRequestHandler<MakeProductionOrderCommand, ProductionOrderDto>
{
    public async Task<ProductionOrderDto> Handle(MakeProductionOrderCommand request, CancellationToken cancellationToken)
    {
        if (await orders.GetByIdAsync(request.OrderId, cancellationToken) is { } made)
            return ProductionOrderDto.From(made, (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id));

        var item = await materials.GetRequiredAsync(request.PrepItemId, cancellationToken);
        if (!item.IsPrepItem)
            throw new DomainException($"{item.Name} is bought, not made: it comes in on a purchase invoice.");
        var recipe = await recipes.GetByIdAsync(item.Id, cancellationToken)
            ?? throw new DomainException($"{item.Name} has no ingredients yet: write its recipe first.");

        // Tracked, as they change: their stock and, for the prep item, its average.
        var ingredients = recipe.Ingredients.Count == 0
            ? new Dictionary<Guid, RawMaterial>()
            : (await materials.GetByIdsAsync(recipe.Ingredients.Select(i => i.RawMaterialId).ToList(), cancellationToken)).ToDictionary(m => m.Id);

        var (order, ledger) = ProductionOrder.Make(
            request.OrderId, item, recipe, ingredients, request.PlannedQuantity, request.ProducedQuantity, request.StaffName, request.Notes);
        orders.Add(order);
        foreach (var movement in ledger)
            movements.Add(movement);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        ingredients[item.Id] = item;
        return ProductionOrderDto.From(order, ingredients);
    }
}

// The production ledger, newest first: one prep item's when it is given.
public sealed record GetProductionOrdersQuery(Guid? PrepItemId = null) : IRequest<IReadOnlyList<ProductionOrderDto>>;

internal sealed class GetProductionOrdersQueryHandler(IProductionOrderRepository orders, IRawMaterialRepository materials)
    : IRequestHandler<GetProductionOrdersQuery, IReadOnlyList<ProductionOrderDto>>
{
    public async Task<IReadOnlyList<ProductionOrderDto>> Handle(GetProductionOrdersQuery request, CancellationToken cancellationToken)
    {
        var all = (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id);
        return (await orders.GetRecentAsync(request.PrepItemId, 200, cancellationToken))
            .Select(o => ProductionOrderDto.From(o, all))
            .ToList();
    }
}

public sealed record GetProductionOrderQuery(Guid OrderId) : IRequest<ProductionOrderDto?>;

internal sealed class GetProductionOrderQueryHandler(IProductionOrderRepository orders, IRawMaterialRepository materials)
    : IRequestHandler<GetProductionOrderQuery, ProductionOrderDto?>
{
    public async Task<ProductionOrderDto?> Handle(GetProductionOrderQuery request, CancellationToken cancellationToken) =>
        await orders.GetByIdAsync(request.OrderId, cancellationToken) is { } order
            ? ProductionOrderDto.From(order, (await materials.GetAllAsync(cancellationToken)).ToDictionary(m => m.Id))
            : null;
}
