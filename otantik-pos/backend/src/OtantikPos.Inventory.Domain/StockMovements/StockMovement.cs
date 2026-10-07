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
    internal StockMovement(
        Guid rawMaterialId, decimal quantity, StockMovementReason reason, Guid sourceId, decimal? unitCost,
        string? note = null, string? recordedBy = null)
    {
        RawMaterialId = rawMaterialId;
        Quantity = quantity;
        Reason = reason;
        SourceId = sourceId;
        UnitCost = unitCost;
        Note = note;
        RecordedBy = recordedBy;
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

    // The cost of one unit (gram, millilitre, piece) at that moment: the price paid on a purchase,
    // the average cost otherwise. Null while the material had no price. Quantity × UnitCost is
    // what the movement was worth, so a month's food cost is the sum of its sales' worth.
    public decimal? UnitCost { get; private set; }

    // For an entry made by hand (spoilage): why, and who recorded it. Null for the rest, whose
    // SourceId already says.
    public string? Note { get; private set; }
    public string? RecordedBy { get; private set; }
}
