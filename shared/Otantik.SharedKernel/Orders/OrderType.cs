namespace Otantik.SharedKernel.Orders;

// How the order reaches the customer. Replaces the delivery system's IsPickup flag, which could
// only say delivery or takeaway (see Order.IsPickup).
//
// Delivery is first, and so the default, because every order the delivery system holds today
// is either a delivery or a pickup. Append only: like the other shared enums, this is sent as
// a number in SignalR messages.
public enum OrderType
{
    Delivery,
    Takeaway,
    DineIn,
}
