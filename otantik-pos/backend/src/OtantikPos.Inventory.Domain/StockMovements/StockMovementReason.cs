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
}
