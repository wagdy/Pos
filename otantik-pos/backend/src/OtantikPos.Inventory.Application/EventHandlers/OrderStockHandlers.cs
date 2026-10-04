using MediatR;
using Microsoft.Extensions.Logging;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Inventory.Application.EventHandlers;

// Inventory's whole view of ordering: these events, as catalog ids and quantities. The stock
// arithmetic itself (base or variant recipe, plus add-ons, times quantity) is
// StockRequirements, in the domain.

internal sealed class DeductStockOnOrderSettled(OrderStockPoster poster, IUnitOfWork unitOfWork)
    : INotificationHandler<OrderSettled>
{
    public async Task Handle(OrderSettled notification, CancellationToken cancellationToken)
    {
        await poster.PostAsync(
            notification.Lines.Select(l => (l.OrderItemPublicId, new SoldItem(l.MenuItemId, l.VariantId, l.AddOnIds, l.Quantity))).ToList(),
            StockMovementReason.Sale,
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// BeforeKitchen   never touched stock.
// AfterKitchen    the kitchen had it and nobody paid: the ingredients are waste.
// AfterPayment    the brief's Void After: the ingredients go back into stock, even if the
//                 dish was made. If refunds of served food should count as waste instead,
//                 this is the line to change.
internal sealed class ApplyStockForVoidedItems(OrderStockPoster poster, IUnitOfWork unitOfWork)
    : INotificationHandler<OrderItemsVoided>
{
    public async Task Handle(OrderItemsVoided notification, CancellationToken cancellationToken)
    {
        await poster.PostAsync(LinesAt(notification, VoidStage.AfterKitchen), StockMovementReason.Waste, cancellationToken);
        await poster.PostAsync(LinesAt(notification, VoidStage.AfterPayment), StockMovementReason.VoidReturn, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static List<(Guid, SoldItem)> LinesAt(OrderItemsVoided notification, VoidStage stage) =>
        notification.Lines
            .Where(l => l.Stage == stage)
            .Select(l => (l.OrderItemPublicId, new SoldItem(l.MenuItemId, l.VariantId, l.AddOnIds, l.Quantity)))
            .ToList();
}

// Turns sold lines into stock movements. Shared by the two handlers above, which differ only in
// which lines they pass and why. Adds the movements; the caller saves.
internal sealed class OrderStockPoster(
    IRecipeRepository recipes,
    IRawMaterialRepository materials,
    IStockMovementRepository movements,
    ILogger<OrderStockPoster> logger)
{
    public async Task PostAsync(IReadOnlyList<(Guid OrderItemPublicId, SoldItem Item)> lines, StockMovementReason reason, CancellationToken cancellationToken)
    {
        // A line already posted for this reason is skipped. That is what makes a redelivered
        // event harmless: all of an event's lines are saved in one transaction, so a line is
        // either fully posted or not at all. The movement's SourceId is the order item's
        // PublicId, the same on the cloud and the till.
        var pending = new List<(Guid OrderItemPublicId, SoldItem Item)>();
        foreach (var line in lines)
        {
            if (!await movements.ExistsAsync(line.OrderItemPublicId, reason, cancellationToken))
                pending.Add(line);
        }
        if (pending.Count == 0)
            return;

        var relevant = await recipes.GetByTargetsAsync(StockRequirements.TargetsFor(pending.Select(p => p.Item)), cancellationToken);
        var needs = pending.Select(p => (p.OrderItemPublicId, Needs: StockRequirements.For(p.Item, relevant))).ToList();

        var materialsById = (await materials.GetByIdsAsync(needs.SelectMany(n => n.Needs.Keys).Distinct().ToList(), cancellationToken))
            .ToDictionary(m => m.Id);

        foreach (var (orderItemPublicId, need) in needs)
        {
            foreach (var (rawMaterialId, quantity) in need)
            {
                // Should not happen: raw materials are never deleted. Logged and skipped rather
                // than thrown, because a throw would have the outbox redeliver this event
                // forever, and the rest of the order would never be posted either.
                if (!materialsById.TryGetValue(rawMaterialId, out var material))
                {
                    logger.LogWarning("Raw material {RawMaterialId} in a recipe does not exist; order item {OrderItemPublicId} not posted against it",
                        rawMaterialId, orderItemPublicId);
                    continue;
                }

                movements.Add(Post(material, quantity, orderItemPublicId, reason));
            }
        }
    }

    private static StockMovement Post(RawMaterial material, decimal quantity, Guid orderItemPublicId, StockMovementReason reason) => reason switch
    {
        StockMovementReason.Sale => material.RecordSale(quantity, orderItemPublicId),
        StockMovementReason.Waste => material.RecordWaste(quantity, orderItemPublicId),
        StockMovementReason.VoidReturn => material.RecordVoidReturn(quantity, orderItemPublicId),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a reason an order can post."),
    };
}
