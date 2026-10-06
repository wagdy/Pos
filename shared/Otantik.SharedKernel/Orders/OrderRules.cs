namespace Otantik.SharedKernel.Orders;

// Rules both systems must apply identically. Pure functions over the shared model: the cloud
// and the restaurant machine call these rather than each keeping its own copy that could drift.
public static class OrderRules
{
    // Whether the customer has paid. True for an order closed at the till, or for a delivery-
    // system order whose payment is confirmed and was either taken up front (Visa, Instapay)
    // or collected on fulfilment (cash on delivery). A refunded order counts too: it was paid,
    // then reversed.
    //
    // PaymentStatus alone cannot answer this. The delivery system marks a cash-on-delivery
    // order Confirmed the moment it is placed, long before anyone has handed over cash.
    public static bool IsSettled(Order order) =>
        order.ClosedAt is not null
        || (order.PaymentStatus is PaymentStatus.Confirmed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded
            && (order.PaymentMethod != PaymentMethod.Cash || OrderStatuses.IsFulfilled(order.Status)));

    // What voiding this item now would be. Null when it cannot be voided: it already is, or
    // the whole order was cancelled or refunded.
    public static VoidType? VoidTypeFor(Order order, OrderItem item)
    {
        if (item.IsVoided || order.Status == OrderStatus.Cancelled || order.PaymentStatus == PaymentStatus.Refunded)
            return null;

        if (IsSettled(order))
            return VoidType.AfterPayment;

        return item.IsSentToKitchen ? VoidType.AfterKitchen : VoidType.BeforeKitchen;
    }

    // Whether the till must ask for the customer's mobile number before taking the order.
    // Takeaway and delivery orders identify the customer by it: that is how the POS finds
    // their profile in the delivery system, applies their points, and puts the order in their
    // history. A dine-in table may stay anonymous.
    public static bool RequiresCustomerPhone(OrderType type) =>
        type is OrderType.Takeaway or OrderType.Delivery;

    // Whether the order can still change: items added, sent, voided before payment. False
    // once it is paid, cancelled or fulfilled.
    public static bool IsOpen(Order order) =>
        !IsSettled(order) && !OrderStatuses.IsFinal(order.Status);

    // Whether the bill is still to be paid: not paid, and not cancelled. Unlike IsOpen, true for
    // an order already handed over. Cash on delivery is the everyday case: the driver hands the
    // food over, often marking it Delivered from the delivery app, and brings the money back to
    // the till, which takes payment then. Taking payment and applying points ask this; adding
    // items and sending them to the kitchen ask IsOpen.
    public static bool AwaitsPayment(Order order) =>
        !IsSettled(order) && order.Status != OrderStatus.Cancelled;

    // Something still on the bill has reached the kitchen. A cancellation from the delivery
    // system then waits for the till: the food is being made, and the till records it as waste
    // when it voids it. The till does not take such a cancellation (CloudOrderMerge), so the
    // delivery system does not accept one either (OrderAccessPolicy.CanChangeStatus); otherwise
    // it called cancelled an order the kitchen was still making.
    public static bool KitchenHasIt(Order order) =>
        order.OrderItems.Any(i => i.IsSentToKitchen && !i.IsVoided);
}
