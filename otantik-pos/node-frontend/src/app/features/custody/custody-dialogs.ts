import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Observable, firstValueFrom } from 'rxjs';
import { ApprovalResult } from '../../core/costing/costing.api';
import {
  AssetCategory,
  AssetDetail,
  AssetItem,
  AssetMovementReason,
  CustodyApi,
  CustodyResult,
  reasonLabels,
  statusLabels,
  takesAll,
} from '../../core/custody/custody.api';
import { EmployeePicker } from '../../core/hr/employee-picker';
import { Employee } from '../../core/hr/hr.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

// What a dialog that saves closes with: what was made, or what was asked of a manager.
export type CustodyAnswer = CustodyResult | ApprovalResult;

// Asked of a manager, not made yet: the custody answers carry a request either way.
export function wasAsked(result: CustodyAnswer): result is ApprovalResult {
  return result.outcome === 'Requested';
}

// A dialog's save: what it sends, the answer it closes with, and the error it says.
abstract class SavingDialog {
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected abstract readonly ref: MatDialogRef<unknown, CustodyAnswer>;

  protected async send(call: () => Observable<CustodyAnswer>, failed: string): Promise<void> {
    this.saving.set(true);
    this.error.set(null);
    try {
      this.ref.close(await firstValueFrom(call()));
    } catch (error) {
      this.error.set(errorMessage(error, failed));
    } finally {
      this.saving.set(false);
    }
  }
}

export interface AssetDialogData {
  // The one changed; none for a new one.
  asset?: AssetItem;
  categories: AssetCategory[];
  canApprove: boolean;
  // Its cost not known here (an employee in the portal): not asked, and kept as it is.
  costHidden?: boolean;
}

// An asset added in HR, or changed: its name, category, a piece's cost and notes. How many there
// are is said when it is added; vouchers change it after. One bought on an invoice costs what the
// invoice says.
@Component({
  selector: 'app-asset-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.asset) {
        Edit {{ data.asset.name }} <span class="inline-ar">تعديل الأصل</span>
      } @else {
        Add an asset <span class="inline-ar">إضافة أصل</span>
      }
    </h2>
    <mat-dialog-content>
      <div class="form">
        <mat-form-field class="wide">
          <mat-label>Name · الاسم</mat-label>
          <input matInput [(ngModel)]="name" maxlength="150" placeholder="Dinner plates, fryer, uniform" aria-label="Name" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Category · الفئة</mat-label>
          <mat-select [(ngModel)]="categoryId" aria-label="Category">
            <mat-option [value]="null">None</mat-option>
            @for (c of data.categories; track c.id) {
              <mat-option [value]="c.id">{{ c.name }}{{ c.isConsumable ? ' · consumable' : '' }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        @if (!(data.costHidden && data.asset)) {
          <mat-form-field>
            <mat-label>Cost a piece · تكلفة القطعة</mat-label>
            <input matInput type="number" min="0" step="any" [(ngModel)]="unitCost" [disabled]="bought" aria-label="Cost a piece" />
            <span matTextSuffix>&nbsp;L.E</span>
            <mat-hint>{{ bought ? 'As its invoice says' : 'Leave empty when not known' }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>How many · العدد</mat-label>
          <input matInput type="number" min="1" step="1" [(ngModel)]="quantity" [disabled]="!!data.asset" aria-label="How many" />
          @if (data.asset) {
            <mat-hint>Changed by vouchers</mat-hint>
          }
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>Notes · ملاحظات</mat-label>
          <input matInput [(ngModel)]="notes" maxlength="300" aria-label="Notes" />
        </mat-form-field>
        @if (!data.canApprove) {
          <mat-form-field class="wide">
            <mat-label>Reason · السبب</mat-label>
            <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Reason" />
          </mat-form-field>
        }
      </div>
      @if (!data.canApprove) {
        <p class="ask">A manager approves assets and their changes: nothing changes until then. <span class="inline-ar">يُرسل للمدير للموافقة.</span></p>
      }
      @if (saving()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="!!problem() || saving()">{{ data.canApprove ? 'Save' : 'Send for approval' }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 8px 12px;
    }
    .wide {
      grid-column: span 2;
    }
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    @media (max-width: 600px) {
      .form {
        grid-template-columns: minmax(0, 1fr);
      }
      .wide {
        grid-column: auto;
      }
    }
  `,
})
export class AssetDialog extends SavingDialog {
  protected readonly data = inject<AssetDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<AssetDialog, CustodyAnswer>>(MatDialogRef);
  private readonly api = inject(CustodyApi);
  private readonly id = this.data.asset?.id ?? crypto.randomUUID();
  // Made once: a retry after a lost answer finds what was asked.
  private readonly requestId = crypto.randomUUID();

  protected readonly bought = this.data.asset?.origin === 'FinancialPurchase';
  protected name = this.data.asset?.name ?? '';
  protected categoryId: string | null = this.data.asset?.categoryId ?? null;
  protected unitCost: number | null = this.data.asset?.unitCost ?? null;
  protected quantity: number | null = this.data.asset?.quantity ?? 1;
  protected notes = this.data.asset?.notes ?? '';
  protected reason = '';

  protected problem(): string | null {
    if (!this.name.trim()) {
      return 'Give it its name.';
    }
    if (this.unitCost !== null && !(this.unitCost >= 0)) {
      return 'A piece costs zero or more.';
    }
    return !this.data.asset && !(Number.isInteger(this.quantity) && this.quantity! >= 1) ? 'Say how many there are: 1 or more.' : null;
  }

  protected save(): Promise<void> {
    return this.send(
      () =>
        this.api.saveAsset(this.id, {
          requestId: this.requestId,
          name: this.name.trim(),
          categoryId: this.categoryId,
          unitCost: this.unitCost === null ? null : Number(this.unitCost),
          quantity: Number(this.quantity),
          notes: this.notes.trim() || null,
          requestReason: this.reason.trim() || null,
        }),
      'The asset could not be saved.',
    );
  }
}

export interface CheckInDialogData {
  asset: AssetItem;
  // Those employed now.
  employees: Employee[];
  today: string;
  canApprove: boolean;
}

// إذن دخول: all of it into an employee's custody, today or a day before.
@Component({
  selector: 'app-check-in-dialog',
  imports: [EmployeePicker, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    <h2 mat-dialog-title>Check in {{ data.asset.name }} <span class="inline-ar">إذن دخول عهدة</span></h2>
    <mat-dialog-content>
      <p class="muted">All {{ data.asset.quantity }} of it into their custody, from {{ from }}.</p>
      <div class="form">
        <div class="wide">
          <app-employee-picker [employees]="data.employees" [(value)]="employeeId" [fill]="true" label="Into the custody of · أمين العهدة" />
        </div>
        <mat-form-field>
          <mat-label>Date · التاريخ</mat-label>
          <input matInput type="date" [(ngModel)]="date" [max]="data.today" aria-label="Date" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Notes · ملاحظات</mat-label>
          <input matInput [(ngModel)]="notes" maxlength="300" aria-label="Notes" />
        </mat-form-field>
        @if (!data.canApprove) {
          <mat-form-field class="wide">
            <mat-label>Reason · السبب</mat-label>
            <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Reason" />
          </mat-form-field>
        }
      </div>
      @if (saving()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="!employeeId || !date || saving()">{{ data.canApprove ? 'Check in' : 'Send for approval' }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 8px 12px;
      margin-top: 8px;
    }
    .wide {
      grid-column: span 2;
    }
  `,
})
export class CheckInDialog extends SavingDialog {
  protected readonly data = inject<CheckInDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<CheckInDialog, CustodyAnswer>>(MatDialogRef);
  private readonly api = inject(CustodyApi);
  private readonly requestId = crypto.randomUUID();
  private readonly transactionId = crypto.randomUUID();

  protected readonly from = this.data.asset.status === 'Maintenance' ? 'maintenance' : 'the store';
  protected employeeId: string | null = null;
  protected date = this.data.today;
  protected notes = '';
  protected reason = '';

  protected save(): Promise<void> {
    return this.send(
      () =>
        this.api.checkIn(this.data.asset.id, {
          requestId: this.requestId,
          transactionId: this.transactionId,
          employeeId: this.employeeId!,
          date: this.date,
          notes: this.notes.trim() || null,
          requestReason: this.reason.trim() || null,
        }),
      'It could not be checked in.',
    );
  }
}

export interface CheckOutDialogData {
  asset: AssetItem;
  today: string;
  canApprove: boolean;
}

// إذن خروج, and why: handed back or to maintenance (all of it), or broken, lost or thrown away
// (how many of it). A consumable's breakage goes to Costing → Waste at its cost.
@Component({
  selector: 'app-check-out-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Check out {{ data.asset.name }} <span class="inline-ar">إذن خروج</span></h2>
    <mat-dialog-content>
      <p class="muted">
        @if (data.asset.employeeName) {
          From {{ data.asset.employeeName }}'s custody.
        } @else {
          {{ statusLabels[data.asset.status].en }}.
        }
        {{ data.asset.quantity }} of it.
      </p>
      <div class="form">
        <mat-form-field>
          <mat-label>Why · السبب</mat-label>
          <mat-select [ngModel]="why()" (ngModelChange)="choose($event)" aria-label="Why">
            @for (r of reasons; track r) {
              <mat-option [value]="r">{{ reasonLabels[r].en }} · {{ reasonLabels[r].ar }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>How many · العدد</mat-label>
          <input matInput type="number" min="1" [max]="data.asset.quantity" step="1" [ngModel]="quantity()" (ngModelChange)="quantity.set($event)"
            [disabled]="all()" aria-label="How many" />
          @if (all()) {
            <mat-hint>All of it goes</mat-hint>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label>Date · التاريخ</mat-label>
          <input matInput type="date" [(ngModel)]="date" [max]="data.today" aria-label="Date" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Notes · ملاحظات</mat-label>
          <input matInput [(ngModel)]="notes" maxlength="300" aria-label="Notes" />
        </mat-form-field>
        @if (!data.canApprove) {
          <mat-form-field class="wide">
            <mat-label>Reason for asking · سبب الطلب</mat-label>
            <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Reason for asking" />
          </mat-form-field>
        }
      </div>
      @if (why() === 'Breakage') {
        @if (data.asset.isConsumable) {
          <p class="waste">
            <mat-icon>delete_sweep</mat-icon> Goes to Costing → Waste as breakage@if (data.asset.unitCost !== null) {, at {{ breakageCost() | money }}}.
          </p>
        } @else {
          <p class="muted small">A fixed asset: broken, it is not the day's waste.</p>
        }
      }
      @if (saving()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="!!problem() || saving()">{{ data.canApprove ? 'Check out' : 'Send for approval' }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 8px 12px;
    }
    .wide {
      grid-column: span 2;
    }
    .waste {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 16%, transparent);
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class CheckOutDialog extends SavingDialog {
  protected readonly data = inject<CheckOutDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<CheckOutDialog, CustodyAnswer>>(MatDialogRef);
  private readonly api = inject(CustodyApi);
  private readonly requestId = crypto.randomUUID();
  private readonly transactionId = crypto.randomUUID();
  protected readonly reasonLabels = reasonLabels;
  protected readonly statusLabels = statusLabels;

  // Handed back is for one with someone or at maintenance; maintenance, for one not there yet.
  protected readonly reasons: AssetMovementReason[] = (['Returned', 'Maintenance', 'Breakage', 'Lost', 'Discarded'] as AssetMovementReason[]).filter(
    (r) => !(r === 'Returned' && this.data.asset.status === 'InStore') && !(r === 'Maintenance' && this.data.asset.status === 'Maintenance'),
  );
  protected readonly why = signal<AssetMovementReason>(this.data.asset.status === 'InCustody' ? 'Returned' : 'Breakage');
  protected readonly quantity = signal<number | null>(this.data.asset.quantity);
  protected readonly all = computed(() => takesAll(this.why()));
  protected readonly breakageCost = computed(() => (this.data.asset.unitCost ?? 0) * (Number(this.quantity()) || 0));
  protected date = this.data.today;
  protected notes = '';
  protected reason = '';

  protected choose(reason: AssetMovementReason): void {
    this.why.set(reason);
    if (takesAll(reason)) {
      this.quantity.set(this.data.asset.quantity);
    } else if (this.quantity() === this.data.asset.quantity && this.data.asset.quantity > 1) {
      this.quantity.set(1);
    }
  }

  protected problem(): string | null {
    const n = Number(this.quantity());
    if (!this.date) {
      return 'Choose the day.';
    }
    return Number.isInteger(n) && n >= 1 && n <= this.data.asset.quantity ? null : `Between 1 and ${this.data.asset.quantity} of it.`;
  }

  protected save(): Promise<void> {
    return this.send(
      () =>
        this.api.checkOut(this.data.asset.id, {
          requestId: this.requestId,
          transactionId: this.transactionId,
          reason: this.why(),
          quantity: Number(this.quantity()),
          date: this.date,
          notes: this.notes.trim() || null,
          requestReason: this.reason.trim() || null,
        }),
      'It could not be checked out.',
    );
  }
}

// An asset's vouchers, the latest first: who had it, from when, and what left.
@Component({
  selector: 'app-asset-history-dialog',
  imports: [DatePipe, MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>{{ detail()?.asset?.name ?? 'Asset' }} <span class="inline-ar">سجل الأصل</span></h2>
    <mat-dialog-content>
      @if (error(); as e) {
        <p class="error">{{ e }}</p>
      } @else if (detail(); as d) {
        <p class="muted">
          {{ d.asset.quantity }} now · {{ d.asset.employeeName ? 'with ' + d.asset.employeeName : statusLabels[d.asset.status].en }}
          @if (d.asset.origin === 'FinancialPurchase') {
            · bought on a purchase invoice
          }
        </p>
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Voucher<span class="ar">الإذن</span></th>
              <th>Employee<span class="ar">الموظف</span></th>
              <th class="num">How many<span class="ar">العدد</span></th>
              <th class="num">Cost<span class="ar">التكلفة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (v of d.vouchers; track v.id) {
              <tr>
                <td class="nowrap">{{ v.date | date: 'd MMM y' }}</td>
                <td>
                  {{ v.type === 'CheckIn' ? 'In · دخول' : 'Out · خروج' }}
                  @if (v.reason) {
                    <span class="muted"> · {{ reasonLabels[v.reason].en }}</span>
                  }
                  @if (v.routedToWaste) {
                    <span class="badge">In Waste · في الهالك</span>
                  }
                  @if (v.notes) {
                    <div class="muted small">{{ v.notes }}</div>
                  }
                </td>
                <td>{{ v.employeeName ?? '—' }}</td>
                <td class="num">{{ v.quantity }}</td>
                <td class="num">{{ v.cost === null ? '—' : (v.cost | money) }}</td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="muted">No vouchers yet: it has not left the store.</td></tr>
            }
          </tbody>
        </table>
      } @else {
        <mat-progress-bar mode="indeterminate" />
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .badge {
      display: inline-block;
      margin-left: 6px;
      padding: 0 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: color-mix(in srgb, var(--till-warn) 30%, transparent);
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class AssetHistoryDialog {
  private readonly api = inject(CustodyApi);
  protected readonly reasonLabels = reasonLabels;
  protected readonly statusLabels = statusLabels;
  protected readonly detail = signal<AssetDetail | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    const { assetId } = inject<{ assetId: string }>(MAT_DIALOG_DATA);
    firstValueFrom(this.api.asset(assetId)).then(
      (detail) => this.detail.set(detail),
      (error: unknown) => this.error.set(errorMessage(error, 'Its vouchers could not be loaded.')),
    );
  }
}
