import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApprovalResult } from '../costing/costing.api';

// Custody & Assets (عهدة وأصول): the categories, the assets and whose custody each is in, and the
// vouchers that move them. Seen by whoever sees HR; categories are a manager's; assets and
// vouchers are whoever manages HR's, an accountant's asked of a manager.

export type AssetOrigin = 'HrManual' | 'FinancialPurchase';
export type AssetStatus = 'InStore' | 'InCustody' | 'Maintenance' | 'BrokenWaste' | 'Discarded';
export type AssetTransactionType = 'CheckIn' | 'CheckOut';
export type AssetMovementReason = 'Returned' | 'Maintenance' | 'Breakage' | 'Lost' | 'Discarded';

export interface AssetCategory {
  id: string;
  name: string;
  isConsumable: boolean;
  // Not gone, and of those, in someone's custody.
  assets: number;
  inCustody: number;
}

export interface AssetItem {
  id: string;
  name: string;
  categoryId: string | null;
  categoryName: string | null;
  isConsumable: boolean;
  // A piece's; none when not known.
  unitCost: number | null;
  quantity: number;
  totalCost: number | null;
  employeeId: string | null;
  employeeName: string | null;
  origin: AssetOrigin;
  status: AssetStatus;
  // The posted invoice it was bought on.
  sourceInvoiceId: string | null;
  notes: string | null;
  createdBy: string;
  createdAtUtc: string;
}

export interface AssetVoucher {
  id: string;
  assetItemId: string;
  assetName: string;
  type: AssetTransactionType;
  date: string;
  employeeId: string | null;
  employeeName: string | null;
  quantity: number;
  reason: AssetMovementReason | null;
  notes: string | null;
  unitCost: number | null;
  cost: number | null;
  recordedBy: string;
  recordedAtUtc: string;
  // The breakage entry it made in Costing → Waste: a consumable's breakage.
  wasteEntryId: string | null;
  routedToWaste: boolean;
}

export interface AssetDetail {
  asset: AssetItem;
  vouchers: AssetVoucher[];
}

export interface CustodyItem {
  asset: AssetItem;
  // When it came into their custody.
  since: string | null;
}

export interface BreakageEntry {
  id: string;
  businessDate: string;
  assetItemId: string;
  assetTransactionId: string;
  description: string;
  quantity: number;
  unitCost: number | null;
  cost: number;
  employeeId: string | null;
  employeeName: string | null;
  notes: string | null;
  recordedBy: string;
  recordedAtUtc: string;
  isVoided: boolean;
}

// What a manager's change answers: applied, with the asset and the voucher it made.
export interface CustodyResult {
  outcome: 'Applied';
  asset: AssetItem | null;
  voucher: AssetVoucher | null;
}

export interface AssetInput {
  requestId: string;
  name: string;
  categoryId: string | null;
  unitCost: number | null;
  quantity: number;
  notes: string | null;
  requestReason: string | null;
}

export interface CheckInInput {
  requestId: string;
  transactionId: string;
  employeeId: string;
  date: string;
  notes: string | null;
  requestReason: string | null;
}

export interface CheckOutInput {
  requestId: string;
  transactionId: string;
  reason: AssetMovementReason;
  quantity: number;
  date: string;
  notes: string | null;
  requestReason: string | null;
}

export const statusLabels: Record<AssetStatus, { en: string; ar: string }> = {
  InStore: { en: 'In the store', ar: 'بالمخزن' },
  InCustody: { en: 'In custody', ar: 'في العهدة' },
  Maintenance: { en: 'At maintenance', ar: 'في الصيانة' },
  BrokenWaste: { en: 'Broken', ar: 'كسر هالك' },
  Discarded: { en: 'Discarded', ar: 'مستبعد' },
};

export const reasonLabels: Record<AssetMovementReason, { en: string; ar: string }> = {
  Returned: { en: 'Handed back', ar: 'مرتجع' },
  Maintenance: { en: 'To maintenance', ar: 'صيانة' },
  Breakage: { en: 'Broken', ar: 'كسر' },
  Lost: { en: 'Lost', ar: 'فقد' },
  Discarded: { en: 'Thrown away', ar: 'استبعاد' },
};

// Handed back and to maintenance take all of it; the rest, some of it.
export const takesAll = (reason: AssetMovementReason): boolean => reason === 'Returned' || reason === 'Maintenance';

export function isGone(asset: AssetItem): boolean {
  return asset.status === 'BrokenWaste' || asset.status === 'Discarded';
}

@Injectable({ providedIn: 'root' })
export class CustodyApi {
  private readonly http = inject(HttpClient);

  categories(): Observable<AssetCategory[]> {
    return this.http.get<AssetCategory[]>('/api/custody/categories');
  }

  saveCategory(id: string, name: string, isConsumable: boolean): Observable<AssetCategory> {
    return this.http.put<AssetCategory>(`/api/custody/categories/${id}`, { name, isConsumable });
  }

  deleteCategory(id: string): Observable<void> {
    return this.http.delete<void>(`/api/custody/categories/${id}`);
  }

  // Link with Financials on: those bought on purchase invoices too.
  assets(withFinancial: boolean): Observable<AssetItem[]> {
    return this.http.get<AssetItem[]>('/api/custody/assets', { params: { withFinancial } });
  }

  asset(id: string): Observable<AssetDetail> {
    return this.http.get<AssetDetail>(`/api/custody/assets/${id}`);
  }

  saveAsset(id: string, input: AssetInput): Observable<CustodyResult | ApprovalResult> {
    return this.http.put<CustodyResult | ApprovalResult>(`/api/custody/assets/${id}`, input);
  }

  checkIn(assetId: string, input: CheckInInput): Observable<CustodyResult | ApprovalResult> {
    return this.http.post<CustodyResult | ApprovalResult>(`/api/custody/assets/${assetId}/check-in`, input);
  }

  checkOut(assetId: string, input: CheckOutInput): Observable<CustodyResult | ApprovalResult> {
    return this.http.post<CustodyResult | ApprovalResult>(`/api/custody/assets/${assetId}/check-out`, input);
  }

  vouchers(from: string, to: string): Observable<AssetVoucher[]> {
    return this.http.get<AssetVoucher[]>('/api/custody/vouchers', { params: { from, to } });
  }

  employeeCustody(employeeId: string): Observable<CustodyItem[]> {
    return this.http.get<CustodyItem[]>(`/api/hr/employees/${employeeId}/custody`);
  }

  breakage(days = 30): Observable<BreakageEntry[]> {
    return this.http.get<BreakageEntry[]>('/api/costing/breakage', { params: { days } });
  }
}
