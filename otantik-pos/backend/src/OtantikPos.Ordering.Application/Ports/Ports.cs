using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Catalog;
using OtantikPos.Ordering.Contracts;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Ordering.Application.Ports;

// What the Ordering use cases need from outside, implemented by Infrastructure (and by the
// API, for ITillNotifier and the shared kernel's ICurrentUser).

// The till's own copy of orders, in the restaurant machine's database.
public interface IOrderStore
{
    // With items and their add-ons loaded. Every TillOperations method reads them.
    Task<Order?> GetAsync(Guid orderPublicId, CancellationToken cancellationToken);

    // Orders still to be paid (OrderRules.AwaitsPayment), delivered ones included, oldest first.
    Task<IReadOnlyList<Order>> GetOpenAsync(CancellationToken cancellationToken);

    // Newest first, any state. How a cashier finds a paid order to refund.
    Task<IReadOnlyList<Order>> GetCreatedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken);

    // Created in [fromUtc, toUtc), newest first, any state: one business day, for its report.
    Task<IReadOnlyList<Order>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    void Add(Order order);
}

// The menu, as replicated down from the delivery system. The cloud owns it; the till only
// reads it.
public interface ICatalog
{
    // With Variants and MenuItemAddOns (with their AddOns) loaded, as OrderItemBuilder needs.
    // Null when the item does not exist or is deleted.
    Task<MenuItem?> GetMenuItemAsync(int menuItemId, CancellationToken cancellationToken);

    // Everything not deleted, items with the same includes as above, each list in display order.
    Task<TillMenu> GetMenuAsync(CancellationToken cancellationToken);
}

// The restaurant's business day, from the till's configuration (Restaurant:TimeZoneId and
// Restaurant:BusinessDayStartsAtHour): it runs from that hour, local time, to the same hour the
// next day, so an order at 01:30 belongs to the evening before.
public interface IBusinessCalendar
{
    DateOnly BusinessDateOf(DateTime utc);

    // The day's span in UTC: [FromUtc, ToUtc).
    (DateTime FromUtc, DateTime ToUtc) Bounds(DateOnly businessDate);
}

// The delivery system's pricing settings, replicated down. Read per use rather than cached
// here, so a change made in its admin reaches the till without a restart.
public interface IPricingSettings
{
    // RestaurantSettings.TaxPercentage.
    Task<decimal> GetTaxPercentageAsync(CancellationToken cancellationToken);

    // LoyaltySettings.RedemptionValuePer100Points, the redemption rate: L.E per 100 points,
    // LoyaltyRedemption.DefaultValuePer100Points (Points / 10) until the first sync.
    Task<decimal> GetRedemptionValuePer100PointsAsync(CancellationToken cancellationToken);
}

// The delivery system is the record for customers and their points. Both of these need the
// cloud and throw DeliverySystemUnavailableException when it cannot be reached.
public interface ICustomerDirectory
{
    // Null when no customer account has this number.
    Task<CustomerProfile?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);
}

public interface ILoyaltyGateway
{
    Task<int> GetBalanceAsync(string customerUserId, CancellationToken cancellationToken);

    // Spends points against an order. Idempotent by orderPublicId: a retry after a failure
    // spends nothing more. Throws ConflictException when the balance no longer covers it,
    // for instance because the customer spent it online in the meantime.
    Task RedeemAsync(string customerUserId, int points, Guid orderPublicId, CancellationToken cancellationToken);

    // Settles a Void After with the wallet: gives back `points` redeemed points, and takes back
    // what `refundedAmount` earned. Idempotent by refundId. Called from the outbox, so offline
    // it throws DeliverySystemUnavailableException and is simply tried again later.
    Task RefundAsync(string customerUserId, Guid orderPublicId, Guid refundId, int points, decimal refundedAmount, CancellationToken cancellationToken);
}

public sealed class DeliverySystemUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// Saved in the same unit of work as the order, so the trail and the change cannot disagree.
public interface IAuditLog
{
    void Add(OrderAuditEntry entry);
}

// Saved in the same unit of work as the order and delivered after it commits.
public interface IEventOutbox
{
    void Add(OrderingEvent orderingEvent);
}

// Pushes an order's new state to every till (SignalR). Called after the commit. Must not throw:
// the change has happened, and a till that missed the push reloads when it reconnects.
public interface ITillNotifier
{
    Task OrderChangedAsync(Order order, CancellationToken cancellationToken);
}

public enum KitchenTicketKind
{
    New,
    Void,
}

public sealed record KitchenTicketLine(string Name, int Quantity, IReadOnlyList<string> AddOns, string? Notes);

public sealed record KitchenTicket(Guid TicketId, string OrderLabel, KitchenTicketKind Kind, IReadOnlyList<KitchenTicketLine> Lines, string? Reason);

// Stores the ticket and returns; a worker prints it. Enqueueing the same TicketId twice must
// be a no-op, because the event that produces it can be delivered twice.
public interface IKitchenPrintQueue
{
    Task EnqueueAsync(KitchenTicket ticket, CancellationToken cancellationToken);
}

// The customer's final receipt. Each call prints a copy; a reprint is a second call.
public interface IReceiptPrintQueue
{
    Task EnqueueAsync(Order order, string printedByUserId, CancellationToken cancellationToken);
}

// The cash handed over for a bill paid in cash, for its receipt's "Cash received" and "Change".
// Kept by the till alone, saved with the payment: the order the delivery system gets is unchanged.
public interface ICashReceived
{
    void Record(Guid orderPublicId, decimal amount);
}
