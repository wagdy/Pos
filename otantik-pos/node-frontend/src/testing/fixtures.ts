import { Me, Order, OrderItem } from '../app/core/api/models';

// Orders as the API returns them, for specs: an open dine-in table unless told otherwise.
export function anItem(overrides: Partial<OrderItem> = {}): OrderItem {
  return {
    id: 1,
    publicId: crypto.randomUUID(),
    menuItemId: 1,
    menuItemName: 'Burger',
    variantId: null,
    variantName: null,
    quantity: 1,
    unitPrice: 120,
    addOns: [],
    notes: null,
    sentToKitchenAt: null,
    voidType: null,
    voidReason: null,
    voidedByUserId: null,
    voidedAt: null,
    refundAmount: 0,
    ...overrides,
  };
}

export function anOrder(overrides: Partial<Order> = {}): Order {
  return {
    id: 1,
    publicId: crypto.randomUUID(),
    type: 'DineIn',
    tableNumber: '4',
    userId: null,
    customerName: '',
    customerPhone: '',
    deliveryAddress: '',
    status: 'Pending',
    paymentMethod: 'Cash',
    paymentStatus: 'Pending',
    totalAmount: 0,
    taxAmount: 0,
    deliveryFee: 0,
    discountAmount: 0,
    deliveryDiscountAmount: 0,
    pointsRedeemed: 0,
    pointsDiscountAmount: 0,
    pointsRefunded: 0,
    refundedAmount: 0,
    notes: null,
    createdByUserId: 'staff-cashier',
    closedByUserId: null,
    closedAt: null,
    createdAt: '2026-10-03T12:00:00Z',
    updatedAt: '2026-10-03T12:00:00Z',
    orderItems: [],
    ...overrides,
  };
}

// What GET /api/auth/me returns for each till-facing role: the shared RolePermissions table.
const captain = ['order.add-items', 'order.create', 'order.send-to-kitchen', 'order.view-own'];
const cashier = [
  ...captain,
  'inventory.view',
  'order.apply-loyalty',
  'order.checkout',
  'order.update-fulfilment',
  'order.view-all',
  'order.void.after-kitchen',
  'order.void.after-payment',
  'order.void.before-kitchen',
  'receipt.print-final',
];

export const captainMe: Me = { id: 'staff-captain', fullName: 'Hany', role: 'CaptainOrder', permissions: captain };
export const cashierMe: Me = { id: 'staff-cashier', fullName: 'Sara', role: 'Cashier', permissions: cashier };
