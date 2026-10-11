namespace OtantikPos.Inventory.Domain.StockMovements;

public enum StockMovementReason
{
    // An order was paid.
    Sale = 1,

    // An item was voided after it reached the kitchen but before payment.
    Waste = 2,

    // An item was voided after payment, and its ingredients go back on the shelf.
    VoidReturn = 3,

    Purchase = 4,

    CountAdjustment = 5,

    // Raw stock thrown away, recorded by hand: expired, spoiled, dropped, damaged packaging.
    Spoilage = 6,

    // Taken from stock to make a prep item: the flour that went into the dough. A transfer
    // between stock items, not a cost of sales.
    ProductionInput = 7,

    // A prep item made: the dough, into stock at what its ingredients cost.
    ProductionOutput = 8,
}
