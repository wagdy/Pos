namespace Otantik.SharedKernel.Orders;

// Which kind of void an item went through. Decided by OrderRules.VoidTypeFor from the order's
// state and never taken from the person voiding, so a printed item cannot be voided as
// "before kitchen" to keep it off the waste report.
public enum VoidType
{
    // "Void Before": never printed in the kitchen. Comes off the bill; nothing is wasted.
    BeforeKitchen,

    // Printed in the kitchen but not yet paid. Comes off the bill, and its ingredients are
    // recorded as waste because the kitchen has likely started on it. Without this, a printed
    // item could only be removed by taking payment first and then refunding it.
    AfterKitchen,

    // "Void After": already paid. The item's share of the payment is refunded and its
    // ingredients go back into stock.
    AfterPayment,
}
