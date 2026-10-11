using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Domain.RawMaterials;

public enum MaterialKind
{
    // Bought from a supplier, on purchase invoices.
    Purchased = 1,

    // Made in the kitchen from other materials, by a production order: dough, a house sauce. Kept
    // in stock, costed and counted like the rest; dishes use it as an ingredient.
    Prep = 2,
}

public sealed class RawMaterial : AggregateRoot
{
    private RawMaterial() { }

    public RawMaterial(string name, UnitOfMeasure unit, decimal reorderLevel = 0, MaterialKind kind = MaterialKind.Purchased)
    {
        Rename(name);
        Unit = unit;
        Kind = kind;
        SetReorderLevel(reorderLevel);
        (PurchaseUnit, PurchaseUnitSize) = unit switch
        {
            UnitOfMeasure.Gram => ("kg", 1000m),
            UnitOfMeasure.Millilitre => ("L", 1000m),
            _ => ("piece", 1m),
        };
    }

    public string Name { get; private set; } = string.Empty;
    public UnitOfMeasure Unit { get; private set; }
    public MaterialKind Kind { get; private set; } = MaterialKind.Purchased;
    public bool IsPrepItem => Kind == MaterialKind.Prep;

    // Can go negative; see Move.
    public decimal QuantityOnHand { get; private set; }

    public decimal ReorderLevel { get; private set; }
    public bool NeedsReorder => QuantityOnHand <= ReorderLevel;

    // How the reports name it: ING-001. Optional.
    public string? Code { get; private set; }

    // The purchase category it is filed under (Dairy, Meat): the count sheet and the reports
    // group by it, and purchases are added up by it. Every material has one; General until sorted.
    public Guid CategoryId { get; private set; }

    // How it is bought, and so how its price is written: per kg (1000 g), per carton of 30 eggs
    // (30 pieces). Stock, recipes and the ledger stay in Unit; this is only for prices.
    public string PurchaseUnit { get; private set; } = string.Empty;
    public decimal PurchaseUnitSize { get; private set; } = 1;

    // The As Purchased (AP) cost of one Unit (a gram, a millilitre, a piece), averaged over what
    // was bought, weighted by quantity. Null until it has a price. Each stock movement records the
    // cost at its moment, so a month's costs do not change when later prices do.
    public decimal? AverageCost { get; private set; }

    // The share left after trimming, for a recipe line that does not say: 85 for peppers,
    // 98 for eggs. Each recipe line keeps its own.
    public decimal DefaultYieldPercent { get; private set; } = 100;

    // AverageCost per purchase unit, as prices are written: 360 a kg rather than 0.36 a gram.
    public decimal? CostPerPurchaseUnit => AverageCost * PurchaseUnitSize;

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

    public void Describe(string? code, Guid categoryId)
    {
        Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        MoveTo(categoryId);
    }

    public void MoveTo(Guid categoryId)
    {
        if (categoryId == Guid.Empty)
            throw new DomainException("Choose the material's category.");
        CategoryId = categoryId;
    }

    public void SetPurchaseUnit(string name, decimal size)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Name the unit it is bought in, such as kg or carton.");
        if (size <= 0)
            throw new DomainException("The unit it is bought in must hold more than zero.");
        PurchaseUnit = name.Trim();
        PurchaseUnitSize = size;
    }

    public void SetDefaultYield(decimal percent) => DefaultYieldPercent = ValidYield(percent);

    // The price to start from, per purchase unit: for stock already on the shelf when costing
    // starts, or a price agreed before any delivery. Purchases with prices move it from there.
    public void SetCost(decimal costPerPurchaseUnit)
    {
        if (costPerPurchaseUnit < 0)
            throw new DomainException("A cost cannot be negative.");
        AverageCost = costPerPurchaseUnit / PurchaseUnitSize;
    }

    public static decimal ValidYield(decimal percent) =>
        percent is > 0 and <= 100 ? percent : throw new DomainException("Yield is more than 0% and at most 100%.");

    // Each method below changes QuantityOnHand and returns the ledger entry that explains the
    // change. The caller adds it to IStockMovementRepository in the same unit of work.
    // StockMovement has no public constructor, so every entry in the ledger was produced here.

    public StockMovement RecordSale(decimal quantity, Guid orderItemId) =>
        Move(-Positive(quantity), StockMovementReason.Sale, orderItemId, AverageCost);

    public StockMovement RecordWaste(decimal quantity, Guid orderItemId) =>
        Move(-Positive(quantity), StockMovementReason.Waste, orderItemId, AverageCost);

    public StockMovement RecordVoidReturn(decimal quantity, Guid orderItemId) =>
        Move(Positive(quantity), StockMovementReason.VoidReturn, orderItemId, AverageCost);

    // lineCost: what this line of the delivery cost in all, when the invoice is entered. It moves
    // the average cost, weighted by quantity against what is on the shelf; stock below zero
    // counts as none, since nothing is left to average with.
    public StockMovement ReceivePurchase(decimal quantity, Guid purchaseId, decimal? lineCost = null)
    {
        if (IsPrepItem)
            throw new DomainException($"{Name} is made in the kitchen, not bought: make it with a production order.");
        Positive(quantity);
        if (lineCost is not { } cost)
            return Move(quantity, StockMovementReason.Purchase, purchaseId, AverageCost);
        if (cost < 0)
            throw new DomainException("A purchase cannot cost less than nothing.");

        var unitCost = cost / quantity;
        AverageWith(quantity, unitCost);
        return Move(quantity, StockMovementReason.Purchase, purchaseId, unitCost);
    }

    // Taken to make a prep item, at the average cost now: what it adds to the batch's cost.
    public StockMovement UseInProduction(decimal quantity, Guid productionOrderId) =>
        Move(-Positive(quantity), StockMovementReason.ProductionInput, productionOrderId, AverageCost);

    // A batch of this prep item made. batchCost is what its ingredients cost; it moves the
    // average exactly as a delivery's price moves a bought material's.
    public StockMovement ReceiveProduction(decimal quantity, Guid productionOrderId, decimal batchCost)
    {
        if (!IsPrepItem)
            throw new DomainException($"{Name} is bought, not made: it comes in on a purchase invoice.");
        Positive(quantity);
        if (batchCost < 0)
            throw new DomainException("A batch cannot cost less than nothing.");

        var unitCost = batchCost / quantity;
        AverageWith(quantity, unitCost);
        return Move(quantity, StockMovementReason.ProductionOutput, productionOrderId, unitCost);
    }

    // Stock thrown away before it reached a dish. Why is required: the variance report sets it
    // against the shortfall, and "expired" and "dropped" call for different fixes.
    public StockMovement RecordSpoilage(decimal quantity, Guid spoilageId, string reason, string recordedBy)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Say why it was thrown away.");
        if (reason.Trim().Length > 200)
            throw new DomainException("Keep the reason under 200 characters.");
        return Move(-Positive(quantity), StockMovementReason.Spoilage, spoilageId, AverageCost, reason.Trim(), recordedBy);
    }

    public StockMovement AdjustToCount(decimal countedQuantity, Guid stockCountId)
    {
        if (countedQuantity < 0)
            throw new DomainException("A counted quantity cannot be negative.");
        return Move(countedQuantity - QuantityOnHand, StockMovementReason.CountAdjustment, stockCountId, AverageCost);
    }

    // Weighted by quantity against what is on the shelf; stock below zero counts as none, since
    // nothing is left to average with.
    private void AverageWith(decimal quantity, decimal unitCost)
    {
        var onShelf = Math.Max(QuantityOnHand, 0);
        AverageCost = AverageCost is { } average && onShelf > 0
            ? (onShelf * average + quantity * unitCost) / (onShelf + quantity)
            : unitCost;
    }

    // Stock is allowed to go negative. Deduction runs after the sale has already happened, so
    // refusing it would not un-sell the dish; it would only lose the record. A negative
    // balance is the signal that a delivery was never booked in or a recipe quantity is wrong,
    // and a stock count puts it right.
    private StockMovement Move(
        decimal delta, StockMovementReason reason, Guid sourceId, decimal? unitCost, string? note = null, string? recordedBy = null)
    {
        QuantityOnHand += delta;
        return new StockMovement(Id, delta, reason, sourceId, unitCost, note, recordedBy);
    }

    private static decimal Positive(decimal quantity) =>
        quantity > 0 ? quantity : throw new DomainException("Quantity must be greater than zero.");
}
