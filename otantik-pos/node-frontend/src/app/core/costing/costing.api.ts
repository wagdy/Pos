import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of, throwError } from 'rxjs';

// The manager's costing, as the till server's api/costing and api/inventory speak it.

export type UnitOfMeasure = 'Gram' | 'Millilitre' | 'Piece';
export type RecipeTargetKind = 'MenuItem' | 'AddOn';
export type CostStatus = 'WithinTarget' | 'AboveTarget' | 'NoRecipe' | 'MissingPrices' | 'NoPrice';
// Bought from a supplier, or made in the kitchen (a prep item: dough, a house sauce).
export type MaterialKind = 'Purchased' | 'Prep';

// A raw material with its costs. Quantities are in its unit (grams, millilitres, pieces); costs
// are per purchase unit (a kg, a carton), as prices are written.
export interface MaterialCost {
  id: string;
  code: string | null;
  name: string;
  // Its purchase category's name, and the category.
  category: string | null;
  categoryId: string;
  unit: UnitOfMeasure;
  purchaseUnit: string;
  purchaseUnitSize: number;
  costPerPurchaseUnit: number | null;
  defaultYieldPercent: number;
  quantityOnHand: number;
  reorderLevel: number;
  stockValue: number | null;
  lastPurchaseCostPerPurchaseUnit: number | null;
  lastPurchasedAtUtc: string | null;
  // Per gram, millilitre or piece, unrounded: what recipe costs are worked out from.
  costPerUnit: number | null;
  kind: MaterialKind;
}

export interface RecipeCostLine {
  rawMaterialId: string;
  code: string | null;
  ingredient: string;
  quantity: number;
  unit: UnitOfMeasure;
  purchaseUnit: string;
  purchaseUnitSize: number;
  apsPerUnit: number | null;
  yieldPercent: number;
  epsPerUnit: number | null;
  recipeCost: number | null;
}

// The Recipe Costing Template for one dish, size or add-on.
// One recipe's costing card, or a menu item's by the recipe it sells by. recipe is the name it
// goes by (its own, or its menu item's); menuPrice what it is costed against (the menu's, or an
// off-menu recipe's planned price). No target: an off-menu (R&D) recipe.
export interface RecipeCostCard {
  targetKind: RecipeTargetKind | null;
  catalogItemId: number | null;
  variantId: number | null;
  itemCode: string;
  recipe: string;
  recipeAr: string | null;
  category: string | null;
  hasRecipe: boolean;
  portions: number;
  menuPrice: number | null;
  foodCostTargetPercent: number;
  lines: RecipeCostLine[];
  costPerRecipe: number;
  ingredientCostPerPortion: number;
  sharedCostPerPortion: number;
  costPerPortion: number;
  marginPerPortion: number | null;
  foodCostPercentActual: number | null;
  idealSellingPrice: number;
  missingPrices: string[];
  status: CostStatus;
  recipeId: string | null;
  // Its own name, when it has one.
  recipeName: string | null;
  // The menu item it is linked to, by name.
  linkedTo: string | null;
  isStandalone: boolean;
  plannedPrice: number | null;
  // A size with no recipe of its own, costed by the dish's.
  usesBaseRecipe: boolean;
  isDeleted: boolean;
  deletedAtUtc: string | null;
  deletedBy: string | null;
  // The kitchen's own category for it, apart from the menu's; null until it is sorted.
  recipeCategoryId: string | null;
  recipeCategory: string | null;
  recipeCategoryAr: string | null;
}

// The kitchen's own grouping of its recipes: Sauces, Staff meals, Catering orders.
export interface RecipeCategory {
  id: string;
  name: string;
  nameAr: string | null;
  // Active recipes in it.
  recipes: number;
}

// A recipe as written: name and link optional on the menu, the name required off it; the
// category always.
export interface SaveRecipe {
  name: string | null;
  recipeCategoryId: string | null;
  plannedPrice: number | null;
  targetKind: RecipeTargetKind | null;
  catalogItemId: number | null;
  variantId: number | null;
  portions: number;
  ingredients: RecipeLine[];
}

export interface TheoreticalCostRow {
  itemCode: string;
  menuItemId: number;
  variantId: number | null;
  menuItem: string;
  menuItemAr: string | null;
  category: string;
  quantitySold: number;
  netSales: number;
  recipeCostPerUnit: number;
  theoreticalCost: number;
  sharedCost: number;
  foodCostPercent: number | null;
  status: CostStatus;
  // Sold before the dish had a recipe: costed by today's recipe at today's prices.
  quantityCostedNow: number;
  // Entered by hand: a prep item's unit (kg, L), its quantity being in it rather than portions;
  // and which recipe or prep item the row is, for one off the menu (menuItemId 0).
  unit?: string | null;
  recipeId?: string | null;
  prepItemId?: string | null;
}

export interface TheoreticalCostReport {
  year: number;
  month: number;
  from: string;
  to: string;
  foodCostTargetPercent: number;
  rows: TheoreticalCostRow[];
  quantitySold: number;
  netSales: number;
  theoreticalCost: number;
  foodCostPercent: number | null;
  // Pos: the till's sales. Manual: the quantities typed in for the month, by whom and when.
  source: TheoreticalSource;
  enteredBy: string | null;
  enteredAtUtc: string | null;
}

export type TheoreticalSource = 'Pos' | 'Manual';

export type ManualSalesItemKind = 'Recipe' | 'PrepItem';

// A row of the month's sales sheet: an active recipe, in portions, or a prep item, in its unit.
// costPerUnit is null when it cannot be costed; price is null off the menu with no planned price,
// and for prep items. quantity is what was saved for the month.
export interface ManualSalesItem {
  kind: ManualSalesItemKind;
  itemId: string;
  code: string | null;
  name: string;
  nameAr: string | null;
  group: string;
  unit: string;
  costPerUnit: number | null;
  price: number | null;
  quantity: number | null;
}

export interface ManualSalesSheet {
  year: number;
  month: number;
  items: ManualSalesItem[];
  enteredBy: string | null;
  enteredAtUtc: string | null;
}

export interface ManualSalesLine {
  kind: ManualSalesItemKind;
  itemId: string;
  quantity: number;
}

export interface CostingSettings {
  foodCostTargetPercent: number;
  // Left out of a save, these stay as they are.
  varianceTolerancePercent?: number | null;
  // Menu categories that are drinks: the beverage cost %. None, everything is food.
  beverageCategories?: string[] | null;
}

export type KpiKey =
  | 'FoodCost'
  | 'BeverageCost'
  | 'LabourCost'
  | 'AverageCheck'
  | 'SalesPerLabourHour'
  | 'GrossProfitMargin'
  | 'NetProfitMargin';

export type KpiStatus = 'Healthy' | 'Above' | 'Below' | 'NoRange' | 'Missing';

// Value is a % for the ratios, money for the average check and sales per labour hour. Missing
// says what is needed when there is no value.
export interface Kpi {
  key: KpiKey;
  value: number | null;
  healthyFrom: number | null;
  healthyTo: number | null;
  highIsBad: boolean;
  status: KpiStatus;
  missing: string | null;
}

// The month as an income statement: revenue before VAT, after promo discounts.
export interface MonthStatement {
  foodSales: number;
  beverageSales: number;
  deliveryFees: number;
  revenue: number;
  paidOrders: number;
  foodRecipeCost: number;
  beverageRecipeCost: number;
  kitchenWaste: number;
  spoilage: number;
  countDifferences: number;
  costOfSales: number;
  grossProfit: number;
  wagesAndBenefits: number | null;
  otherControllableCosts: number | null;
  occupationCost: number | null;
  interest: number | null;
  depreciation: number | null;
  netProfit: number | null;
  // Expense items on the month's posted purchase invoices, on each category's line, on top of
  // what is entered by hand; and each category's part. totalExpenses: every overhead together.
  loggedControllableCosts: number;
  loggedOccupationCost: number;
  loggedOtherFixedCosts: number;
  loggedExpenses: LoggedExpense[] | null;
  // Wages paid as expenses (vouchers, invoices under a Wages category), added to the wages entered.
  loggedWages: number;
  totalExpenses: number;
}

export interface LoggedExpense {
  categoryId: string;
  category: string;
  categoryAr: string | null;
  expenseLine: ExpenseLine;
  amount: number;
}

// What a month cost beyond its ingredients, as entered; null is not known yet.
export interface MonthlyExpenses {
  wagesAndBenefits: number | null;
  labourHours: number | null;
  otherControllableCosts: number | null;
  occupationCost: number | null;
  interest: number | null;
  depreciation: number | null;
  updatedAtUtc?: string | null;
  updatedBy?: string | null;
}

export interface ItemProfit {
  itemCode: string;
  menuItem: string;
  menuItemAr: string | null;
  category: string;
  beverage: boolean;
  quantitySold: number;
  netSales: number;
  cost: number | null;
  profit: number | null;
  profitPerUnit: number | null;
  marginPercent: number | null;
}

// The break-even worksheet's Income Statement column: the chosen months at a year's pace.
export interface BreakEvenWorksheet {
  from: string;
  to: string;
  days: number;
  weeklySales: number;
  grossSales: number;
  costOfSales: number;
  costOfSalesRatio: number;
  grossProfit: number;
  controllableCosts: number;
  occupationCost: number;
  interest: number;
  depreciation: number;
  // Logged expenses whose category counts them as other fixed costs.
  otherFixedCosts: number;
  totalFixedCosts: number;
  restaurantProfit: number;
  breakEvenYearlySales: number | null;
  breakEvenWeeklySales: number | null;
  monthsWithoutCosts: string[];
  scenarios: number[];
  // Of controllableCosts and occupationCost, what came from expense invoices rather than typed in.
  controllableFromInvoices: number;
  occupationFromInvoices: number;
}

// One column of the worksheet: weekly sales tried against the year's costs.
export interface BreakEvenColumn {
  weeklySales: number;
  grossSales: number;
  costOfSales: number;
  grossProfit: number;
  profitMarginPercent: number | null;
  controllableCosts: number;
  occupationCost: number;
  interest: number;
  depreciation: number;
  otherFixedCosts: number;
  totalFixedCosts: number;
  restaurantProfit: number;
}

// The template's arithmetic for an option: gross sales = weekly × 52, cost of sales at the
// income statement's ratio, the same fixed costs.
export function breakEvenColumn(sheet: BreakEvenWorksheet, weeklySales: number): BreakEvenColumn {
  const grossSales = weeklySales * 52;
  const costOfSales = grossSales * sheet.costOfSalesRatio;
  const grossProfit = grossSales - costOfSales;
  return {
    weeklySales,
    grossSales,
    costOfSales,
    grossProfit,
    profitMarginPercent: grossSales > 0 ? (grossProfit / grossSales) * 100 : null,
    controllableCosts: sheet.controllableCosts,
    occupationCost: sheet.occupationCost,
    interest: sheet.interest,
    depreciation: sheet.depreciation,
    otherFixedCosts: sheet.otherFixedCosts,
    totalFixedCosts: sheet.totalFixedCosts,
    restaurantProfit: grossProfit - sheet.totalFixedCosts,
  };
}

export interface KpiReport {
  year: number;
  month: number;
  from: string;
  to: string;
  beverageCategories: string[];
  statement: MonthStatement;
  expenses: MonthlyExpenses;
  kpis: Kpi[];
  items: ItemProfit[];
  itemsWithoutRecipe: number;
  salesWithoutRecipe: number;
}

// Voided: posted, then deleted with a manager's approval, its adjustments reversed.
export type StockCountStatus = 'Draft' | 'Posted' | 'Voided';

export interface StockCountSummary {
  id: string;
  status: StockCountStatus;
  startedAtUtc: string;
  startedBy: string;
  postedAtUtc: string | null;
  postedBy: string | null;
  materials: number;
  voidedAtUtc?: string | null;
  voidedBy?: string | null;
}

// Quantities in the material's unit (grams, millilitres, pieces). Book quantity and cost are
// filled in when the count is posted; a draft has neither.
export interface StockCountLine {
  rawMaterialId: string;
  countedQuantity: number;
  bookQuantity: number | null;
  unitCost: number | null;
}

export interface StockCount {
  id: string;
  status: StockCountStatus;
  startedAtUtc: string;
  startedBy: string;
  postedAtUtc: string | null;
  postedBy: string | null;
  lines: StockCountLine[];
  voidedAtUtc?: string | null;
  voidedBy?: string | null;
}

export interface SpoilageLine {
  rawMaterialId: string;
  quantity: number;
  reason: string;
}

export interface SpoilageEntry {
  spoilageId: string;
  recordedAtUtc: string;
  recordedBy: string | null;
  lines: {
    rawMaterialId: string;
    material: string;
    unit: UnitOfMeasure;
    purchaseUnitSize: number;
    quantity: number;
    reason: string | null;
    value: number | null;
  }[];
  value: number | null;
}

export type VarianceEvaluation = 'WithinLimit' | 'Unfavourable' | 'Favourable' | 'SharedCost';

// One material between two counts, in its unit. Variance = actual − standard: above zero, more
// left the stores than the recipes account for.
export interface VarianceRow {
  rawMaterialId: string;
  code: string | null;
  material: string;
  category: string | null;
  unit: UnitOfMeasure;
  purchaseUnitSize: number;
  opening: number;
  received: number;
  rawWaste: number;
  productWaste: number;
  standardUsage: number;
  expectedClosing: number;
  closing: number;
  actualUsage: number;
  varianceQuantity: number;
  variancePercent: number | null;
  unexplainedQuantity: number;
  unitCost: number | null;
  varianceValue: number | null;
  unexplainedValue: number | null;
  evaluation: VarianceEvaluation;
}

export interface VarianceReport {
  from: { id: string; postedAtUtc: string; postedBy: string | null };
  to: { id: string; postedAtUtc: string; postedBy: string | null };
  tolerancePercent: number;
  rows: VarianceRow[];
  unfavourableCount: number;
  netVarianceValue: number;
  recordedWasteValue: number;
  unexplainedValue: number;
  largestRelativeMaterial: string | null;
  largestRelativePercent: number | null;
  notInBothCounts: string[];
  soldWithoutStock: { itemCode: string; menuItem: string; quantity: number }[];
  missingPrices: string[];
}

export interface SharedCost {
  id: string;
  name: string;
  rawMaterialId: string | null;
  rawMaterialName: string | null;
  monthlyAmount: number | null;
  category: string | null;
}

export interface SaveSharedCost {
  name: string;
  rawMaterialId: string | null;
  monthlyAmount: number | null;
  category: string | null;
}

// Creating or changing a material. The unit is set once, when it is created: every recipe and
// ledger entry for it is in that unit.
export interface SaveMaterial {
  name: string;
  unit?: UnitOfMeasure;
  reorderLevel: number;
  code: string | null;
  // None: General.
  categoryId: string | null;
  purchaseUnit: string;
  purchaseUnitSize: number;
  defaultYieldPercent: number;
  costPerPurchaseUnit: number | null;
}

export interface RecipeLine {
  rawMaterialId: string;
  quantity: number;
  yieldPercent: number | null;
}

// Voided: posted, then deleted with a manager's approval, its goods taken back out of stock.
export type PurchaseInvoiceStatus = 'Draft' | 'Posted' | 'Voided';

export type PurchaseItemType = 'Inventory' | 'Expense';

// A line as written on the invoice (5, in kg of 1000 g) and what it comes to in the material's
// own unit (5,000 g). An expense line names its expense item instead, has no unit of stock, and
// is only its cost. material: the line's name, the material's or the expense's.
export interface PurchaseInvoiceLine {
  rawMaterialId: string | null;
  material: string;
  unit: UnitOfMeasure | null;
  enteredQuantity: number;
  unitLabel: string;
  unitSize: number;
  quantity: number;
  cost: number | null;
  itemType: PurchaseItemType;
  expenseItemId: string | null;
  categoryId: string | null;
  category: string | null;
  categoryAr: string | null;
}

export interface PurchaseAttachment {
  fileName: string;
  contentType: string;
  bytes: number;
  attachedAtUtc: string;
  attachedBy: string | null;
}

export interface PurchaseInvoice {
  id: string;
  status: PurchaseInvoiceStatus;
  supplierId: string | null;
  // The supplier's name now, archived or not.
  supplierName: string | null;
  supplierInvoiceNumber: string | null;
  invoiceDate: string | null;
  createdAtUtc: string;
  createdBy: string;
  postedAtUtc: string | null;
  postedBy: string | null;
  total: number;
  attachment: PurchaseAttachment | null;
  lines: PurchaseInvoiceLine[];
  // total, split: what went into stock, and what was expenses.
  inventoryTotal: number;
  expenseTotal: number;
  voidedAtUtc?: string | null;
  voidedBy?: string | null;
  paymentMethod: PurchasePaymentMethod | null;
  revenueDate: string | null;
  paymentStatus: InvoicePaymentStatus;
}

export interface PurchaseInvoiceSummary {
  id: string;
  status: PurchaseInvoiceStatus;
  supplierId: string | null;
  // The supplier's name now, archived or not.
  supplierName: string | null;
  supplierInvoiceNumber: string | null;
  invoiceDate: string | null;
  createdAtUtc: string;
  createdBy: string;
  postedAtUtc: string | null;
  postedBy: string | null;
  total: number;
  lines: number;
  // Null while no paper is attached.
  attachmentFileName: string | null;
  inventoryTotal: number;
  expenseTotal: number;
  voidedAtUtc?: string | null;
  voidedBy?: string | null;
  paymentMethod: PurchasePaymentMethod | null;
  revenueDate: string | null;
  paymentStatus: InvoicePaymentStatus;
}

// Maker-checker: what a manager decides. Deletions in every costing module; new and changed prep
// items and production orders too.
export type ApprovalModule = 'Recipe' | 'PurchaseInvoice' | 'Waste' | 'StockCount' | 'RawMaterial' | 'PrepItem' | 'ProductionOrder' | 'Treasury';
export type ApprovalAction = 'Create' | 'Update' | 'Delete';
export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected';
export type ApprovalOutcome = 'Requested' | 'Applied';

export const moduleLabels: Record<ApprovalModule, { en: string; ar: string }> = {
  Recipe: { en: 'Recipe', ar: 'الوصفات' },
  PurchaseInvoice: { en: 'Purchases', ar: 'المشتريات' },
  Waste: { en: 'Waste', ar: 'الهالك' },
  StockCount: { en: 'Stock count', ar: 'الجرد' },
  RawMaterial: { en: 'Raw material', ar: 'الخامات' },
  PrepItem: { en: 'Prep item', ar: 'الأصناف نصف المصنعة' },
  ProductionOrder: { en: 'Production', ar: 'أذونات التصنيع' },
  Treasury: { en: 'Safe', ar: 'الخزنة' },
};

export const actionLabels: Record<ApprovalAction, { en: string; ar: string }> = {
  Create: { en: 'New', ar: 'جديد' },
  Update: { en: 'Change', ar: 'تعديل' },
  Delete: { en: 'Delete', ar: 'حذف' },
};

// One line of what a request changes: "Quantity to produce: 10 kg → 12 kg". before is null for
// something new, after for something taken away.
export interface ApprovalChange {
  label: string;
  before: string | null;
  after: string | null;
}

// summary: the record as it was when asked about (for a new one, what it would be). recordId:
// the record's, or for a new one the id it is given when approved. decidedBy, decidedAtUtc and
// decisionNote are set once a manager has decided.
export interface ApprovalRequest {
  id: string;
  module: ApprovalModule;
  action: ApprovalAction;
  recordId: string;
  summary: string;
  changes: ApprovalChange[];
  reason: string | null;
  requestedBy: string;
  requestedAtUtc: string;
  status: ApprovalStatus;
  decidedBy: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
}

export interface ApprovalRequests {
  pending: ApprovalRequest[];
  decided: ApprovalRequest[];
}

export interface ApprovalResult {
  outcome: ApprovalOutcome;
  request: ApprovalRequest;
}

// What approving a batch would do now: the batch made at today's averages, and for a correction
// the batch it replaces, whose entries go back first.
export interface ApprovalPreview {
  batch: ProductionPreview;
  replaces: ProductionOrder | null;
}

// A save answered with a request (someone who may not approve) rather than the record itself.
export function isRequested<T extends object>(result: T | ApprovalResult): result is ApprovalResult {
  return 'outcome' in result && 'request' in result;
}

// One ingredient of a prep item's batch, costed at its average now: what it adds to a batch, and
// its share of the batch's cost.
export interface PrepIngredientCost {
  rawMaterialId: string;
  code: string | null;
  name: string;
  isPrepItem: boolean;
  unit: UnitOfMeasure;
  purchaseUnit: string;
  purchaseUnitSize: number;
  // The Edible Portion for one batch, in the ingredient's unit; and what that takes off stock.
  quantity: number;
  yieldPercent: number;
  asPurchasedQuantity: number;
  costPerPurchaseUnit: number | null;
  batchCost: number | null;
  sharePercent: number | null;
}

// A prep item: made from a recipe for one batch, kept in stock. Quantities in its unit (grams,
// millilitres); costs per its display unit (a kg, a litre). The standard cost is the recipe's at
// today's prices; the average is what the batches on the shelf cost.
export interface PrepItem {
  id: string;
  code: string | null;
  name: string;
  category: string | null;
  categoryId: string;
  unit: UnitOfMeasure;
  displayUnit: string;
  displayUnitSize: number;
  reorderLevel: number;
  batchQuantity: number;
  ingredients: PrepIngredientCost[];
  standardBatchCost: number | null;
  standardCostPerDisplayUnit: number | null;
  missingPrices: string[];
  quantityOnHand: number;
  averageCostPerDisplayUnit: number | null;
  stockValue: number | null;
  lastProducedAtUtc: string | null;
  lastBatchCostPerDisplayUnit: number | null;
  recent: ProductionOrder[];
}

export interface SavePrepItem {
  name: string;
  unit: UnitOfMeasure;
  batchQuantity: number;
  ingredients: { rawMaterialId: string; quantity: number; yieldPercent: number | null }[];
  code: string | null;
  categoryId: string | null;
  displayUnit: string;
  displayUnitSize: number;
  reorderLevel: number;
  // For a request: made here, so a retry is recorded once; and why, for the manager.
  requestId?: string;
  reason?: string | null;
}

// An ingredient of a batch. onHand and short on a preview only.
export interface ProductionLine {
  rawMaterialId: string;
  code: string | null;
  material: string;
  isPrepItem: boolean;
  unit: UnitOfMeasure;
  purchaseUnit: string;
  purchaseUnitSize: number;
  quantity: number;
  onHand: number | null;
  short: number | null;
  costPerPurchaseUnit: number | null;
  cost: number | null;
}

// A batch made: planned (which set the ingredients), produced (what came out), and what it cost.
export interface ProductionOrder {
  id: string;
  prepItemId: string;
  prepItem: string;
  unit: UnitOfMeasure;
  displayUnit: string;
  displayUnitSize: number;
  plannedQuantity: number;
  producedQuantity: number;
  yieldPercent: number;
  totalCost: number;
  unitCostPerDisplayUnit: number;
  producedAtUtc: string;
  producedBy: string;
  notes: string | null;
  lines: ProductionLine[];
  // Voided: its entries reversed, deleted or corrected. A correction names the batch it replaced
  // (revisionOf), and the replaced one names it (replacedBy).
  status: 'Made' | 'Voided';
  voidedAtUtc: string | null;
  voidedBy: string | null;
  revisionOf: string | null;
  replacedBy: string | null;
  // The manager who approved it, when it was asked of one.
  approvedBy: string | null;
}

// A batch as it would be made now: the review before posting.
export interface ProductionPreview {
  prepItemId: string;
  prepItem: string;
  unit: UnitOfMeasure;
  displayUnit: string;
  displayUnitSize: number;
  plannedQuantity: number;
  producedQuantity: number;
  lines: ProductionLine[];
  totalCost: number | null;
  unitCostPerDisplayUnit: number | null;
  missingPrices: string[];
  shortages: string[];
}

export interface MakeProductionOrder {
  prepItemId: string;
  plannedQuantity: number;
  producedQuantity: number;
  notes: string | null;
  requestId?: string;
  reason?: string | null;
}

// A batch corrected: made again as newOrderId with these quantities, the old one reversed.
export interface ReviseProductionOrder {
  requestId: string;
  newOrderId: string;
  plannedQuantity: number;
  producedQuantity: number;
  notes: string | null;
  reason: string | null;
}

// Who the restaurant buys from. Invoices, drafts included, decide what deleting it does: on
// none it goes, on any it is archived.
export interface Supplier {
  id: string;
  name: string;
  contactPerson: string | null;
  phone: string | null;
  email: string | null;
  notes: string | null;
  isArchived: boolean;
  invoices: number;
  postedTotal: number;
  lastPostedAtUtc: string | null;
}

export interface SaveSupplier {
  name: string;
  contactPerson: string | null;
  phone: string | null;
  email: string | null;
  notes: string | null;
}

export type SupplierRemoval = 'Deleted' | 'Archived';

export interface SavePurchaseInvoice {
  supplierId: string | null;
  supplierInvoiceNumber: string | null;
  invoiceDate: string | null;
  lines: SavePurchaseLine[];
  // Required to post: from the safe, from a day's takings (revenueDate), or on credit (a supplier).
  paymentMethod: PurchasePaymentMethod | null;
  revenueDate: string | null;
}

// A stock line names its material; an expense line its expense item, in units of 1, with its cost.
export interface SavePurchaseLine {
  itemType: PurchaseItemType;
  rawMaterialId: string | null;
  expenseItemId: string | null;
  enteredQuantity: number;
  unitLabel: string | null;
  unitSize: number;
  cost: number | null;
}

// Where an expense filed under a category counts in the profit and loss.
export type ExpenseLine = 'Controllable' | 'Occupation' | 'OtherFixed' | 'Wages';

export const expenseLineLabels: Record<ExpenseLine, { en: string; ar: string; hint: string }> = {
  Controllable: { en: 'Controllable costs', ar: 'تكاليف قابلة للتحكم', hint: 'Utilities, cleaning, maintenance, marketing' },
  Occupation: { en: 'Occupation (rent)', ar: 'الإشغال (الإيجار)', hint: 'Rent and what comes with the premises' },
  OtherFixed: { en: 'Other fixed costs', ar: 'تكاليف ثابتة أخرى', hint: 'Insurance, licences, subscriptions' },
  Wages: { en: 'Wages and benefits', ar: 'الأجور والمزايا', hint: 'Salaries paid by voucher: added to the month’s wages' },
};

// What the restaurant buys, grouped: raw materials and expense items are each in one. General is
// always there and cannot be deleted.
export interface PurchaseCategory {
  id: string;
  name: string;
  nameAr: string | null;
  expenseLine: ExpenseLine;
  isGeneral: boolean;
  materials: number;
  expenseItems: number;
}

export interface SavePurchaseCategory {
  name: string;
  nameAr: string | null;
  expenseLine: ExpenseLine;
}

// Bought but never stock: only a cost, under its category.
export interface ExpenseItem {
  id: string;
  name: string;
  nameAr: string | null;
  categoryId: string;
  category: string;
  categoryAr: string | null;
  unit: string | null;
  isArchived: boolean;
  invoices: number;
  postedTotal: number;
  lastPostedAtUtc: string | null;
}

export interface SaveExpenseItem {
  name: string;
  nameAr: string | null;
  categoryId: string | null;
  unit: string | null;
}

export type ExpenseItemRemoval = 'Deleted' | 'Archived';

export interface ItemSpending {
  itemType: PurchaseItemType;
  itemId: string;
  name: string;
  quantity: number;
  unit: string;
  total: number;
  lines: number;
}

export interface CategorySpending {
  categoryId: string;
  category: string;
  categoryAr: string | null;
  expenseLine: ExpenseLine;
  inventory: number;
  expenses: number;
  total: number;
  sharePercent: number | null;
  items: ItemSpending[];
}

// Purchases by category over a period, from the posted invoices, each on its invoice date.
export interface SpendingReport {
  from: string;
  to: string;
  total: number;
  inventory: number;
  expenses: number;
  invoices: number;
  unpricedLines: number;
  categories: CategorySpending[];
}

@Injectable({ providedIn: 'root' })
export class CostingApi {
  private readonly http = inject(HttpClient);

  materials(): Observable<MaterialCost[]> {
    return this.http.get<MaterialCost[]>('/api/costing/materials');
  }

  recipeCategories(): Observable<RecipeCategory[]> {
    return this.http.get<RecipeCategory[]>('/api/inventory/recipe-categories');
  }

  // Adds it on the first save, under the id made here; renames it after.
  saveRecipeCategory(id: string, category: { name: string; nameAr: string | null }): Observable<RecipeCategory> {
    return this.http.put<RecipeCategory>(`/api/inventory/recipe-categories/${id}`, category);
  }

  // moveTo: where its recipes go; required while it has any.
  deleteRecipeCategory(id: string, moveTo: string | null): Observable<void> {
    return this.http.delete<void>(`/api/inventory/recipe-categories/${id}`, { params: moveTo ? { moveTo } : {} });
  }

  // Every recipe, on the menu or off it; the deleted ones too when asked for.
  recipes(includeDeleted = false): Observable<RecipeCostCard[]> {
    return this.http.get<RecipeCostCard[]>('/api/costing/recipes', { params: { includeDeleted } });
  }

  recipeCardById(recipeId: string): Observable<RecipeCostCard> {
    return this.http.get<RecipeCostCard>(`/api/costing/recipes/${recipeId}`);
  }

  // Adds it without an id, changes it with one.
  writeRecipe(recipeId: string | null, recipe: SaveRecipe): Observable<{ id: string }> {
    return recipeId
      ? this.http.put<{ id: string }>(`/api/inventory/recipes/${recipeId}`, recipe)
      : this.http.post<{ id: string }>('/api/inventory/recipes', recipe);
  }

  // Marked deleted, kept for the record.
  deleteRecipe(recipeId: string): Observable<void> {
    return this.http.delete<void>(`/api/inventory/recipes/${recipeId}`);
  }

  // Deleted at once for someone who may approve; asked of a manager otherwise. The
  // id is made here: a retry after a lost answer is recorded once.
  requestDeletion(requestId: string, module: ApprovalModule, recordId: string, reason: string | null): Observable<ApprovalResult> {
    return this.http.post<ApprovalResult>('/api/inventory/deletion-requests', { requestId, module, recordId, reason });
  }

  // What waits for a manager; with decided, the last decisions too (for whoever decides them).
  approvalRequests(decided = 0): Observable<ApprovalRequests> {
    return this.http.get<ApprovalRequests>('/api/inventory/approval-requests', { params: { decided } });
  }

  approveRequest(requestId: string): Observable<ApprovalRequest> {
    return this.http.post<ApprovalRequest>(`/api/inventory/approval-requests/${requestId}/approve`, {});
  }

  rejectRequest(requestId: string, note: string | null): Observable<ApprovalRequest> {
    return this.http.post<ApprovalRequest>(`/api/inventory/approval-requests/${requestId}/reject`, { note });
  }

  // What approving a batch waiting for it would move now, at today's averages; for a correction,
  // with the batch it replaces.
  approvalPreview(requestId: string): Observable<ApprovalPreview> {
    return this.http.get<ApprovalPreview>(`/api/inventory/approval-requests/${requestId}/preview`);
  }

  // name: what to call it if it comes back off the menu, its menu item having another recipe now.
  restoreRecipe(recipeId: string, name: string | null): Observable<{ id: string; catalogItemId: number | null }> {
    return this.http.post<{ id: string; catalogItemId: number | null }>(`/api/inventory/recipes/${recipeId}/restore`, { name });
  }

  menu(): Observable<RecipeCostCard[]> {
    return this.http.get<RecipeCostCard[]>('/api/costing/menu');
  }

  recipeCard(kind: RecipeTargetKind, catalogItemId: number, variantId: number | null): Observable<RecipeCostCard> {
    return this.http.get<RecipeCostCard>('/api/costing/recipe-card', { params: this.target(kind, catalogItemId, variantId) });
  }

  theoretical(year: number, month: number, source: TheoreticalSource = 'Pos'): Observable<TheoreticalCostReport> {
    return this.http.get<TheoreticalCostReport>('/api/costing/theoretical', { params: { year, month, source } });
  }

  manualSales(year: number, month: number): Observable<ManualSalesSheet> {
    return this.http.get<ManualSalesSheet>(`/api/costing/manual-sales/${year}/${month}`);
  }

  // The whole sheet: what is left out is cleared. Answers with the report from it.
  saveManualSales(year: number, month: number, lines: ManualSalesLine[]): Observable<TheoreticalCostReport> {
    return this.http.put<TheoreticalCostReport>(`/api/costing/manual-sales/${year}/${month}`, { lines });
  }

  settings(): Observable<CostingSettings> {
    return this.http.get<CostingSettings>('/api/costing/settings');
  }

  saveSettings(settings: CostingSettings): Observable<CostingSettings> {
    return this.http.put<CostingSettings>('/api/costing/settings', settings);
  }

  sharedCosts(): Observable<SharedCost[]> {
    return this.http.get<SharedCost[]>('/api/costing/shared-costs');
  }

  saveSharedCost(id: string | null, cost: SaveSharedCost): Observable<SharedCost> {
    return id
      ? this.http.put<SharedCost>(`/api/costing/shared-costs/${id}`, cost)
      : this.http.post<SharedCost>('/api/costing/shared-costs', cost);
  }

  deleteSharedCost(id: string): Observable<void> {
    return this.http.delete<void>(`/api/costing/shared-costs/${id}`);
  }

  purchaseCategories(): Observable<PurchaseCategory[]> {
    return this.http.get<PurchaseCategory[]>('/api/inventory/purchase-categories');
  }

  // Adds it on the first save, under the id made here; changes it after.
  savePurchaseCategory(id: string, category: SavePurchaseCategory): Observable<PurchaseCategory> {
    return this.http.put<PurchaseCategory>(`/api/inventory/purchase-categories/${id}`, category);
  }

  // moveTo: where what is in it goes, needed while it holds anything.
  deletePurchaseCategory(id: string, moveTo: string | null): Observable<void> {
    return this.http.delete<void>(`/api/inventory/purchase-categories/${id}`, { params: moveTo ? { moveTo } : {} });
  }

  expenseItems(includeArchived = false): Observable<ExpenseItem[]> {
    return this.http.get<ExpenseItem[]>('/api/inventory/expense-items', { params: { includeArchived } });
  }

  saveExpenseItem(id: string, item: SaveExpenseItem): Observable<ExpenseItem> {
    return this.http.put<ExpenseItem>(`/api/inventory/expense-items/${id}`, item);
  }

  // Deleted if it is on no invoice, archived if it is.
  deleteExpenseItem(id: string): Observable<{ outcome: ExpenseItemRemoval }> {
    return this.http.delete<{ outcome: ExpenseItemRemoval }>(`/api/inventory/expense-items/${id}`);
  }

  restoreExpenseItem(id: string): Observable<ExpenseItem> {
    return this.http.post<ExpenseItem>(`/api/inventory/expense-items/${id}/restore`, {});
  }

  // Both days included, as yyyy-mm-dd.
  spending(from: string, to: string): Observable<SpendingReport> {
    return this.http.get<SpendingReport>('/api/costing/purchases/spending', { params: { from, to } });
  }

  // The Excel template to fill in: the columns, and the unit and category lists.
  rawMaterialsTemplate(): Observable<Blob> {
    return this.http.get('/api/inventory/raw-materials/template', { responseType: 'blob' });
  }

  importRawMaterials(file: File): Observable<RawMaterialImportResult> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<RawMaterialImportResult>('/api/inventory/raw-materials/import', form);
  }

  saveMaterial(id: string | null, material: SaveMaterial): Observable<unknown> {
    return id
      ? this.http.put(`/api/inventory/raw-materials/${id}`, material)
      : this.http.post('/api/inventory/raw-materials', material);
  }

  kpis(year: number, month: number): Observable<KpiReport> {
    return this.http.get<KpiReport>('/api/costing/kpis', { params: { year, month } });
  }

  // The whole month each time: a figure left empty is not known yet.
  saveExpenses(year: number, month: number, expenses: MonthlyExpenses): Observable<MonthlyExpenses> {
    return this.http.put<MonthlyExpenses>(`/api/costing/expenses/${year}/${month}`, expenses);
  }

  // A run of months, inclusive; one month is from = to. Months are 'YYYY-MM'.
  breakEven(from: string, to: string): Observable<BreakEvenWorksheet> {
    const [fromYear, fromMonth] = from.split('-').map(Number);
    const [toYear, toMonth] = to.split('-').map(Number);
    return this.http.get<BreakEvenWorksheet>('/api/costing/break-even', { params: { fromYear, fromMonth, toYear, toMonth } });
  }

  saveBreakEvenScenarios(scenarios: number[]): Observable<number[]> {
    return this.http.put<number[]>('/api/costing/break-even/scenarios', scenarios);
  }

  stockCounts(): Observable<StockCountSummary[]> {
    return this.http.get<StockCountSummary[]>('/api/inventory/stock-counts');
  }

  // Posted invoices for the history, newest first, or the drafts.
  purchaseInvoices(status: PurchaseInvoiceStatus): Observable<PurchaseInvoiceSummary[]> {
    return this.http.get<PurchaseInvoiceSummary[]>('/api/inventory/purchase-invoices', { params: { status } });
  }

  // Null for an invoice not saved yet (404, and only 404).
  purchaseInvoice(id: string): Observable<PurchaseInvoice | null> {
    return this.http.get<PurchaseInvoice>(`/api/inventory/purchase-invoices/${id}`).pipe(
      catchError((error: unknown) => (error instanceof HttpErrorResponse && error.status === 404 ? of(null) : throwError(() => error))),
    );
  }

  // Step one: kept as a draft. The id is made here, once per invoice.
  savePurchaseInvoice(id: string, invoice: SavePurchaseInvoice): Observable<PurchaseInvoice> {
    return this.http.put<PurchaseInvoice>(`/api/inventory/purchase-invoices/${id}`, invoice);
  }

  // Step two, after the review: the stock goes in, and it is paid as chosen. paymentStatus says
  // whether a cash payment waits for a manager.
  postPurchaseInvoice(id: string): Observable<PurchaseInvoice> {
    return this.http.post<PurchaseInvoice>(`/api/inventory/purchase-invoices/${id}/post`, {});
  }

  // Pays a posted cash invoice that is not paid (its payment rejected or voided), or puts it on credit.
  payPurchaseInvoice(id: string, requestId: string, method: PurchasePaymentMethod, revenueDate: string | null): Observable<{ paymentStatus: InvoicePaymentStatus }> {
    return this.http.post<{ paymentStatus: InvoicePaymentStatus }>(`/api/inventory/purchase-invoices/${id}/pay`, { requestId, method, revenueDate });
  }

  // ---- Financials (الماليات): the safe and what is owed to suppliers ----

  // The till's business day (2026-10-12): after midnight, yesterday's until the day starts.
  businessToday(): Observable<{ today: string }> {
    return this.http.get<{ today: string }>('/api/financials/today');
  }

  safe(year: number, month: number): Observable<SafeMonth> {
    return this.http.get<SafeMonth>('/api/financials/safe', { params: { year, month } });
  }

  funding(from: string, to: string): Observable<FundingReport> {
    return this.http.get<FundingReport>('/api/financials/funding', { params: { from, to } });
  }

  dayRevenue(from: string, to: string): Observable<DayRevenueReport> {
    return this.http.get<DayRevenueReport>('/api/financials/revenue', { params: { from, to } });
  }

  reconciliation(from: string, to: string): Observable<ReconciliationReport> {
    return this.http.get<ReconciliationReport>('/api/financials/reconciliation', { params: { from, to } });
  }

  financialSettings(): Observable<FinancialSettings> {
    return this.http.get<FinancialSettings>('/api/financials/settings');
  }

  // A manager's only.
  saveFinancialSettings(servicePercent: number): Observable<FinancialSettings> {
    return this.http.put<FinancialSettings>('/api/financials/settings', { servicePercent });
  }

  expenseVouchers(from: string, to: string): Observable<ExpenseVoucherReport> {
    return this.http.get<ExpenseVoucherReport>('/api/financials/expense-vouchers', { params: { from, to } });
  }

  supplierBalances(): Observable<SupplierBalance[]> {
    return this.http.get<SupplierBalance[]>('/api/financials/suppliers');
  }

  supplierStatement(supplierId: string): Observable<SupplierStatement> {
    return this.http.get<SupplierStatement>(`/api/financials/suppliers/${supplierId}`);
  }

  supplierPayments(from: string, to: string): Observable<SupplierPaymentRow[]> {
    return this.http.get<SupplierPaymentRow[]>('/api/financials/supplier-payments', { params: { from, to } });
  }

  // What a supplier was owed before the treasury: no cash moves, so it is entered at once.
  supplierOpeningBalance(supplierId: string, entryId: string, owed: number, date: string | null): Observable<void> {
    return this.http.post<void>(`/api/financials/suppliers/${supplierId}/opening-balance`, { entryId, owed, date });
  }

  // A safe entry: made at once for a manager; a request for anyone else.
  recordVoucher(voucher: SafeVoucherInput): Observable<SafeTransaction | ApprovalResult> {
    return this.http.post<SafeTransaction | ApprovalResult>('/api/financials/vouchers', voucher);
  }

  discardPurchaseInvoice(id: string): Observable<void> {
    return this.http.delete<void>(`/api/inventory/purchase-invoices/${id}`);
  }

  attachPurchaseInvoiceFile(id: string, file: File): Observable<PurchaseInvoice> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.put<PurchaseInvoice>(`/api/inventory/purchase-invoices/${id}/attachment`, form);
  }

  // The paper itself, fetched with the manager's sign-in, to show or save.
  purchaseInvoiceFile(id: string): Observable<Blob> {
    return this.http.get(`/api/inventory/purchase-invoices/${id}/attachment`, { responseType: 'blob' });
  }

  importPrepItems(file: File): Observable<PrepImportResult> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<PrepImportResult>('/api/inventory/prep-items/import', form);
  }

  prepItems(): Observable<PrepItem[]> {
    return this.http.get<PrepItem[]>('/api/inventory/prep-items');
  }

  // Null for one that is not there (404, and only 404).
  prepItem(id: string): Observable<PrepItem | null> {
    return this.http.get<PrepItem>(`/api/inventory/prep-items/${id}`).pipe(
      catchError((error: unknown) => (error instanceof HttpErrorResponse && error.status === 404 ? of(null) : throwError(() => error))),
    );
  }

  // Adds it without an id; changes it with one. A request, for someone who may not approve.
  savePrepItem(id: string | null, item: SavePrepItem): Observable<PrepItem | ApprovalResult> {
    return id
      ? this.http.put<PrepItem | ApprovalResult>(`/api/inventory/prep-items/${id}`, item)
      : this.http.post<PrepItem | ApprovalResult>('/api/inventory/prep-items', item);
  }

  previewProduction(prepItemId: string, planned: number, produced: number): Observable<ProductionPreview> {
    return this.http.get<ProductionPreview>('/api/inventory/production-orders/preview', { params: { prepItemId, planned, produced } });
  }

  productionOrders(prepItemId?: string): Observable<ProductionOrder[]> {
    return this.http.get<ProductionOrder[]>('/api/inventory/production-orders', { params: prepItemId ? { prepItemId } : {} });
  }

  productionOrder(id: string): Observable<ProductionOrder | null> {
    return this.http.get<ProductionOrder>(`/api/inventory/production-orders/${id}`).pipe(
      catchError((error: unknown) => (error instanceof HttpErrorResponse && error.status === 404 ? of(null) : throwError(() => error))),
    );
  }

  // Makes the batch under the id made here: a retry after a lost answer makes it once. A
  // request, moving nothing yet, for someone who may not approve.
  makeProductionOrder(id: string, order: MakeProductionOrder): Observable<ProductionOrder | ApprovalResult> {
    return this.http.put<ProductionOrder | ApprovalResult>(`/api/inventory/production-orders/${id}`, order);
  }

  reviseProductionOrder(id: string, revision: ReviseProductionOrder): Observable<ApprovalResult> {
    return this.http.post<ApprovalResult>(`/api/inventory/production-orders/${id}/revision`, revision);
  }

  suppliers(includeArchived = false): Observable<Supplier[]> {
    return this.http.get<Supplier[]>('/api/inventory/suppliers', { params: { includeArchived } });
  }

  // Adds it on the first save, under the id made here; changes it after.
  saveSupplier(id: string, supplier: SaveSupplier): Observable<Supplier> {
    return this.http.put<Supplier>(`/api/inventory/suppliers/${id}`, supplier);
  }

  deleteSupplier(id: string): Observable<{ outcome: SupplierRemoval }> {
    return this.http.delete<{ outcome: SupplierRemoval }>(`/api/inventory/suppliers/${id}`);
  }

  restoreSupplier(id: string): Observable<Supplier> {
    return this.http.post<Supplier>(`/api/inventory/suppliers/${id}/restore`, {});
  }

  // Null for a count not saved yet (404, and only 404).
  stockCount(id: string): Observable<StockCount | null> {
    return this.http.get<StockCount>(`/api/inventory/stock-counts/${id}`).pipe(
      catchError((error: unknown) => (error instanceof HttpErrorResponse && error.status === 404 ? of(null) : throwError(() => error))),
    );
  }

  // The whole list each time: a material left out is not counted.
  saveStockCount(id: string, lines: { rawMaterialId: string; countedQuantity: number }[]): Observable<StockCount> {
    return this.http.put<StockCount>(`/api/inventory/stock-counts/${id}`, { lines });
  }

  postStockCount(id: string): Observable<StockCount> {
    return this.http.post<StockCount>(`/api/inventory/stock-counts/${id}/post`, {});
  }

  discardStockCount(id: string): Observable<void> {
    return this.http.delete<void>(`/api/inventory/stock-counts/${id}`);
  }

  // spoilageId is made here, once per entry, as purchaseId is.
  recordSpoilage(spoilageId: string, lines: SpoilageLine[]): Observable<unknown> {
    return this.http.post('/api/inventory/spoilage', { spoilageId, lines });
  }

  spoilage(days = 30): Observable<SpoilageEntry[]> {
    return this.http.get<SpoilageEntry[]>('/api/costing/spoilage', { params: { days } });
  }

  // No ids: the last two posted counts.
  variance(fromCountId: string | null, toCountId: string | null): Observable<VarianceReport> {
    const params: Record<string, string> = {};
    if (fromCountId) {
      params['fromCountId'] = fromCountId;
    }
    if (toCountId) {
      params['toCountId'] = toCountId;
    }
    return this.http.get<VarianceReport>('/api/costing/variance', { params });
  }

  private target(kind: RecipeTargetKind, catalogItemId: number, variantId: number | null): Record<string, string | number> {
    return variantId === null ? { targetKind: kind, catalogItemId } : { targetKind: kind, catalogItemId, variantId };
  }
}

// Labels, in English and Arabic, as the reports carry both.
export const statusLabels: Record<CostStatus, { en: string; ar: string }> = {
  WithinTarget: { en: 'Within target', ar: 'ضمن الهدف' },
  AboveTarget: { en: 'Above target', ar: 'فوق الهدف' },
  NoRecipe: { en: 'No recipe', ar: 'لا توجد وصفة' },
  MissingPrices: { en: 'Missing prices', ar: 'أسعار ناقصة' },
  NoPrice: { en: 'No price', ar: 'بلا سعر' },
};

export const unitLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

// Raw materials brought in from the Excel template: each made a purchased stock item, or skipped
// and said why, by its row on the sheet; and the categories added for them.
export interface ImportedRawMaterial {
  row: number;
  id: string;
  name: string;
  category: string;
  unit: UnitOfMeasure;
  purchaseUnit: string;
  costPerPurchaseUnit: number | null;
}

export interface SkippedSheetRow {
  row: number;
  name: string | null;
  reason: string;
}

export interface RawMaterialImportResult {
  created: ImportedRawMaterial[];
  skipped: SkippedSheetRow[];
  categoriesAdded: string[];
}

// Prep items brought in from a sheet of batch recipes (المصنعات): each made, or asked of a manager
// (requested), its batch in grams and what to check on it; the rows skipped and why; the raw
// materials added for the recipes, and the category added for the prep items.
export interface ImportedPrepItem {
  row: number;
  id: string;
  name: string;
  batchQuantity: number;
  ingredients: number;
  requested: boolean;
  notes: string[];
}

export interface PrepImportResult {
  imported: ImportedPrepItem[];
  skipped: SkippedSheetRow[];
  materialsAdded: { id: string; name: string; unit: UnitOfMeasure }[];
  categoryAdded: string | null;
}

// Stock is counted and reported in kg, litres and pieces, whatever it is bought in: a count of
// 3.25 kg of cheese, not 0.13 of a 25 kg sack. What is bought by the gram or millilitre, such as
// saffron, is counted so. The ledger keeps grams, millilitres and pieces.
export function countUnit(unit: UnitOfMeasure, purchaseUnitSize: number): { label: string; size: number } {
  switch (unit) {
    case 'Gram':
      return purchaseUnitSize < 1000 ? { label: 'g', size: 1 } : { label: 'kg', size: 1000 };
    case 'Millilitre':
      return purchaseUnitSize < 1000 ? { label: 'ml', size: 1 } : { label: 'L', size: 1000 };
    default:
      return { label: 'pc', size: 1 };
  }
}

// The units a delivery of this material can be entered in, each with how many of its own units
// (grams, millilitres, pieces) one holds: what it is bought by first, then the kilogram or litre,
// then the gram, millilitre or piece itself. 5 kg is 5,000 g whichever is chosen.
export function purchaseUnits(material: MaterialCost): { label: string; size: number }[] {
  const units = [
    { label: material.purchaseUnit, size: material.purchaseUnitSize },
    ...(material.unit === 'Gram' ? [{ label: 'kg', size: 1000 }, { label: 'g', size: 1 }] : []),
    ...(material.unit === 'Millilitre' ? [{ label: 'L', size: 1000 }, { label: 'ml', size: 1 }] : []),
    ...(material.unit === 'Piece' ? [{ label: 'pc', size: 1 }] : []),
  ];
  return units.filter((u, i) => units.findIndex((other) => other.size === u.size) === i);
}

export const evaluationLabels: Record<VarianceEvaluation, { en: string; ar: string }> = {
  Unfavourable: { en: 'Unfavourable', ar: 'غير مواتٍ' },
  Favourable: { en: 'Favourable', ar: 'مواتٍ' },
  WithinLimit: { en: 'Within limit', ar: 'ضمن الحد' },
  SharedCost: { en: 'Shared cost', ar: 'تكلفة مشتركة' },
};

// The template's indicators: name, formula and purpose in English and Arabic, as it writes them.
export const kpiLabels: Record<KpiKey, { en: string; ar: string; formula: string; formulaAr: string; purpose: string; money: boolean }> = {
  FoodCost: {
    en: 'Food cost %',
    ar: 'نسبة تكلفة الطعام',
    formula: 'Cost of raw materials consumed ÷ food sales × 100',
    formulaAr: '(تكلفة المواد الخام المستهلكة ÷ إجمالي مبيعات الطعام) × 100',
    purpose: 'How well food is bought and prepared',
    money: false,
  },
  BeverageCost: {
    en: 'Beverage cost %',
    ar: 'نسبة تكلفة المشروبات',
    formula: 'Cost of beverages sold ÷ beverage revenue × 100',
    formulaAr: '(تكلفة المشروبات المباعة ÷ إيرادات المشروبات) × 100',
    purpose: 'How well drinks are managed',
    money: false,
  },
  LabourCost: {
    en: 'Labour cost %',
    ar: 'نسبة تكلفة العمالة',
    formula: 'Wages and benefits ÷ revenue × 100',
    formulaAr: '(إجمالي الرواتب والمزايا ÷ إجمالي الإيرادات) × 100',
    purpose: 'Staffing against revenue',
    money: false,
  },
  AverageCheck: {
    en: 'Average check',
    ar: 'متوسط قيمة الفاتورة',
    formula: 'Revenue ÷ paid orders',
    formulaAr: 'إجمالي الإيرادات ÷ عدد الفواتير',
    purpose: 'What a customer spends on average',
    money: true,
  },
  SalesPerLabourHour: {
    en: 'Sales per labour hour',
    ar: 'الإيراد لكل ساعة عمل',
    formula: 'Revenue ÷ labour hours',
    formulaAr: 'إجمالي المبيعات ÷ إجمالي ساعات العمل',
    purpose: 'How productive the staff are',
    money: true,
  },
  GrossProfitMargin: {
    en: 'Gross profit margin',
    ar: 'هامش الربح الإجمالي',
    formula: 'Gross profit ÷ revenue × 100',
    formulaAr: '(إجمالي الربح ÷ إجمالي الإيرادات) × 100',
    purpose: 'Profit after the cost of sales',
    money: false,
  },
  NetProfitMargin: {
    en: 'Net profit margin',
    ar: 'هامش الربح الصافي',
    formula: 'Net profit ÷ revenue × 100',
    formulaAr: '(صافي الربح ÷ إجمالي الإيرادات) × 100',
    purpose: 'Profit after everything',
    money: false,
  },
};

// ---- Financials (الماليات) ----

// How an invoice is paid: from the safe, from one day's cash takings, or on credit (owed to its
// supplier). BeforeTreasury: posted before the treasury was kept.
export type PurchasePaymentMethod = 'Safe' | 'DayRevenue' | 'Credit' | 'BeforeTreasury';
export type InvoicePaymentStatus = 'NotPosted' | 'Paid' | 'Pending' | 'Unpaid' | 'OnCredit' | 'BeforeTreasury';

export const paymentMethodLabels: Record<PurchasePaymentMethod, { en: string; ar: string }> = {
  Safe: { en: 'Paid from the safe', ar: 'خصم من الخزنة' },
  DayRevenue: { en: 'Paid from a day’s takings', ar: 'خصم من إيراد يوم' },
  Credit: { en: 'On credit', ar: 'آجل' },
  BeforeTreasury: { en: 'Before the treasury', ar: 'قبل الخزنة' },
};

export const paymentStatusLabels: Record<InvoicePaymentStatus, { en: string; ar: string }> = {
  NotPosted: { en: 'Draft', ar: 'مسودة' },
  Paid: { en: 'Paid', ar: 'مدفوعة' },
  Pending: { en: 'Payment waiting for approval', ar: 'الدفع بانتظار الموافقة' },
  Unpaid: { en: 'Unpaid', ar: 'غير مدفوعة' },
  OnCredit: { en: 'On credit', ar: 'آجل' },
  BeforeTreasury: { en: 'Before the treasury', ar: 'قبل الخزنة' },
};

export type SafeTransactionKind = 'OpeningBalance' | 'Funding' | 'RevenueTransfer' | 'ExpenseVoucher' | 'SupplierPayment' | 'InvoicePayment';
export type CashFund = 'Safe' | 'DayRevenue';
// Where money put into the safe came from (مصدر التمويل).
export type FundingSource = 'CarriedOver' | 'OwnerFinancing' | 'Other';

export const fundingSourceLabels: Record<FundingSource, { en: string; ar: string }> = {
  CarriedOver: { en: 'Carried over', ar: 'رصيد مرحل' },
  OwnerFinancing: { en: 'Owners’ financing', ar: 'تمويل جاري / جاري الشركاء' },
  Other: { en: 'Other source', ar: 'مصدر آخر' },
};

export const safeKindLabels: Record<SafeTransactionKind, { en: string; ar: string }> = {
  OpeningBalance: { en: 'Opening balance', ar: 'رصيد افتتاحي' },
  Funding: { en: 'Funding', ar: 'تمويل' },
  RevenueTransfer: { en: 'Takings in', ar: 'توريد إيراد' },
  ExpenseVoucher: { en: 'Expense voucher', ar: 'إذن صرف مصروفات' },
  SupplierPayment: { en: 'Supplier payment', ar: 'إذن صرف لمورد' },
  InvoicePayment: { en: 'Invoice paid', ar: 'سداد فاتورة' },
};

// One line of the safe's ledger: in or out, and the balance after it (null where not running).
// A reversal names what it undid (reversalOf); what it undid is marked voided.
export interface SafeEntry {
  id: string;
  kind: SafeTransactionKind;
  fund: CashFund;
  date: string;
  description: string;
  in: number;
  out: number;
  balance: number | null;
  source: FundingSource | null;
  revenueDate: string | null;
  supplierId: string | null;
  purchaseInvoiceId: string | null;
  expenseItemId: string | null;
  notes: string | null;
  recordedBy: string;
  recordedAtUtc: string;
  approvedBy: string | null;
  reversalOf: string | null;
  voidedAtUtc: string | null;
  voidedBy: string | null;
  // A day's takings entered with the tax in them: the rate, and the tax (signed as in and out are).
  includesTax: boolean;
  taxPercent: number | null;
  tax: number;
}

// How a month's opening balance came about: carried over from the month before, the treasury's
// own opening entry made this month, or none yet.
export type SafeOpening = 'None' | 'CarriedOver' | 'OpeningEntry';

export interface SafeMonth {
  year: number;
  month: number;
  from: string;
  to: string;
  openingBalance: number;
  openingKind: SafeOpening;
  openingSource: FundingSource | null;
  in: number;
  out: number;
  closingBalance: number;
  balanceNow: number;
  hasOpeningBalance: boolean;
  entries: SafeEntry[];
}

export interface FundingReport {
  from: string;
  to: string;
  carriedOver: number;
  ownerFinancing: number;
  other: number;
  revenue: number;
  total: number;
  entries: SafeEntry[];
}

// One business day's cash: taken, paid straight out of it, moved into the safe, and what is left.
export interface DayRevenue {
  date: string;
  cashSales: number;
  paidOut: number;
  transferred: number;
  left: number;
}

export interface DayRevenueReport {
  from: string;
  to: string;
  cashSales: number;
  paidOut: number;
  transferred: number;
  left: number;
  days: DayRevenue[];
}

export interface ExpenseVoucherRow {
  id: string;
  date: string;
  expenseItemId: string;
  expenseItem: string;
  category: string | null;
  amount: number;
  notes: string | null;
  recordedBy: string;
  approvedBy: string | null;
  voidedAtUtc: string | null;
  voidedBy: string | null;
}

export interface ExpenseVoucherReport {
  from: string;
  to: string;
  total: number;
  vouchers: ExpenseVoucherRow[];
}

// What is owed to one supplier, and how it came about.
export interface SupplierBalance {
  supplierId: string;
  supplier: string;
  isArchived: boolean;
  openingDebt: number;
  creditPurchases: number;
  payments: number;
  balance: number;
  lastPaidOn: string | null;
  openInvoices: number;
}

export type SupplierLedgerKind = 'OpeningBalance' | 'CreditInvoice' | 'Payment';

// A line of a supplier's statement (كشف حساب): owed adds to the debt, paid takes from it.
export interface SupplierStatementLine {
  id: string;
  date: string;
  kind: SupplierLedgerKind;
  description: string;
  owed: number;
  paid: number;
  balance: number;
  sourceId: string;
  isReversal: boolean;
}

// A credit invoice not fully paid: payments go to the opening debt first, then the oldest invoice.
export interface OpenInvoice {
  invoiceId: string;
  supplierInvoiceNumber: string | null;
  date: string;
  total: number;
  paid: number;
  remaining: number;
  status: 'Unpaid' | 'PartlyPaid';
}

export interface SupplierStatement {
  supplierId: string;
  supplier: string;
  openingDebt: number;
  creditPurchases: number;
  payments: number;
  balance: number;
  lines: SupplierStatementLine[];
  openInvoices: OpenInvoice[];
}

export interface SupplierPaymentRow {
  id: string;
  date: string;
  supplierId: string;
  supplier: string;
  amount: number;
  notes: string | null;
  recordedBy: string;
  approvedBy: string | null;
  voidedAtUtc: string | null;
  voidedBy: string | null;
}

// A safe entry as entered. requestId and transactionId are made here, once per entry.
export interface SafeVoucherInput {
  requestId: string;
  transactionId: string;
  kind: SafeTransactionKind;
  amount: number;
  date: string | null;
  source: FundingSource | null;
  revenueDate: string | null;
  supplierId: string | null;
  expenseItemId: string | null;
  notes: string | null;
  reason: string | null;
  // A day's takings counted with the tax in them: the server takes it off at the till's rate.
  includesTax: boolean;
}

export interface SafeTransaction {
  id: string;
  kind: SafeTransactionKind;
  fund: CashFund;
  amount: number;
  businessDate: string;
  recordedBy: string;
  approvedBy: string | null;
  includesTax: boolean;
  taxPercent: number | null;
  taxAmount: number;
}

// Tax and service (إعدادات الضريبة والخدمة): the till's tax rate, as the delivery system set it
// (null until it has synced), and the service charge, a manager's to set.
export interface FinancialSettings {
  taxPercent: number | null;
  servicePercent: number;
  updatedBy: string | null;
  updatedAtUtc: string | null;
}

// One business day of the reconciliation (مطابقة الإيرادات), or the period's total (date null). The
// cash before tax (A, netCash) against the cash the sales should have brought in (expectedCash):
// every item at its price (B, itemSales), less discounts and points, plus delivery fees, less
// refunds, less what was paid by card or online, all before tax. Difference: A less that; below
// zero cash is missing, above it cash came in for items not rung up.
export interface ReconciliationDay {
  date: string | null;
  takingsEntered: number;
  paidOut: number;
  tax: number;
  netCash: number;
  itemSales: number;
  discounts: number;
  points: number;
  deliveryFees: number;
  refunds: number;
  cardAndOnline: number;
  expectedCash: number;
  difference: number;
}

export interface ReconciliationReport {
  from: string;
  to: string;
  taxPercent: number | null;
  total: ReconciliationDay;
  days: ReconciliationDay[];
}
