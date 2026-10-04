using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Domain.RawMaterials;

public sealed class RawMaterial : AggregateRoot
{
    private RawMaterial() { }

    public RawMaterial(string name, UnitOfMeasure unit, decimal reorderLevel = 0)
    {
        Rename(name);
        Unit = unit;
        SetReorderLevel(reorderLevel);
    }

    public string Name { get; private set; } = string.Empty;

    public UnitOfMeasure Unit { get; private set; }

    // Can go negative; see Move.
    public decimal QuantityOnHand { get; private set; }

    public decimal ReorderLevel { get; private set; }

    public bool NeedsReorder => QuantityOnHand <= ReorderLevel;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Raw material name is required.");

        Name = name.Trim();
    }

    public void SetReorderLevel(decimal reorderLevel)
    {
        if (reorderLevel < 0)
            throw new DomainException("Reorder level cannot be negative.");

        ReorderLevel = reorderLevel;
    }

    // Each method below changes QuantityOnHand and returns the ledger entry that explains the
    // change. The caller adds it to IStockMovementRepository in the same unit of work.
    // StockMovement has no public constructor, so every entry in the ledger was produced here.

    public StockMovement RecordSale(decimal quantity, Guid orderItemId) =>
        Move(-Positive(quantity), StockMovementReason.Sale, orderItemId);

    public StockMovement RecordWaste(decimal quantity, Guid orderItemId) =>
        Move(-Positive(quantity), StockMovementReason.Waste, orderItemId);

    public StockMovement RecordVoidReturn(decimal quantity, Guid orderItemId) =>
        Move(Positive(quantity), StockMovementReason.VoidReturn, orderItemId);

    public StockMovement ReceivePurchase(decimal quantity, Guid purchaseId) =>
        Move(Positive(quantity), StockMovementReason.Purchase, purchaseId);

    public StockMovement AdjustToCount(decimal countedQuantity, Guid stockCountId)
    {
        if (countedQuantity < 0)
            throw new DomainException("A counted quantity cannot be negative.");

        return Move(countedQuantity - QuantityOnHand, StockMovementReason.CountAdjustment, stockCountId);
    }

    // Stock is allowed to go negative. Deduction runs after the sale has already happened, so
    // refusing it would not un-sell the dish; it would only lose the record. A negative
    // balance is the signal that a delivery was never booked in or a recipe quantity is wrong,
    // and a stock count puts it right.
    private StockMovement Move(decimal delta, StockMovementReason reason, Guid sourceId)
    {
        QuantityOnHand += delta;
        return new StockMovement(Id, delta, reason, sourceId);
    }

    private static decimal Positive(decimal quantity) =>
        quantity > 0 ? quantity : throw new DomainException("Quantity must be greater than zero.");
}
