using System.Text.Json.Serialization;

namespace Otantik.SharedKernel.Orders;

// The delivery system's Order, unchanged down to the "Added by the shared kernel" section at
// the bottom, so its database columns and JSON property names stay exactly as they are.
//
// The [JsonIgnore] attributes are new. They make this class itself the payload that moves
// between the cloud and the restaurant machine: an order with its items and add-ons, without
// the user account or menu item graphs hanging off it.
public class Order
{
    // This database's own number for the order. The cloud and the restaurant machine each
    // number their rows independently, so this is not the order's identity across systems;
    // PublicId is.
    public int Id { get; set; }

    // Null when placed as a guest checkout.
    public string? UserId { get; set; }

    // Never serialized: an AppUser carries the password hash and security stamp.
    [JsonIgnore]
    public AppUser? User { get; set; }

    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string DeliveryAddress { get; set; } = string.Empty;

    // Grand total actually charged: (item subtotal - DiscountAmount) + TaxAmount + DeliveryFee.
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string? Notes { get; set; }

    // Snapshot of the promo code applied at checkout (if any) - a plain string, not a
    // foreign key, so deleting/editing that PromoCode later never invalidates this
    // order's own historical record of what was actually applied.
    public string? PromoCodeText { get; set; }
    public decimal DiscountAmount { get; set; }

    // Loyalty points spent on this order and what they were worth. Both snapshotted:
    // RedemptionValuePer100Points is an admin setting that can change, and a receipt has
    // to keep saying what the customer actually got at the time.
    //
    // PointsRedeemed is the authoritative record of the spend - the ledger row in
    // LoyaltyPointTransactions carries the same number, and the two are written in one
    // transaction so they cannot disagree.
    public int PointsRedeemed { get; set; }
    public decimal PointsDiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }

    // Snapshotted so TotalAmount's breakdown always adds up on a receipt. The live rate
    // comes from RestaurantSettings.BaseDeliveryFee for a customer checkout, or from the
    // request for a staff-created one (see OrderService.CreateAsync). Always 0 when
    // IsPickup - there is nothing to deliver.
    public decimal DeliveryFee { get; set; }

    // True when the customer chose "Store Pickup" instead of delivery. Without this the
    // kitchen, the captain dispatch list and the receipt would all read a pickup order as
    // an ordinary delivery and send a driver to an address the customer never gave.
    // DeliveryAddress still carries text for these orders - the branch address and the
    // chosen pickup time - so staff can read the detail without a second column.
    //
    // Shared kernel: now read-only, derived from Type, which replaces it as the stored value.
    // A pickup is a Takeaway. Every existing reader keeps working, and the compiler finds the
    // one place that wrote it. Database queries cannot use it, though: EF Core cannot
    // translate a computed property, so a query that filtered or grouped on IsPickup must say
    // Type == OrderType.Takeaway instead. Each system's EF configuration Ignore()s it.
    [JsonIgnore]
    public bool IsPickup => Type == OrderType.Takeaway;

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    // Only Visa starts Pending (see PaymentStatus's own doc comment) - there's no
    // dedicated "mark as paid" admin action yet, since one wasn't requested; an admin
    // can still see this value on the order to know a Visa payment needs confirming.
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Confirmed;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Deliberately independent of Status (not a new OrderStatus value like
    // "Acknowledged") - this tracks whether a cashier has dismissed the live new-order
    // alarm for this order, which is orthogonal to its fulfillment stage. An order can
    // be Pending and acknowledged, or (in principle) Preparing and never acknowledged
    // (e.g. it arrived via a channel that doesn't push the alarm at all, like a bulk
    // Excel import - see BulkOrderImportService). Mirrors the same reasoning that kept
    // PaymentStatus out of the OrderStatus enum.
    public bool IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    // Set the first time this order's Delivered-transition finishes awarding loyalty
    // points/punches (see LoyaltyService.ProcessOrderDeliveredAsync) - checked before
    // re-processing so toggling the status away and back to Delivered can never award
    // points twice for the same order. Independent of the "was the previous status
    // already Delivered" check in OrderService.UpdateStatusAsync, which alone doesn't
    // survive a Delivered -> Cancelled -> Delivered round trip.
    public bool PointsAwarded { get; set; }

    // Soft delete, for clearing test and mistaken orders without destroying the rows
    // that hang off them. A hard delete would cascade OrderItems away and null the
    // OrderId on any LoyaltyPointTransaction that referenced this order, silently
    // rewriting a customer's points history to explain itself with nothing.
    //
    // A deleted order is excluded from EVERY report - revenue, AOV, sales channels and
    // peak hours - because leaving a test order in the analytics is the problem this
    // exists to solve.
    public bool IsDeleted { get; set; }

    // Set only for orders imported from an external POS (e.g. "Dgtera"). The pair
    // (ExternalSource, ExternalOrderId) is what DgteraSyncService matches on to decide
    // insert vs. update, so re-running a sync never creates duplicates. Both stay null
    // for orders placed normally through this app.
    public string? ExternalSource { get; set; }
    public string? ExternalOrderId { get; set; }

    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    // ==========================================================================================
    // Added by the shared kernel. Additions only, each a new nullable or defaulted column, so
    // the delivery system's existing rows, queries and clients carry on unchanged.
    // ==========================================================================================

    // The order's identity everywhere: sync between cloud and restaurant, SignalR messages,
    // kitchen tickets, audit entries. Generated by whichever system creates the order, so an
    // order taken offline at the till already has an identity nobody else will use. Existing
    // rows are backfilled by the migration that adds the column.
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // Delivery is the default because every order the delivery system has created so far is
    // either a delivery or, with IsPickup set, a takeaway. The migration that adds this column
    // maps IsPickup rows to Takeaway.
    public OrderType Type { get; set; } = OrderType.Delivery;

    // Dine-in only. DeliveryAddress is left empty for a dine-in order.
    public string? TableNumber { get; set; }

    // The staff member who created the order: a captain at a table, a cashier at the till.
    // Null for an order a customer placed online. OrderAccessPolicy reads it: a captain may
    // only add to orders they created.
    public string? CreatedByUserId { get; set; }

    // Who took the final payment and closed the bill, and when. Null until then. A closed
    // order is settled; see OrderRules.IsSettled.
    public string? ClosedByUserId { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Money handed back by Void After, accumulated. TotalAmount stays what was charged, so
    // the original receipt and the reversal can both be read back.
    public decimal RefundedAmount { get; set; }

    // Redeemed points handed back to the customer's wallet by Void After, accumulated: each
    // refunded item returns its share of PointsRedeemed. Sent to the delivery system through
    // the outbox as it happens; this is the till's running total, so the last refund returns
    // exactly what is left.
    public int PointsRefunded { get; set; }

    // The part of a promo code's discount that came off the delivery fee. The delivery system
    // computes it at checkout but has never stored it: DeliveryFee holds the full fee, and
    // only TotalAmount reflects the discount. Any later re-pricing therefore put the fee back.
    // Its own order-edit path does exactly that today. OrderPricing needs it to re-price an
    // order without losing a free-delivery promo, so the delivery system should start
    // writing it when it adopts this kernel. Rows from before then hold 0.
    public decimal DeliveryDiscountAmount { get; set; }

    // Sum of the items still on the bill: everything except items voided before payment.
    // Items voided after payment stay in, because they were charged; their reversal is in
    // RefundedAmount.
    [JsonIgnore]
    public decimal BilledItemsSubtotal =>
        OrderItems.Where(i => i.VoidType is null or VoidType.AfterPayment).Sum(i => i.LineTotal);
}
