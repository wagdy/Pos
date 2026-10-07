using static Otantik.SharedKernel.Authorization.Permissions;

namespace Otantik.SharedKernel.Authorization;

// What each role may do. The single table both APIs and both Angular apps read.
//
// Fail closed: a role missing from this table gets nothing. That matters across two separately
// deployed systems. When the cloud has shipped a role the restaurant machine has not heard of
// yet, the machine must refuse that role, not guess.
public static class RolePermissions
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    // Floor staff: create and submit, and nothing that touches money or removes an item.
    // ViewOwn so a captain can find a table's order again to add the next round.
    private static readonly IReadOnlySet<string> CaptainOrder = new HashSet<string>
    {
        OrderCreate, OrderAddItems, OrderSendToKitchen, OrderViewOwn,
    };

    // Drivers: what they could do in the delivery system before their move from CaptainOrder,
    // minus cancelling, which is a void.
    private static readonly IReadOnlySet<string> DeliveryCaptain = new HashSet<string>
    {
        OrderViewAll, OrderUpdateFulfilment,
    };

    // "Full authority to process payments, close orders, and execute voids", every kind. If a
    // refund should need a manager, remove VoidAfterPayment here; nothing else changes.
    private static readonly IReadOnlySet<string> Cashier = new HashSet<string>
    {
        OrderCreate, OrderAddItems, OrderSendToKitchen, OrderViewOwn, OrderViewAll,
        OrderUpdateFulfilment, OrderApplyLoyalty, OrderCheckout, ReceiptPrintFinal,
        VoidBeforeKitchen, VoidAfterKitchen, VoidAfterPayment,
        InventoryView,
    };

    private static readonly IReadOnlySet<string> Manager = new HashSet<string>(Cashier)
    {
        InventoryManage, MenuManage, StaffManage, CostingView,
    };

    // Everything. In the delivery system an Admin can additionally be narrowed by a custom
    // Role's AdminModules; that check stays where it is, on top of this one.
    private static readonly IReadOnlySet<string> Admin = new HashSet<string>(Manager);

    private static readonly IReadOnlyDictionary<UserRole, IReadOnlySet<string>> ByRole =
        new Dictionary<UserRole, IReadOnlySet<string>>
        {
            [UserRole.Customer] = None,
            [UserRole.CaptainOrder] = CaptainOrder,
            [UserRole.DeliveryCaptain] = DeliveryCaptain,
            [UserRole.Cashier] = Cashier,
            [UserRole.Manager] = Manager,
            [UserRole.Admin] = Admin,
        };

    public static IReadOnlySet<string> For(UserRole role) =>
        ByRole.TryGetValue(role, out var permissions) ? permissions : None;

    public static bool Has(UserRole role, string permission) => For(role).Contains(permission);
}
