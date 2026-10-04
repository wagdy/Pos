namespace Otantik.SharedKernel.Auditing;

public enum OrderAuditAction
{
    Created,
    ItemsAdded,
    SentToKitchen,
    LoyaltyApplied,
    ItemVoided,
    OrderVoided,
    PaymentTaken,
    FinalReceiptPrinted,
}

// One line in an order's audit trail: who did what, when, and what it was worth. Append-only:
// an entry is never edited or deleted, and a correction is a new entry. Every void, payment
// and refund writes one, which is the "respective audit logs" the voids require.
//
// Refers to orders and items by PublicId, so an entry written on the restaurant machine still
// points at the right order once both are synced to the cloud.
public class OrderAuditEntry
{
    // This database's own row number; PublicId is the entry's identity across systems.
    public long Id { get; set; }

    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid OrderPublicId { get; set; }

    // Set when the action was on one item, such as a single void.
    public Guid? OrderItemPublicId { get; set; }

    public OrderAuditAction Action { get; set; }

    // For ItemVoided and OrderVoided: which kind, as decided by OrderRules.
    public VoidType? VoidType { get; set; }

    // Who did it, and in what role at that moment. The role is recorded, not looked up later,
    // because a cashier promoted to manager next month must not rewrite who voided what today.
    public string UserId { get; set; } = string.Empty;
    public UserRole Role { get; set; }

    public int? Quantity { get; set; }

    // The money involved: a void's value off the bill, a refund, a payment taken.
    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public static OrderAuditEntry For(
        Order order,
        OrderAuditAction action,
        ICurrentUser user,
        decimal amount = 0,
        OrderItem? item = null,
        VoidType? voidType = null,
        int? quantity = null,
        string? reason = null) => new()
    {
        OrderPublicId = order.PublicId,
        OrderItemPublicId = item?.PublicId,
        Action = action,
        VoidType = voidType,
        UserId = user.UserId,
        Role = user.Role,
        Quantity = quantity,
        Amount = amount,
        Reason = reason,
    };
}
