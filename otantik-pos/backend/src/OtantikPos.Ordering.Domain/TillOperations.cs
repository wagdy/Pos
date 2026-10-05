using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Customers;
using Otantik.SharedKernel.Orders;

namespace OtantikPos.Ordering.Domain;

public sealed record OpenOrderDetails(
    OrderType Type,
    string? TableNumber,
    string? CustomerName,
    string? CustomerPhone,
    string? DeliveryAddress,
    decimal DeliveryFee,
    string? Notes);

// The customer as the delivery system knows them, found by mobile number.
public sealed record CustomerProfile(string UserId, string FullName, string PhoneNumber, int PointsBalance);

// PointsReturned: the item's share of the points redeemed on the order, going back to the
// customer's wallet. Only a Void After returns any; before payment none were spent yet.
public sealed record VoidedLine(OrderItem Item, VoidType Type, decimal RefundAmount, int PointsReturned = 0);

// Everything a till does to an order. The shared Order is the delivery system's model as it
// is, with public setters and no behaviour, so that both systems can use it. The POS changes
// an order only through these methods, which keep its totals, statuses and voids consistent.
//
// Nothing here checks who is asking. OrderAccessPolicy decides that, in the Application
// layer, before any of these run.
public static class TillOperations
{
    public static Order Open(OpenOrderDetails details, string createdByUserId, DateTime now)
    {
        var phone = Clean(details.CustomerPhone);

        if (details.Type == OrderType.DineIn && TableNumbers.Normalize(details.TableNumber) is null)
            throw new DomainException("Enter the table number.");
        if (OrderRules.RequiresCustomerPhone(details.Type) && phone is null)
            throw new DomainException("Enter the customer's mobile number.");
        if (details.Type == OrderType.Delivery && Clean(details.DeliveryAddress) is null)
            throw new DomainException("Enter the delivery address.");
        if (details.DeliveryFee < 0)
            throw new DomainException("The delivery fee cannot be negative.");

        return new Order
        {
            Type = details.Type,
            TableNumber = details.Type == OrderType.DineIn ? TableNumbers.Normalize(details.TableNumber) : null,
            CustomerName = Clean(details.CustomerName) ?? string.Empty,
            CustomerPhone = phone ?? string.Empty,
            // Required by the delivery database, so never null; empty unless it is a delivery.
            DeliveryAddress = details.Type == OrderType.Delivery ? Clean(details.DeliveryAddress)! : string.Empty,
            DeliveryFee = details.Type == OrderType.Delivery ? details.DeliveryFee : 0,
            Notes = Clean(details.Notes),
            Status = OrderStatus.Pending,
            // The C# default is Confirmed, which the delivery system relies on for cash on
            // delivery. A bill at the till is unpaid until checkout.
            PaymentStatus = PaymentStatus.Pending,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // profile null: the number is not registered (or the delivery system could not be reached).
    // The order keeps the number and name as a walk-in, and earns no points.
    public static void AttachCustomer(Order order, CustomerProfile? profile, string phoneNumber, string? name, decimal taxPercentage, DateTime now)
    {
        RequireOpen(order);
        var phone = Clean(phoneNumber) ?? throw new DomainException("Enter the customer's mobile number.");

        // Points belong to the account they were taken from. A different customer, or none,
        // means the redemption no longer applies.
        if (order.PointsRedeemed > 0 && order.UserId != profile?.UserId)
        {
            order.PointsRedeemed = 0;
            order.PointsDiscountAmount = 0;
        }

        order.UserId = profile?.UserId;
        order.CustomerPhone = profile?.PhoneNumber ?? phone;
        order.CustomerName = profile?.FullName ?? Clean(name) ?? order.CustomerName;
        Touch(order, taxPercentage, now);
    }

    public static OrderItem AddItem(
        Order order, MenuItem menuItem, int? variantId, IReadOnlyCollection<int> addOnIds,
        int quantity, string? notes, decimal taxPercentage, DateTime now)
    {
        RequireOpen(order);

        // The delivery system's own limit on a line (OrderItemRequest).
        if (quantity is < 1 or > 100)
            throw new DomainException("Quantity must be between 1 and 100.");

        var built = OrderItemBuilder.Build(menuItem, variantId, addOnIds, quantity);
        var item = built.Item ?? throw new DomainException(built.Error!);

        item.Notes = Clean(notes);
        item.Order = order;
        order.OrderItems.Add(item);

        Touch(order, taxPercentage, now);
        return item;
    }

    // Marks everything not yet printed as sent and returns it, for the kitchen ticket.
    public static IReadOnlyList<OrderItem> SendToKitchen(Order order, DateTime now)
    {
        var pending = order.OrderItems.Where(i => !i.IsVoided && !i.IsSentToKitchen).ToList();
        if (pending.Count == 0)
            return pending;

        foreach (var item in pending)
            item.SentToKitchenAt = now;

        // The delivery system's own meaning of Preparing: the kitchen is working on it.
        if (order.Status == OrderStatus.Pending)
            order.Status = OrderStatus.Preparing;

        order.UpdatedAt = now;
        return pending;
    }

    // Applies points to the bill. The balance is checked here so the cashier hears about a
    // shortfall now. The points are actually spent in the delivery system at checkout.
    // valuePer100Points is the redemption rate, the delivery system's setting (Points / 10 at
    // its default); see LoyaltyRedemption.
    public static void ApplyPoints(Order order, int points, int balance, decimal valuePer100Points, decimal taxPercentage, DateTime now)
    {
        RequireUnpaid(order);
        if (order.UserId is null)
            throw new DomainException("Look the customer up by mobile number before applying points.");
        if (valuePer100Points <= 0)
            throw new DomainException("Points cannot be redeemed at the moment.");
        if (points < 1)
            throw new DomainException("Points to redeem must be at least 1.");
        if (points > balance)
            throw new DomainException($"The customer has only {balance} points.");

        // Priced first, so the cap is measured against this bill as it stands.
        OrderPricing.Reprice(order, taxPercentage);
        var max = LoyaltyRedemption.MaxPointsFor(OrderPricing.TotalBeforePoints(order), valuePer100Points);
        if (points > max)
            throw new DomainException($"This bill can take at most {max} points.");

        order.PointsRedeemed = points;
        order.PointsDiscountAmount = LoyaltyRedemption.DiscountFor(points, valuePer100Points);
        Touch(order, taxPercentage, now);
    }

    public static void RemovePoints(Order order, decimal taxPercentage, DateTime now)
    {
        RequireUnpaid(order);
        order.PointsRedeemed = 0;
        order.PointsDiscountAmount = 0;
        Touch(order, taxPercentage, now);
    }

    // Takes payment and closes the bill. Returns anything sent to the kitchen in the process: a
    // counter that takes payment first must still cook what was paid for.
    //
    // Dine-in becomes Served: the table has finished. A takeaway or delivery stays where it is
    // on its fulfilment leg (ReadyForCollection, OutForDelivery and the rest), because being
    // paid is not the same as being handed over.
    public static IReadOnlyList<OrderItem> Close(Order order, PaymentMethod method, string closedByUserId, decimal taxPercentage, DateTime now)
    {
        RequireUnpaid(order);
        if (!order.OrderItems.Any(i => !i.IsVoided))
            throw new DomainException("Add at least one item before taking payment.");
        if (OrderRules.RequiresCustomerPhone(order.Type) && string.IsNullOrWhiteSpace(order.CustomerPhone))
            throw new DomainException("Enter the customer's mobile number first.");

        OrderPricing.Reprice(order, taxPercentage);

        // Possible when items were voided after the points were applied.
        if (order.TotalAmount < 0)
            throw new DomainException("The points discount is now more than the bill. Reduce it before taking payment.");

        var sentNow = SendToKitchen(order, now);

        order.PaymentMethod = method;
        order.PaymentStatus = PaymentStatus.Confirmed;
        order.ClosedAt = now;
        order.ClosedByUserId = closedByUserId;
        if (order.Type == OrderType.DineIn)
            order.Status = OrderStatus.Served;

        order.UpdatedAt = now;
        return sentNow;
    }

    // Voids `quantity` of one item. The kind of void comes from OrderRules, never the caller.
    public static VoidedLine VoidItem(Order order, OrderItem item, int quantity, string? reason, string voidedByUserId, decimal taxPercentage, DateTime now)
    {
        var type = OrderRules.VoidTypeFor(order, item)
            ?? throw new DomainException("This item can no longer be voided.");
        if (quantity < 1 || quantity > item.Quantity)
            throw new DomainException($"Quantity to void must be between 1 and {item.Quantity}.");

        var cleanReason = ReasonFor(type, reason);
        var target = quantity == item.Quantity ? item : SplitOff(order, item, quantity);
        var line = Void(order, target, type, cleanReason, voidedByUserId, now);

        AfterVoids(order, taxPercentage, now);
        return line;
    }

    // Voids everything still standing. Before payment the order is Cancelled, and any points
    // come off (they were never spent: that happens at checkout). After payment it is refunded
    // in full.
    public static IReadOnlyList<VoidedLine> VoidOrder(Order order, string? reason, string voidedByUserId, decimal taxPercentage, DateTime now)
    {
        if (order.Status == OrderStatus.Cancelled || order.PaymentStatus == PaymentStatus.Refunded)
            throw new DomainException("This order has already been voided.");

        var live = order.OrderItems
            .Where(i => !i.IsVoided)
            .Select(i => (Item: i, Type: OrderRules.VoidTypeFor(order, i)!.Value))
            .ToList();

        var worst = live.Any(l => l.Type != VoidType.BeforeKitchen) ? VoidType.AfterKitchen : VoidType.BeforeKitchen;
        var cleanReason = ReasonFor(worst, reason);
        var settled = OrderRules.IsSettled(order);

        // One at a time, in order: each refund share depends on the ones before it.
        var lines = live.Select(l => Void(order, l.Item, l.Type, cleanReason, voidedByUserId, now)).ToList();

        if (settled)
        {
            order.PaymentStatus = PaymentStatus.Refunded;
        }
        else
        {
            order.Status = OrderStatus.Cancelled;
            order.PointsRedeemed = 0;
            order.PointsDiscountAmount = 0;
            OrderPricing.Reprice(order, taxPercentage);
        }

        order.UpdatedAt = now;
        return lines;
    }

    private static VoidedLine Void(Order order, OrderItem item, VoidType type, string? reason, string voidedByUserId, DateTime now)
    {
        var refund = type == VoidType.AfterPayment ? RefundFor(order, item) : 0m;
        var points = type == VoidType.AfterPayment ? PointsFor(order, item) : 0;

        item.VoidType = type;
        item.VoidReason = reason;
        item.VoidedByUserId = voidedByUserId;
        item.VoidedAt = now;
        item.RefundAmount = refund;
        order.RefundedAmount += refund;
        order.PointsRefunded += points;

        return new VoidedLine(item, type, refund, points);
    }

    // An item's refund is its share of what was actually charged, not its menu price, so on a
    // bill partly paid with points, refunding everything gives back exactly the money taken.
    // The last item standing gets whatever is left, which absorbs the rounding of the shares
    // before it.
    private static decimal RefundFor(Order order, OrderItem item)
    {
        var remaining = order.TotalAmount - order.RefundedAmount;
        if (order.OrderItems.All(i => ReferenceEquals(i, item) || i.IsVoided))
            return remaining;

        var billed = order.BilledItemsSubtotal;
        if (billed == 0)
            return 0;

        var share = Math.Round(item.LineTotal * order.TotalAmount / billed, 2, MidpointRounding.AwayFromZero);
        return Math.Min(share, remaining);
    }

    // The item's share of the redeemed points, the same split as RefundFor makes of the money:
    // points paid for part of every item, so a refunded item hands back its part of them.
    // Floored to whole points, and the last item standing gets whatever is left, so the shares
    // always add up to exactly what was redeemed.
    private static int PointsFor(Order order, OrderItem item)
    {
        var remaining = order.PointsRedeemed - order.PointsRefunded;
        if (remaining <= 0)
            return 0;
        if (order.OrderItems.All(i => ReferenceEquals(i, item) || i.IsVoided))
            return remaining;

        var billed = order.BilledItemsSubtotal;
        if (billed == 0)
            return 0;

        return Math.Min((int)Math.Floor(item.LineTotal * order.PointsRedeemed / billed), remaining);
    }

    private static void AfterVoids(Order order, decimal taxPercentage, DateTime now)
    {
        // Paid: the total charged stands, and the payment status says how much came back.
        // Unpaid: the bill simply shrinks.
        if (OrderRules.IsSettled(order))
            order.PaymentStatus = order.OrderItems.All(i => i.IsVoided) ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        else
            OrderPricing.Reprice(order, taxPercentage);

        order.UpdatedAt = now;
    }

    // Moves `quantity` of a line onto a new line with the same price, add-ons and kitchen
    // state, so voiding one burger out of three leaves the other two untouched. The new line
    // gets its own PublicId: it is a different item from here on.
    private static OrderItem SplitOff(Order order, OrderItem item, int quantity)
    {
        item.Quantity -= quantity;

        var split = new OrderItem
        {
            Order = order,
            MenuItemId = item.MenuItemId,
            MenuItemName = item.MenuItemName,
            VariantId = item.VariantId,
            VariantName = item.VariantName,
            UnitPrice = item.UnitPrice,
            Quantity = quantity,
            Notes = item.Notes,
            SentToKitchenAt = item.SentToKitchenAt,
            AddOns = item.AddOns.Select(a => new OrderItemAddOn { AddOnId = a.AddOnId, Name = a.Name, Price = a.Price }).ToList(),
        };

        order.OrderItems.Add(split);
        return split;
    }

    // A Void Before costs nothing and needs no explanation. Anything later wastes food or
    // hands money back, so it has to say why.
    private static string? ReasonFor(VoidType type, string? reason)
    {
        var clean = Clean(reason);
        if (type != VoidType.BeforeKitchen && clean is null)
            throw new DomainException("A reason is required to void an item that has been sent to the kitchen or paid for.");
        return clean;
    }

    private static void RequireOpen(Order order)
    {
        if (!OrderRules.IsOpen(order))
            throw new DomainException("This order is closed and can no longer be changed.");
    }

    // Paying and points, which stay possible after the food is handed over (cash on
    // delivery); see OrderRules.AwaitsPayment.
    private static void RequireUnpaid(Order order)
    {
        if (!OrderRules.AwaitsPayment(order))
            throw new DomainException("This order is already paid or cancelled.");
    }

    private static void Touch(Order order, decimal taxPercentage, DateTime now)
    {
        OrderPricing.Reprice(order, taxPercentage);
        order.UpdatedAt = now;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
