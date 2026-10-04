using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;

namespace Otantik.Domain.Tests;

internal sealed record TestUser(string UserId, UserRole Role) : ICurrentUser;

// Builds orders in the states the rules care about.
internal static class Orders
{
    public static Order DineIn(string createdBy, params OrderItem[] items)
    {
        var order = new Order
        {
            Type = OrderType.DineIn,
            TableNumber = "T4",
            CreatedByUserId = createdBy,
            Status = OrderStatus.Pending,
            PaymentStatus = PaymentStatus.Pending,
        };
        foreach (var item in items)
        {
            item.Order = order;
            order.OrderItems.Add(item);
        }
        return order;
    }

    public static OrderItem Item(decimal price, int quantity = 1, bool sent = false) => new()
    {
        MenuItemId = 1,
        MenuItemName = "Burger",
        UnitPrice = price,
        Quantity = quantity,
        SentToKitchenAt = sent ? DateTime.UtcNow : null,
    };

    public static Order Closed(this Order order, string closedBy)
    {
        order.ClosedAt = DateTime.UtcNow;
        order.ClosedByUserId = closedBy;
        order.PaymentStatus = PaymentStatus.Confirmed;
        order.Status = OrderStatus.Served;
        return order;
    }
}
