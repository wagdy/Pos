namespace Otantik.SharedKernel.Orders;

public enum OrderStatus
{
    Pending,
    Preparing,

    // Delivery leg.
    OutForDelivery,
    Delivered,

    // Collection leg, mirroring the two above for orders the customer picks up (see
    // Order.IsPickup). A pickup order that moved through OutForDelivery/Delivered told
    // the customer their food was "out for delivery" and then "delivered", neither of
    // which was true - these two say what actually happened instead.
    //
    // Both are also FULFILLED states, exactly like Delivered: they award loyalty points
    // and open up reviewing. See OrderStatuses.IsFulfilled, which is what every caller
    // should ask rather than comparing to Delivered directly.
    ReadyForCollection,
    Collected,

    Cancelled,

    // Shared kernel, appended. Never insert above this line: anything that sends enums as
    // numbers (SignalR's default, an older build) would renumber every value after it for
    // clients that have not been updated.

    // The dine-in leg's fulfilled state, the counterpart of Delivered and Collected: the
    // table has eaten and the bill is closed. A fulfilled state like those two, so it awards
    // loyalty points and opens reviewing (see OrderStatuses.IsFulfilled).
    Served
}
