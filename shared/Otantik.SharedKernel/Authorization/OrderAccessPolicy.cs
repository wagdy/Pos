namespace Otantik.SharedKernel.Authorization;

public enum OrderAction
{
    View,
    AddItems,
    SendToKitchen,
    ApplyLoyalty,
    Checkout,
    PrintFinalReceipt,
    VoidItem,
    VoidOrder,
}

public sealed record AccessDecision(bool IsAllowed, string? Reason)
{
    public static readonly AccessDecision Allowed = new(true, null);

    public static AccessDecision Denied(string reason) => new(false, reason);
}

// Who may do what to which order, right now. Both systems call this: the delivery API, where
// captains submit dine-in orders, and the POS API, where cashiers settle and void them. Both
// get the same answer.
//
// Two questions, in this order. Does the user's role grant the permission (RolePermissions)?
// Does the action make sense for this order in its current state? The second matters as much
// as the first: a cashier may void, but not an item already voided, and a captain may add a
// round, but only to their own open table.
//
// The Reason on a denial is written to be shown to the user as it is.
public static class OrderAccessPolicy
{
    public static AccessDecision CanCreate(ICurrentUser user, OrderType type)
    {
        if (!user.Has(Permissions.OrderCreate))
            return AccessDecision.Denied("You are not allowed to create orders.");

        // Captains take orders at the tables. A takeaway or delivery order needs the customer's
        // number and a cashier to look up their profile, so it starts at the till.
        if (user.Role == UserRole.CaptainOrder && type != OrderType.DineIn)
            return AccessDecision.Denied("Captains take dine-in orders only.");

        return AccessDecision.Allowed;
    }

    // item: required for VoidItem, ignored otherwise.
    public static AccessDecision Evaluate(ICurrentUser user, OrderAction action, Order order, OrderItem? item = null)
    {
        if (!CanSee(user, order))
            return AccessDecision.Denied("You can only work on orders you created.");

        return action switch
        {
            OrderAction.View => AccessDecision.Allowed,
            OrderAction.AddItems => WhileOpen(user, order, Permissions.OrderAddItems, "add items to"),
            OrderAction.SendToKitchen => WhileOpen(user, order, Permissions.OrderSendToKitchen, "send"),
            OrderAction.ApplyLoyalty => ApplyLoyalty(user, order),
            OrderAction.Checkout => Checkout(user, order),
            OrderAction.PrintFinalReceipt => PrintFinalReceipt(user, order),
            OrderAction.VoidItem => VoidItem(user, order, item ?? throw new ArgumentNullException(nameof(item))),
            OrderAction.VoidOrder => VoidOrder(user, order),
            _ => AccessDecision.Denied("Unknown action."),
        };
    }

    // A status change is how the delivery system cancels an order today, so it is checked
    // here too. Moving to Cancelled is a void and needs void rights. Moving to Served closes
    // a dine-in bill, which is a checkout. Anything else is the delivery or collection leg.
    public static AccessDecision CanChangeStatus(ICurrentUser user, Order order, OrderStatus newStatus) => newStatus switch
    {
        OrderStatus.Cancelled => Cancel(user, order),
        OrderStatus.Served => Evaluate(user, OrderAction.Checkout, order),
        _ when !CanSee(user, order) => AccessDecision.Denied("You can only work on orders you created."),
        _ when !user.Has(Permissions.OrderUpdateFulfilment) => AccessDecision.Denied("You are not allowed to change an order's status."),
        _ => AccessDecision.Allowed,
    };

    // Cancelling is a void, so it needs the right to void. Someone who has it is still not
    // offered a cancellation once the kitchen has the order: the till would not take it, and
    // would keep the order open while the delivery system called it cancelled.
    private static AccessDecision Cancel(ICurrentUser user, Order order)
    {
        var decision = Evaluate(user, OrderAction.VoidOrder, order);
        if (!decision.IsAllowed || order.Status == OrderStatus.Cancelled || !OrderRules.KitchenHasIt(order))
            return decision;
        return AccessDecision.Denied("The kitchen already has this order. Void it at the till, where what was made is written off.");
    }

    private static bool CanSee(ICurrentUser user, Order order) =>
        user.Has(Permissions.OrderViewAll)
        || (user.Has(Permissions.OrderViewOwn) && order.CreatedByUserId == user.UserId);

    private static AccessDecision WhileOpen(ICurrentUser user, Order order, string permission, string verb)
    {
        if (!user.Has(permission))
            return AccessDecision.Denied($"You are not allowed to {verb} orders.");
        if (!OrderRules.IsOpen(order))
            return AccessDecision.Denied($"This order is closed; you cannot {verb} it.");
        return AccessDecision.Allowed;
    }

    // Points are part of paying, so they can be applied for as long as the bill is unpaid,
    // even to an order already delivered (OrderRules.AwaitsPayment).
    private static AccessDecision ApplyLoyalty(ICurrentUser user, Order order)
    {
        if (!user.Has(Permissions.OrderApplyLoyalty))
            return AccessDecision.Denied("You are not allowed to apply points to orders.");
        if (!OrderRules.AwaitsPayment(order))
            return AccessDecision.Denied("This order is already paid or cancelled.");

        // Points belong to a customer account in the delivery system. A phone number alone is
        // not enough: the profile has to have been found and attached.
        return order.UserId is null
            ? AccessDecision.Denied("Look the customer up by mobile number before applying points.")
            : AccessDecision.Allowed;
    }

    private static AccessDecision Checkout(ICurrentUser user, Order order)
    {
        if (!user.Has(Permissions.OrderCheckout))
            return AccessDecision.Denied("You are not allowed to take payments.");
        if (!OrderRules.AwaitsPayment(order))
            return AccessDecision.Denied("This order is already paid or cancelled.");
        if (!order.OrderItems.Any(i => !i.IsVoided))
            return AccessDecision.Denied("There is nothing on this order to pay for.");
        if (OrderRules.RequiresCustomerPhone(order.Type) && string.IsNullOrWhiteSpace(order.CustomerPhone))
            return AccessDecision.Denied("Enter the customer's mobile number first.");
        return AccessDecision.Allowed;
    }

    private static AccessDecision PrintFinalReceipt(ICurrentUser user, Order order)
    {
        if (!user.Has(Permissions.ReceiptPrintFinal))
            return AccessDecision.Denied("You are not allowed to print final receipts.");

        // A final receipt says the bill is paid; before that it would be a lie.
        return OrderRules.IsSettled(order)
            ? AccessDecision.Allowed
            : AccessDecision.Denied("The order has not been paid yet.");
    }

    private static AccessDecision VoidItem(ICurrentUser user, Order order, OrderItem item)
    {
        if (OrderRules.VoidTypeFor(order, item) is not { } type)
            return AccessDecision.Denied("This item can no longer be voided.");

        return user.Has(Permissions.ForVoid(type))
            ? AccessDecision.Allowed
            : AccessDecision.Denied(VoidDeniedMessage(type));
    }

    // Every item still standing is voided at once, so the user needs the right for each kind
    // of void that will happen.
    private static AccessDecision VoidOrder(ICurrentUser user, Order order)
    {
        var types = order.OrderItems
            .Select(i => OrderRules.VoidTypeFor(order, i))
            .OfType<VoidType>()
            .Distinct()
            .ToList();

        if (types.Count == 0 && (order.Status == OrderStatus.Cancelled || order.PaymentStatus == PaymentStatus.Refunded))
            return AccessDecision.Denied("This order has already been voided.");

        // An order with no items left can still be cancelled. It costs nothing, so it needs
        // only the most basic void right.
        if (types.Count == 0)
            types.Add(VoidType.BeforeKitchen);

        foreach (var type in types)
        {
            if (!user.Has(Permissions.ForVoid(type)))
                return AccessDecision.Denied(VoidDeniedMessage(type));
        }

        return AccessDecision.Allowed;
    }

    private static string VoidDeniedMessage(VoidType type) => type switch
    {
        VoidType.BeforeKitchen => "You are not allowed to void items.",
        VoidType.AfterKitchen => "You are not allowed to void items that have gone to the kitchen.",
        _ => "You are not allowed to refund paid orders.",
    };
}
