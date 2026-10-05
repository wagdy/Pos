// The restaurant machine's API, as JSON. Orders are the shared kernel's Order exactly as the API
// and the delivery system serialize it (camelCase, enums as names), so an order looks the same
// here whether a cashier opened it or a captain sent it from the delivery app.

export type OrderType = 'Delivery' | 'Takeaway' | 'DineIn';

export type OrderStatus =
  | 'Pending'
  | 'Preparing'
  | 'OutForDelivery'
  | 'Delivered'
  | 'ReadyForCollection'
  | 'Collected'
  | 'Cancelled'
  | 'Served';

export type PaymentStatus = 'Confirmed' | 'Pending' | 'PartiallyRefunded' | 'Refunded';

export type PaymentMethod = 'Cash' | 'Visa' | 'Instapay';

export type VoidType = 'BeforeKitchen' | 'AfterKitchen' | 'AfterPayment';

export type UserRole = 'Customer' | 'Admin' | 'CaptainOrder' | 'Cashier' | 'Manager' | 'DeliveryCaptain';

export interface OrderItemAddOn {
  id: number;
  addOnId: number;
  name: string;
  price: number;
}

export interface OrderItem {
  id: number;
  publicId: string;
  menuItemId: number;
  menuItemName: string;
  variantId: number | null;
  variantName: string | null;
  quantity: number;
  unitPrice: number;
  addOns: OrderItemAddOn[];
  notes: string | null;
  sentToKitchenAt: string | null;
  voidType: VoidType | null;
  voidReason: string | null;
  voidedByUserId: string | null;
  voidedAt: string | null;
  refundAmount: number;
}

// `publicId` is the id both systems agree on, and the one every endpoint takes. `id` is only
// the restaurant database's row number.
export interface Order {
  id: number;
  publicId: string;
  type: OrderType;
  tableNumber: string | null;
  userId: string | null;
  customerName: string;
  customerPhone: string;
  deliveryAddress: string;
  status: OrderStatus;
  paymentMethod: PaymentMethod;
  paymentStatus: PaymentStatus;
  totalAmount: number;
  taxAmount: number;
  deliveryFee: number;
  discountAmount: number;
  deliveryDiscountAmount: number;
  pointsRedeemed: number;
  pointsDiscountAmount: number;
  pointsRefunded: number;
  refundedAmount: number;
  notes: string | null;
  createdByUserId: string | null;
  closedByUserId: string | null;
  closedAt: string | null;
  createdAt: string;
  updatedAt: string;
  orderItems: OrderItem[];
}

// The menu, as the delivery system's own catalog entities. Flat lists, grouped by id here.
export interface Category {
  id: number;
  name: string;
  nameAr: string | null;
  displayOrder: number;
}

export interface SubCategory {
  id: number;
  name: string;
  nameAr: string | null;
  displayOrder: number;
  categoryId: number;
}

export interface AddOn {
  id: number;
  name: string;
  nameAr: string | null;
  price: number;
}

export interface MenuItemAddOn {
  menuItemId: number;
  addOnId: number;
  addOn: AddOn;
}

export interface MenuItemVariant {
  id: number;
  menuItemId: number;
  name: string;
  nameAr: string | null;
  price: number;
  displayOrder: number;
  isAvailable: boolean;
}

export interface MenuItem {
  id: number;
  name: string;
  nameAr: string | null;
  description: string | null;
  price: number;
  imageUrl: string | null;
  isAvailable: boolean;
  isPriceBasedOnAddons: boolean;
  priceNote: string | null;
  // The category's name, as free text: an item belongs to the category of that name. The
  // sub-category, when there is one, only groups items within it.
  category: string;
  subCategoryId: number | null;
  menuItemAddOns: MenuItemAddOn[];
  variants: MenuItemVariant[];
}

export interface TillMenu {
  categories: Category[];
  subCategories: SubCategory[];
  menuItems: MenuItem[];
}

// Who is signed in. The role and permissions come from GET /api/auth/me only: the same shared
// RolePermissions table the API enforces.
export interface Me {
  id: string;
  fullName: string;
  role: UserRole;
  permissions: string[];
}

export interface SignInOption {
  id: string;
  fullName: string;
}

export interface SignInResponse {
  token: string;
  expiresAtUtc: string;
}

export interface CustomerProfile {
  userId: string;
  fullName: string;
  phoneNumber: string;
  pointsBalance: number;
}

// Found: an account has this number. NotFound: a walk-in. Unavailable: the delivery system
// could not be asked; the number still goes on the order.
export type CustomerLookupStatus = 'Found' | 'NotFound' | 'Unavailable';

export interface CustomerLookupResult {
  status: CustomerLookupStatus;
  profile: CustomerProfile | null;
  // The redemption rate in force (L.E per 100 points; 10 is Points / 10), and what the whole
  // balance is worth at it.
  redemptionValuePer100Points: number;
  pointsValue: number;
}

export interface AttachCustomerResult {
  order: Order;
  lookup: CustomerLookupStatus;
}

export type DeliverySystemLinkState = 'NotConfigured' | 'Connecting' | 'Online' | 'Offline';

// The restaurant machine's link to the delivery system, which customer lookup and points need.
export interface DeliverySystemLinkStatus {
  state: DeliverySystemLinkState;
  sinceUtc: string;
  cloudFeaturesAvailable: boolean;
}

export interface NodeStatus {
  deliverySystem: DeliverySystemLinkStatus;
}

// Request bodies.
export interface OpenOrderRequest {
  type: OrderType;
  tableNumber?: string | null;
  customerPhone?: string | null;
  customerName?: string | null;
  deliveryAddress?: string | null;
  deliveryFee?: number;
  notes?: string | null;
}

export interface AddOrderItemRequest {
  menuItemId: number;
  quantity: number;
  variantId: number | null;
  addOnIds: number[];
  notes: string | null;
}

// GET /api/reports/day: one business day of this till's orders and what they came to.
export interface VoidTotal {
  items: number;
  value: number;
}

export interface DaySummary {
  orders: number;
  paidOrders: number;
  openOrders: number;
  openValue: number;
  cancelledOrders: number;
  // What the paid orders charged, after any points discount.
  grossSales: number;
  refunds: number;
  netSales: number;
  averageTicket: number;
  taxCharged: number;
  pointsRedeemed: number;
  pointsDiscount: number;
  pointsReturned: number;
  voidedBeforeKitchen: VoidTotal;
  voidedAfterKitchen: VoidTotal;
}

export interface PaymentMethodTotal {
  method: PaymentMethod;
  orders: number;
  charged: number;
  refunded: number;
  net: number;
}

export interface OrderTypeTotal {
  type: OrderType;
  orders: number;
  net: number;
}

export interface DayReport {
  // yyyy-MM-dd: the restaurant's business day, which starts at its configured hour (4 AM).
  businessDate: string;
  fromUtc: string;
  toUtc: string;
  summary: DaySummary;
  byPaymentMethod: PaymentMethodTotal[];
  byOrderType: OrderTypeTotal[];
  orders: Order[];
}
