using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Contracts;
using DomainVoidedLine = OtantikPos.Ordering.Domain.VoidedLine;
using EventVoidedLine = OtantikPos.Ordering.Contracts.VoidedLine;

namespace OtantikPos.Ordering.Application.Orders;

// Turns the shared order into the contract's events: ids, catalog numbers and quantities, and
// nothing about the order itself.
internal static class OrderEvents
{
    public static ItemsSentToKitchen Sent(Order order, IEnumerable<OrderItem> items) => new(
        order.PublicId,
        items.Select(i => new KitchenLine(i.PublicId, NameOf(i), i.Quantity, i.AddOns.Select(a => a.Name).ToList(), i.Notes)).ToList());

    // Everything still on the bill when it was paid. Inventory deducts these.
    public static OrderSettled Settled(Order order) => new(
        order.PublicId,
        order.OrderItems
            .Where(i => !i.IsVoided)
            .Select(i => new SoldLine(i.PublicId, i.MenuItemId, i.VariantId, i.AddOns.Select(a => a.AddOnId).ToList(), i.Quantity))
            .ToList());

    public static OrderItemsVoided Voided(Order order, string? reason, IEnumerable<DomainVoidedLine> lines) => new(
        order.PublicId,
        reason,
        lines.Select(l => new EventVoidedLine(
            l.Item.PublicId,
            l.Item.MenuItemId,
            l.Item.VariantId,
            l.Item.AddOns.Select(a => a.AddOnId).ToList(),
            l.Item.Quantity,
            NameOf(l.Item),
            StageOf(l.Type),
            l.RefundAmount)).ToList());

    // Null when there is nothing for the wallet: no customer account, or no Void After.
    public static LoyaltyRefundDue? LoyaltyRefund(Order order, IReadOnlyCollection<DomainVoidedLine> lines)
    {
        var refunded = lines.Where(l => l.Type == VoidType.AfterPayment).ToList();
        if (order.UserId is null || refunded.Count == 0)
            return null;

        return new LoyaltyRefundDue(order.PublicId, order.UserId, refunded.Sum(l => l.PointsReturned), refunded.Sum(l => l.RefundAmount));
    }

    public static string NameOf(OrderItem item) =>
        item.VariantName is null ? item.MenuItemName : $"{item.MenuItemName} ({item.VariantName})";

    private static VoidStage StageOf(VoidType type) => type switch
    {
        VoidType.BeforeKitchen => VoidStage.BeforeKitchen,
        VoidType.AfterKitchen => VoidStage.AfterKitchen,
        VoidType.AfterPayment => VoidStage.AfterPayment,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
