import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of, throwError } from 'rxjs';

// The manager's costing, as the till server's api/costing and api/inventory speak it.

export type UnitOfMeasure = 'Gram' | 'Millilitre' | 'Piece';
export type RecipeTargetKind = 'MenuItem' | 'AddOn';
export type CostStatus = 'WithinTarget' | 'AboveTarget' | 'NoRecipe' | 'MissingPrices' | 'NoPrice';

// A raw material with its costs. Quantities are in its unit (grams, millilitres, pieces); costs
// are per purchase unit (a kg, a carton), as prices are written.
export interface MaterialCost {
  id: string;
  code: string | null;
  name: string;
  category: string | null;
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
export interface RecipeCostCard {
  targetKind: RecipeTargetKind;
  catalogItemId: number;
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
}

export interface CostingSettings {
  foodCostTargetPercent: number;
  // Left out of a save, it stays as it is.
  varianceTolerancePercent?: number | null;
}

export type StockCountStatus = 'Draft' | 'Posted';

export interface StockCountSummary {
  id: string;
  status: StockCountStatus;
  startedAtUtc: string;
  startedBy: string;
  postedAtUtc: string | null;
  postedBy: string | null;
  materials: number;
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
  category: string | null;
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

export interface Recipe {
  targetKind: RecipeTargetKind;
  catalogItemId: number;
  variantId: number | null;
  ingredients: RecipeLine[];
  portions: number;
}

export interface PurchaseLine {
  rawMaterialId: string;
  quantity: number;
  cost: number | null;
}

@Injectable({ providedIn: 'root' })
export class CostingApi {
  private readonly http = inject(HttpClient);

  materials(): Observable<MaterialCost[]> {
    return this.http.get<MaterialCost[]>('/api/costing/materials');
  }

  menu(): Observable<RecipeCostCard[]> {
    return this.http.get<RecipeCostCard[]>('/api/costing/menu');
  }

  recipeCard(kind: RecipeTargetKind, catalogItemId: number, variantId: number | null): Observable<RecipeCostCard> {
    return this.http.get<RecipeCostCard>('/api/costing/recipe-card', { params: this.target(kind, catalogItemId, variantId) });
  }

  theoretical(year: number, month: number): Observable<TheoreticalCostReport> {
    return this.http.get<TheoreticalCostReport>('/api/costing/theoretical', { params: { year, month } });
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

  saveMaterial(id: string | null, material: SaveMaterial): Observable<unknown> {
    return id
      ? this.http.put(`/api/inventory/raw-materials/${id}`, material)
      : this.http.post('/api/inventory/raw-materials', material);
  }

  // purchaseId is made here, once per delivery: a retry after a lost answer books it once.
  receivePurchase(purchaseId: string, lines: PurchaseLine[]): Observable<unknown> {
    return this.http.post('/api/inventory/purchases', { purchaseId, lines });
  }

  // Null when the dish has no recipe yet (404, and only 404).
  recipe(kind: RecipeTargetKind, catalogItemId: number, variantId: number | null): Observable<Recipe | null> {
    return this.http.get<Recipe>('/api/inventory/recipes', { params: this.target(kind, catalogItemId, variantId) }).pipe(
      catchError((error: unknown) => (error instanceof HttpErrorResponse && error.status === 404 ? of(null) : throwError(() => error))),
    );
  }

  // No ingredients removes the recipe.
  saveRecipe(recipe: Recipe): Observable<unknown> {
    return this.http.put('/api/inventory/recipes', recipe);
  }

  stockCounts(): Observable<StockCountSummary[]> {
    return this.http.get<StockCountSummary[]>('/api/inventory/stock-counts');
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

export const evaluationLabels: Record<VarianceEvaluation, { en: string; ar: string }> = {
  Unfavourable: { en: 'Unfavourable', ar: 'غير مواتٍ' },
  Favourable: { en: 'Favourable', ar: 'مواتٍ' },
  WithinLimit: { en: 'Within limit', ar: 'ضمن الحد' },
  SharedCost: { en: 'Shared cost', ar: 'تكلفة مشتركة' },
};
