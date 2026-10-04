namespace Otantik.SharedKernel.Orders;

public static class OrderStatuses
{
    // "The customer has their food." True for a delivered delivery and a collected
    // pickup alike.
    //
    // This exists because three separate places used to compare directly to Delivered -
    // the loyalty award, the review gate, and the can-this-order-still-be-edited guard -
    // and every one of them would have silently excluded pickup orders when the
    // collection statuses were added. Points never awarded, reviews refused, a finished
    // order still editable. Ask this instead of naming a status.
    //
    // Shared kernel: Served (dine-in) added, for exactly the reason above.
    public static bool IsFulfilled(OrderStatus status) =>
        status is OrderStatus.Delivered or OrderStatus.Collected or OrderStatus.Served;

    // Terminal: nothing further happens to the order, so it can no longer be edited.
    public static bool IsFinal(OrderStatus status) =>
        IsFulfilled(status) || status is OrderStatus.Cancelled;

    // The statuses that only make sense for one fulfilment mode. Used to keep the admin's
    // own status list honest rather than to block a write - there is no transition
    // validation anywhere in this service, and adding it for these two alone would be a
    // rule that applies to a quarter of the enum and nothing else.
    public static bool IsCollectionStatus(OrderStatus status) =>
        status is OrderStatus.ReadyForCollection or OrderStatus.Collected;

    public static bool IsDeliveryStatus(OrderStatus status) =>
        status is OrderStatus.OutForDelivery or OrderStatus.Delivered;

    // How far along an order is, for the rule both systems apply when the other side's copy
    // of an order arrives: the status only moves forward, so a message that left before a
    // later change cannot undo it. The legs (delivery, collection, dine-in) share ranks, as an
    // order only ever walks one of them. A cancellation ranks with the fulfilled states: it is
    // as final as they are.
    public static int Progress(OrderStatus status) => status switch
    {
        OrderStatus.Pending => 0,
        OrderStatus.Preparing => 1,
        OrderStatus.OutForDelivery or OrderStatus.ReadyForCollection => 2,
        OrderStatus.Delivered or OrderStatus.Collected or OrderStatus.Served or OrderStatus.Cancelled => 3,
        _ => -1,
    };
}
