namespace Otantik.SharedKernel.Authorization;

// Every action the order and back-office endpoints guard. A role grants a set of these (see
// RolePermissions), and code checks the permission, never the role name. Adding a role, or
// moving a right from one role to another, is then a change to one table, not a search
// through every controller for IsInRole("...").
//
// Separate from the delivery system's GranularPermissions, which only show and hide sub-tabs
// of its admin UI.
public static class Permissions
{
    // Taking orders.
    public const string OrderCreate = "order.create";
    public const string OrderAddItems = "order.add-items";
    public const string OrderSendToKitchen = "order.send-to-kitchen";

    // Seeing orders. ViewOwn is orders the user created; ViewAll is every order.
    public const string OrderViewOwn = "order.view-own";
    public const string OrderViewAll = "order.view-all";

    // Moving an order along its delivery or collection leg (Preparing, OutForDelivery,
    // Delivered, ReadyForCollection, Collected). Never Cancelled: that is a void.
    public const string OrderUpdateFulfilment = "order.update-fulfilment";

    // Money.
    public const string OrderApplyLoyalty = "order.apply-loyalty";
    public const string OrderCheckout = "order.checkout";
    public const string ReceiptPrintFinal = "receipt.print-final";

    // One per VoidType, so a role can be allowed one kind of void and not another.
    public const string VoidBeforeKitchen = "order.void.before-kitchen";
    public const string VoidAfterKitchen = "order.void.after-kitchen";
    public const string VoidAfterPayment = "order.void.after-payment";

    // Back office.
    public const string InventoryView = "inventory.view";
    public const string InventoryManage = "inventory.manage";
    public const string MenuManage = "menu.manage";
    public const string StaffManage = "staff.manage";

    // What dishes and ingredients cost, and the food cost reports: a manager's, not a cashier's.
    public const string CostingView = "costing.view";

    public static string ForVoid(VoidType type) => type switch
    {
        VoidType.BeforeKitchen => VoidBeforeKitchen,
        VoidType.AfterKitchen => VoidAfterKitchen,
        VoidType.AfterPayment => VoidAfterPayment,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
