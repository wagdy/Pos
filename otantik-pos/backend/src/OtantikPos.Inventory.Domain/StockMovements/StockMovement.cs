using Otantik.BuildingBlocks;

namespace OtantikPos.Inventory.Domain.StockMovements;

// One entry in the stock ledger. Append-only: a mistake is corrected by a new movement, never
// by editing an old one, so a material's QuantityOnHand always equals the sum of its
// movements and can be rebuilt from them.
public sealed class StockMovement : AggregateRoot
{
    private StockMovement() { }

    // Internal: entries are created only by RawMaterial, together with the balance change
    // they explain.
    internal StockMovement(Guid rawMaterialId, decimal quantity, StockMovementReason reason, Guid sourceId)
    {
        RawMaterialId = rawMaterialId;
        Quantity = quantity;
        Reason = reason;
        SourceId = sourceId;
        OccurredAtUtc = DateTime.UtcNow;
    }

    public Guid RawMaterialId { get; private set; }

    // Signed: negative for stock out, positive for stock in.
    public decimal Quantity { get; private set; }

    public StockMovementReason Reason { get; private set; }

    // What caused it: the order item for a sale, waste or void return; the purchase or stock
    // count otherwise. (SourceId, RawMaterialId, Reason) is unique, which is what makes
    // handling the same event twice harmless.
    public Guid SourceId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }
}
