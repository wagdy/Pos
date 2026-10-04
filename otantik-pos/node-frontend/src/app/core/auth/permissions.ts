// The shared kernel's Permissions (Otantik.SharedKernel.Authorization), by the same strings. A
// role's set arrives from GET /api/auth/me, worked out by the same RolePermissions table the API
// enforces, so a button shows exactly when the API would allow what it does.
export const Permissions = {
  OrderCreate: 'order.create',
  OrderAddItems: 'order.add-items',
  OrderSendToKitchen: 'order.send-to-kitchen',
  OrderViewOwn: 'order.view-own',
  OrderViewAll: 'order.view-all',
  OrderUpdateFulfilment: 'order.update-fulfilment',
  OrderApplyLoyalty: 'order.apply-loyalty',
  OrderCheckout: 'order.checkout',
  ReceiptPrintFinal: 'receipt.print-final',
  VoidBeforeKitchen: 'order.void.before-kitchen',
  VoidAfterKitchen: 'order.void.after-kitchen',
  VoidAfterPayment: 'order.void.after-payment',
  InventoryView: 'inventory.view',
  InventoryManage: 'inventory.manage',
  MenuManage: 'menu.manage',
  StaffManage: 'staff.manage',
} as const;

export type Permission = (typeof Permissions)[keyof typeof Permissions];

// Every way money leaves or is taken: what a captain may never do.
export const MoneyPermissions: readonly Permission[] = [
  Permissions.OrderCheckout,
  Permissions.ReceiptPrintFinal,
  Permissions.OrderApplyLoyalty,
  Permissions.VoidBeforeKitchen,
  Permissions.VoidAfterKitchen,
  Permissions.VoidAfterPayment,
];
