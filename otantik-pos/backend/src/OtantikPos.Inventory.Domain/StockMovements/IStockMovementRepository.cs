using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.StockMovements;

public interface IStockMovementRepository : IRepository<StockMovement>
{
    // The outbox delivers at least once, so a handler can see the same event twice. Checking
    // this first lets it skip the repeat; the unique index on (SourceId, RawMaterialId, Reason)
    // catches two deliveries racing each other.
    Task<bool> ExistsAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default);
}
