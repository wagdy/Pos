import { MenuItem, Order, OrderItem, OrderStatus, OrderType, VoidType } from '../api/models';
import { Permission, Permissions } from '../auth/permissions';

// The shared kernel's OrderRules and OrderStatuses, mirrored so the till can show the right
// buttons before asking. The API applies the real rules and refuses with a reason anyway; these
// only keep the screen from offering what it would refuse.

const fulfilled: readonly OrderStatus[] = ['Delivered', 'Collected', 'Served'];

export function isFulfilled(status: OrderStatus): boolean {
  return fulfilled.includes(status);
}

export function isFinal(status: OrderStatus): boolean {
  return isFulfilled(status) || status === 'Cancelled';
}

// Paid: closed at the till, or a delivery-system order whose payment is confirmed and was either
// taken up front or collected on fulfilment.
export function isSettled(order: Order): boolean {
  return (
    order.closedAt !== null ||
    ((order.paymentStatus === 'Confirmed' ||
      order.paymentStatus === 'PartiallyRefunded' ||
      order.paymentStatus === 'Refunded') &&
      (order.paymentMethod !== 'Cash' || isFulfilled(order.status)))
  );
}

// Items can still be added and sent.
export function isOpen(order: Order): boolean {
  return !isSettled(order) && !isFinal(order.status);
}

// The bill is still to be paid, even if the food has been handed over: a cash-on-delivery
// driver brings the money back after delivering. Payment and points follow this, not isOpen.
export function awaitsPayment(order: Order): boolean {
  return !isSettled(order) && order.status !== 'Cancelled';
}

export function isVoided(item: OrderItem): boolean {
  return item.voidType !== null;
}

export function isSentToKitchen(item: OrderItem): boolean {
  return item.sentToKitchenAt !== null;
}

// What voiding this item now would be; null when it cannot be voided any more.
export function voidTypeFor(order: Order, item: OrderItem): VoidType | null {
  if (isVoided(item) || order.status === 'Cancelled' || order.paymentStatus === 'Refunded') {
    return null;
  }
  if (isSettled(order)) {
    return 'AfterPayment';
  }
  return isSentToKitchen(item) ? 'AfterKitchen' : 'BeforeKitchen';
}

export function voidPermissionFor(type: VoidType): Permission {
  switch (type) {
    case 'BeforeKitchen':
      return Permissions.VoidBeforeKitchen;
    case 'AfterKitchen':
      return Permissions.VoidAfterKitchen;
    case 'AfterPayment':
      return Permissions.VoidAfterPayment;
  }
}

// Takeaway and delivery identify the customer by mobile number: that is how their loyalty
// profile is found in the delivery system.
export function requiresCustomerPhone(type: OrderType): boolean {
  return type === 'Takeaway' || type === 'Delivery';
}

export function lineTotal(item: OrderItem): number {
  return item.quantity * (item.unitPrice + item.addOns.reduce((sum, addOn) => sum + addOn.price, 0));
}

export function liveItems(order: Order): OrderItem[] {
  return order.orderItems.filter((item) => !isVoided(item));
}

export function unsentItems(order: Order): OrderItem[] {
  return liveItems(order).filter((item) => !isSentToKitchen(item));
}

// Items still on the bill: everything but what was voided before payment.
export function billedSubtotal(order: Order): number {
  return order.orderItems
    .filter((item) => item.voidType === null || item.voidType === 'AfterPayment')
    .reduce((sum, item) => sum + lineTotal(item), 0);
}

export function orderLabel(order: Order): string {
  switch (order.type) {
    case 'DineIn':
      return `Table ${order.tableNumber ?? '?'}`;
    case 'Takeaway':
      return `Takeaway · ${order.customerName || order.customerPhone}`;
    case 'Delivery':
      return `Delivery · ${order.customerName || order.customerPhone}`;
  }
}

// Where the order stands, in the till's words.
export function statusText(order: Order): string {
  if (order.paymentStatus === 'Refunded') {
    return 'Refunded';
  }
  if (order.paymentStatus === 'PartiallyRefunded') {
    return 'Paid · part refunded';
  }
  if (isSettled(order)) {
    return 'Paid';
  }
  if (order.status === 'Cancelled') {
    return 'Cancelled';
  }
  // Handed over, the money still to come: a cash-on-delivery driver on his way back.
  return isFulfilled(order.status) ? `${order.status} · not paid` : 'Open';
}

export function itemName(item: OrderItem): string {
  return item.variantName ? `${item.menuItemName} (${item.variantName})` : item.menuItemName;
}

// What points are worth off a bill: the shared kernel's LoyaltyRedemption, at the rate the
// delivery system sets (L.E per 100 points; 10 is Points / 10), rounded half away from zero to
// the piastre exactly as both systems round it.
export function pointsDiscount(points: number, valuePer100Points: number): number {
  if (points <= 0 || valuePer100Points <= 0) {
    return 0;
  }
  // The nudge keeps a true half-piastre (33 points at 12.5 is 4.125) from landing just under it
  // in floating point and rounding down.
  return Math.round((points / 100) * valuePer100Points * 100 + 1e-9) / 100;
}

export function maxPointsFor(amount: number, valuePer100Points: number): number {
  if (amount <= 0 || valuePer100Points <= 0) {
    return 0;
  }
  return Math.floor(amount / (valuePer100Points / 100) + 1e-9);
}

// What the API's OrderItemBuilder will refuse, said before the cashier taps Add.
export function addItemProblem(menuItem: MenuItem, variantId: number | null, addOnIds: number[]): string | null {
  if (!menuItem.isAvailable) {
    return `'${menuItem.name}' is currently unavailable.`;
  }
  const variant = menuItem.variants.find((v) => v.id === variantId) ?? null;
  if (menuItem.variants.length > 0 && variant === null) {
    return `Please choose a size for '${menuItem.name}'.`;
  }
  if (variant !== null && !variant.isAvailable) {
    return `'${variant.name}' of '${menuItem.name}' is currently unavailable.`;
  }
  const addOnsPrice = menuItem.menuItemAddOns
    .filter((link) => addOnIds.includes(link.addOnId))
    .reduce((sum, link) => sum + link.addOn.price, 0);
  if (menuItem.isPriceBasedOnAddons) {
    return addOnsPrice > 0 ? null : `Please choose at least one paid option for '${menuItem.name}'.`;
  }
  const price = variant?.price ?? menuItem.price;
  return price > 0 ? null : `'${menuItem.name}' is priced on the day and has no fixed price to charge.`;
}
