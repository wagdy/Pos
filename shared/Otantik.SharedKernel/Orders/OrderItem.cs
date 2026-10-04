using System.Text.Json.Serialization;

namespace Otantik.SharedKernel.Orders;

public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    // Back-reference; the item travels inside its order, never on its own.
    [JsonIgnore]
    public Order Order { get; set; } = null!;

    public int MenuItemId { get; set; }

    // The catalog travels separately (cloud to restaurant). An order carries MenuItemId and
    // the snapshotted name and price below, which are what a receipt needs.
    [JsonIgnore]
    public MenuItem MenuItem { get; set; } = null!;

    // Snapshotted at order time, exactly like OrderItemAddOn.Name and UnitPrice below.
    // Until now the receipt read oi.MenuItem.Name through the navigation, which meant a
    // historical order could only be displayed while its menu item still existed and was
    // still visible to the query. Soft delete breaks precisely that: a global query
    // filter applies to included navigations too, so oi.MenuItem would come back null
    // and every past order containing a deleted item would throw on load.
    public string MenuItemName { get; set; } = string.Empty;

    // Which size/weight was ordered. Null for items that have no variants.
    //
    // The NAME is snapshotted for the same reason MenuItemName above is: a variant is
    // cascade-deleted with its menu item, so reading it back through a navigation would
    // lose the receipt. VariantId is kept only for reporting ("how many 1-Kilo trays did
    // we sell") and is deliberately not what the receipt renders.
    public int? VariantId { get; set; }
    public string? VariantName { get; set; }

    public int Quantity { get; set; }

    // Already the RESOLVED price: the chosen variant's price when there is one, the menu
    // item's base price otherwise. Nothing downstream has to know which case it was.
    public decimal UnitPrice { get; set; }

    public ICollection<OrderItemAddOn> AddOns { get; set; } = new List<OrderItemAddOn>();

    // ==========================================================================================
    // Added by the shared kernel. Additions only.
    // ==========================================================================================

    // The item's identity across systems, for the same reason as Order.PublicId. Voids,
    // kitchen tickets and stock deductions all name a single item, and its Id differs between
    // the cloud and the restaurant machine.
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // For the kitchen, such as "no onions". Printed on the ticket, never priced. The delivery
    // system only ever had order-level Notes.
    public string? Notes { get; set; }

    // When the item was printed on a kitchen ticket. Null while it is only on the bill. This
    // is the line between a Void Before (free) and a void after the kitchen (waste).
    public DateTime? SentToKitchenAt { get; set; }

    // Null while the item stands. Which kind of void it was is decided by OrderRules, never
    // chosen by the person voiding; see VoidType.
    public VoidType? VoidType { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }

    // Set only by a Void After: this item's share of what the customer actually paid.
    public decimal RefundAmount { get; set; }

    // The same arithmetic as the delivery system's OrderItemResponse.LineTotal: add-ons are
    // priced per unit, so they multiply by Quantity too.
    [JsonIgnore]
    public decimal LineTotal => Quantity * (UnitPrice + AddOns.Sum(a => a.Price));

    [JsonIgnore]
    public bool IsVoided => VoidType is not null;

    [JsonIgnore]
    public bool IsSentToKitchen => SentToKitchenAt is not null;
}
