using MediatR;

namespace OtantikPos.Ordering.Contracts;

// Saved in the same transaction as the order change that raised it, then delivered after the
// commit (the outbox in Infrastructure). At least once: a handler can see the same EventId
// twice and must cope.
public abstract record OrderingEvent : INotification
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

// Every change the till makes to an order. Infrastructure pushes the order to the delivery
// system on this, which is how the cloud gets the till's voids, payments and refunds, and how
// the order lands in the customer's history.
public sealed record OrderChanged(Guid OrderPublicId) : OrderingEvent;

// Items to print on a kitchen ticket. The till's own sends, and items a captain submitted in
// the delivery app.
public sealed record ItemsSentToKitchen(Guid OrderPublicId, IReadOnlyList<KitchenLine> Lines) : OrderingEvent;

public sealed record KitchenLine(Guid OrderItemPublicId, string Name, int Quantity, IReadOnlyList<string> AddOns, string? Notes);

// The order is paid: Inventory deducts what was sold.
public sealed record OrderSettled(Guid OrderPublicId, IReadOnlyList<SoldLine> Lines) : OrderingEvent;

// One order line as Inventory needs it: catalog ids and a quantity.
public sealed record SoldLine(Guid OrderItemPublicId, int MenuItemId, int? VariantId, IReadOnlyList<int> AddOnIds, int Quantity);

// A Void After on an order with a customer account. The delivery system holds the customer's
// wallet, so it settles the points: it gives back PointsToReturn, the redeemed points the
// refunded items carried, and takes back what RefundedAmount had earned, by its own earning
// rate. EventId is the refund's id, and the delivery system applies each id once, however
// often the outbox delivers it.
public sealed record LoyaltyRefundDue(Guid OrderPublicId, string CustomerUserId, int PointsToReturn, decimal RefundedAmount) : OrderingEvent;

public sealed record OrderItemsVoided(Guid OrderPublicId, string? Reason, IReadOnlyList<VoidedLine> Lines) : OrderingEvent;

public sealed record VoidedLine(
    Guid OrderItemPublicId,
    int MenuItemId,
    int? VariantId,
    IReadOnlyList<int> AddOnIds,
    int Quantity,
    string Name,
    VoidStage Stage,
    decimal RefundAmount);

// The contract's own copy of the shared VoidType, so a consumer needs nothing from the order
// model. It says how far the item had got; each consumer decides what that means for it.
public enum VoidStage
{
    BeforeKitchen,
    AfterKitchen,
    AfterPayment,
}
