import { Component, Injectable, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import {
  CostingApi,
  ExpenseItem,
  FundingSource,
  InvoicePaymentStatus,
  PurchasePaymentMethod,
  SafeEntry,
  SafeTransactionKind,
  SafeVoucherInput,
  SupplierBalance,
  TakingsPreview,
  fundingSourceLabels,
  isRequested,
  paymentMethodLabels,
  safeKindLabels,
} from '../../core/costing/costing.api';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessage } from '../../core/http/error-message';
import { EmployeePicker } from '../../core/hr/employee-picker';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';

// The treasury's screens share these: dates as the till sends them, the dialog every safe entry is
// entered in, and the service that sends it, which is a request for anyone who may not approve.

// A day as the API takes it: 2026-10-12, in the till's own time.
export function isoDay(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

export function todayIso(): string {
  return isoDay(new Date());
}

export function monthBounds(year: number, month: number): { from: string; to: string } {
  return { from: isoDay(new Date(year, month - 1, 1)), to: isoDay(new Date(year, month, 0)) };
}

// The till's business day, as the server keeps it: between midnight and the hour the day starts
// it is still yesterday, the night the cash belongs to. Asked once, and kept; the browser's own
// date until it answers. An employee working their HR tabs in the portal asks HR for it.
@Injectable({ providedIn: 'root' })
export class BusinessDay {
  private readonly api = inject(CostingApi);
  private readonly hr = inject(HrApi);
  private readonly auth = inject(AuthService);
  private asked: Promise<string> | null = null;

  today(): Promise<string> {
    this.asked ??= (async () => {
      try {
        return (await firstValueFrom(this.auth.employeeActing() ? this.hr.today() : this.api.businessToday())).today;
      } catch {
        // Asked again next time.
        this.asked = null;
        return todayIso();
      }
    })();
    return this.asked;
  }
}

// What a safe entry starts with: its kind, and for some a supplier, an amount or a day's takings.
export interface SafeVoucherDialogData {
  kind: SafeTransactionKind;
  canApprove: boolean;
  // The till's business day: the entry's date unless another is chosen, and the latest.
  today?: string;
  supplierId?: string;
  // An advance's or a salary's employee, chosen already; a salary's month (its first day).
  employeeId?: string;
  // An advance paid outside the till (HR's own): no cash out of the safe or a day's takings, so
  // nothing to say it is paid from. The dialog only asks; HR records it.
  outsideTill?: boolean;
  payrollMonth?: string;
  amount?: number;
  revenueDate?: string;
  // A day's takings: the till's tax rate (null before it has one), and whether the sum counted
  // starts as including tax, as the till's own bills do.
  taxPercent?: number | null;
  includesTax?: boolean;
}

// A day's takings entered with the tax in them, as a ledger line shows it: the rate, the tax, and
// the takings before it. A reversal's are its entry's.
export function entryTax(e: SafeEntry): { percent: number; tax: number; net: number } | null {
  if (!e.includesTax || e.taxPercent == null) {
    return null;
  }
  // The tax is in a day's takings' gross, of which in is what reached the safe.
  const tax = Math.abs(e.tax);
  const sum = e.gross != null ? Math.abs(e.gross) : e.in || e.out;
  return { percent: e.taxPercent, tax, net: Math.round((sum - tax) * 100) / 100 };
}

// The tax in a sum that includes it, as the server takes it off: the sum less the sum over
// (1 + rate), to the piastre.
export function taxIn(gross: number, percent: number): { net: number; tax: number } {
  const net = Math.round((gross / (1 + percent / 100)) * 100) / 100;
  return { net, tax: Math.round((gross - net) * 100) / 100 };
}

const sourcesFor: Partial<Record<SafeTransactionKind, FundingSource[]>> = {
  OpeningBalance: ['CarriedOver', 'OwnerFinancing'],
  Funding: ['OwnerFinancing', 'Other'],
};

// One safe entry, as it is entered: its date and amount, and what it needs: where the money came
// from (opening balance, funding), which day's takings (moving them in), what the expense is (an
// expense voucher), which supplier is paid and how much they are owed, or which employee an advance
// is for and whether the safe or a day's takings pay it. Closes with the entry to send, or nothing
// when cancelled.
@Component({
  selector: 'app-safe-voucher-dialog',
  imports: [
    EmployeePicker,
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MoneyPipe,
  ],
  template: `
    <h2 mat-dialog-title>{{ label.en }} <span class="ar">{{ label.ar }}</span></h2>
    <mat-dialog-content>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      <p class="muted">{{ data.outsideTill ? explanationOutside : explanation }}</p>
      <div class="form">
        @if (data.kind === 'SupplierPayment') {
          <mat-form-field class="wide">
            <mat-label>Supplier · المورد</mat-label>
            <mat-select [(ngModel)]="supplierId" (ngModelChange)="supplierChosen()" aria-label="Supplier">
              @for (s of owedSuppliers(); track s.supplierId) {
                <mat-option [value]="s.supplierId">{{ s.supplier }} · owed {{ s.balance | money }}</mat-option>
              } @empty {
                <mat-option disabled>No supplier is owed anything</mat-option>
              }
            </mat-select>
            @if (owed(); as o) {
              <mat-hint>Owed {{ o | money }}</mat-hint>
            }
          </mat-form-field>
        }
        @if (forEmployee) {
          @if (data.kind === 'SalaryPayment' && data.payrollMonth) {
            <p class="wide muted">The pay of {{ monthName(data.payrollMonth) }}, as their finalized payslip says.</p>
          }
          <div class="wide picker">
            <app-employee-picker [employees]="payable()" [(value)]="employeeId" [disabled]="data.kind === 'SalaryPayment'" [fill]="true"
              [none]="data.kind === 'EmployeeAdvance'" noneLabel="No one yet: give it out on HR → Advances"
              [hint]="data.kind === 'EmployeeAdvance' && !employeeId() ? 'A lump sum handed out as advances: a manager gives it out on HR → Advances' : null" />
          </div>
          @if (data.outsideTill) {
            <p class="wide outside"><strong>Paid outside the till</strong> · من خارج الخزنة: nothing leaves the safe or a day’s takings.</p>
          } @else {
            <mat-form-field>
              <mat-label>Paid from · يُصرف من</mat-label>
              <mat-select [(ngModel)]="paidFrom" aria-label="Paid from">
                <mat-option value="Safe">The safe · الخزنة</mat-option>
                <mat-option value="DayRevenue">A day’s takings · إيراد يوم</mat-option>
              </mat-select>
            </mat-form-field>
            @if (paidFrom === 'DayRevenue') {
              <mat-form-field>
                <mat-label>Takings of · إيراد يوم</mat-label>
                <input matInput type="date" [(ngModel)]="revenueDate" [max]="date" aria-label="Takings of" />
              </mat-form-field>
            } @else {
              <span></span>
            }
          }
        }
        @if (data.kind === 'ExpenseVoucher') {
          <mat-form-field class="wide">
            <mat-label>Expense · المصروف</mat-label>
            <mat-select [(ngModel)]="expenseItemId" aria-label="Expense">
              @for (e of expenseItems(); track e.id) {
                <mat-option [value]="e.id">{{ e.name }} <span class="muted">· {{ e.category }}</span></mat-option>
              } @empty {
                <mat-option disabled>No expense items yet: add them under Purchases · Expense items</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        @if (sources.length) {
          <mat-form-field class="wide">
            <mat-label>Source · المصدر</mat-label>
            <mat-select [(ngModel)]="source" aria-label="Source">
              @for (s of sources; track s) {
                <mat-option [value]="s">{{ sourceLabels[s].en }} · {{ sourceLabels[s].ar }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        @if (data.kind === 'RevenueTransfer') {
          <mat-form-field>
            <mat-label>Takings of · إيراد يوم</mat-label>
            <input matInput type="date" [(ngModel)]="revenueDate" (ngModelChange)="takingsDayChosen()" [max]="date" aria-label="Takings of" />
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ data.kind === 'RevenueTransfer' ? 'Gross takings · إجمالي الإيراد' : 'Amount · المبلغ' }}</mat-label>
          <input matInput type="number" min="0" step="any" [(ngModel)]="amount" aria-label="Amount" />
          <span matTextSuffix>&nbsp;L.E</span>
        </mat-form-field>
        <!-- Not for a day already entered: the problem below says so. -->
        @if (data.kind === 'RevenueTransfer' && preview(); as p) {
          @if (p.entered === null) {
            <dl class="wide breakdown" aria-live="polite">
              <div>
                <dt>Gross takings entered <span class="ar">إجمالي الإيراد</span></dt>
                <dd>{{ amount ?? 0 | money }}</dd>
              </div>
              <div>
                <dt>
                  Less paid out of them
                  @if (p.invoices || p.supplierPayments || p.employeePayments) {
                    <span class="muted small">&nbsp;· {{ paidOutCount(p) }}</span>
                  }
                  <span class="ar">المنصرف منه</span>
                </dt>
                <dd>{{ p.paidOut | money }}</dd>
              </div>
              <div class="net">
                <dt>Net into the safe <span class="ar">الصافي للخزنة</span></dt>
                <dd [class.negative]="netIntoSafe() < 0">{{ netIntoSafe() | money }}</dd>
              </div>
            </dl>
            @if (netIntoSafe() < 0) {
              <p class="wide error small">The invoices paid out of these takings come to {{ -netIntoSafe() | money }} more than them: that comes out of the safe.</p>
            }
          }
        }
        @if (data.kind === 'RevenueTransfer') {
          <div class="wide tax">
            <mat-checkbox [(ngModel)]="includesTax" [disabled]="data.taxPercent == null">
              Total includes tax? <span class="ar">هل إجمالي الإيراد يشمل الضريبة؟</span>
            </mat-checkbox>
            @if (data.taxPercent == null) {
              <p class="muted small">The till has no tax rate yet: it comes from the delivery system's settings.</p>
            } @else if (taxSplit(); as t) {
              <p class="muted small">Before tax {{ t.net | money }} · tax at {{ data.taxPercent }}% {{ t.tax | money }}, in the gross.</p>
            }
          </div>
        }
        <mat-form-field>
          <mat-label>Date · التاريخ</mat-label>
          <input matInput type="date" [(ngModel)]="date" [max]="today" aria-label="Date" />
        </mat-form-field>
        <mat-form-field class="wide" subscriptSizing="dynamic">
          <mat-label>Notes · ملاحظات</mat-label>
          <input matInput [(ngModel)]="notes" maxlength="300" aria-label="Notes" />
        </mat-form-field>
      </div>
      @if (!data.canApprove) {
        <p class="ask">
          A manager approves safe entries: no cash moves until then. <span class="ar">يُرسل للمدير للموافقة.</span>
        </p>
      }
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="done()" [disabled]="!!problem()">{{ data.canApprove ? 'Save' : 'Send for approval' }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: './costing.scss',
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 4px 12px;
    }
    .wide {
      grid-column: span 2;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
      white-space: nowrap;
    }
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    .error {
      color: var(--mat-sys-error);
    }
    .tax {
      margin: -4px 0 8px;

      p {
        margin: 0 0 0 40px;
      }
    }
    .small {
      font-size: 0.85em;
    }
    .breakdown {
      display: grid;
      gap: 4px;
      margin: -4px 0 12px;
      padding: 10px 12px;
      border-radius: 8px;
      background: var(--mat-sys-surface-container);

      div {
        display: flex;
        justify-content: space-between;
        gap: 12px;
      }
      dt,
      dd {
        margin: 0;
      }
      dd {
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }
      .net {
        padding-top: 4px;
        border-top: 1px solid var(--mat-sys-outline-variant);
        font-weight: 600;
      }
      .negative {
        color: var(--mat-sys-error);
      }
    }
  `,
})
export class SafeVoucherDialog {
  private readonly api = inject(CostingApi);
  private readonly hr = inject(HrApi);
  private readonly ref = inject<MatDialogRef<SafeVoucherDialog, SafeVoucherInput>>(MatDialogRef);
  protected readonly data = inject<SafeVoucherDialogData>(MAT_DIALOG_DATA);

  protected readonly label = this.data.outsideTill
    ? { en: 'Advance outside the till', ar: 'سلفة من خارج الخزنة' }
    : safeKindLabels[this.data.kind];
  protected readonly sources = sourcesFor[this.data.kind] ?? [];
  protected readonly sourceLabels = fundingSourceLabels;
  protected readonly today = this.data.today ?? todayIso();
  protected readonly loading = signal(false);
  protected readonly expenseItems = signal<ExpenseItem[]>([]);
  private readonly employees = signal<Employee[]>([]);
  // Who an advance can go to: those still employed. A salary is anyone's, left or not.
  protected readonly payable = computed(() => this.employees().filter((e) => e.isActive || this.data.kind === 'SalaryPayment'));
  private readonly balances = signal<SupplierBalance[]>([]);
  protected readonly owedSuppliers = computed(() => this.balances().filter((b) => b.balance > 0));
  // A day's takings: what was paid out of them already, taken off what goes into the safe.
  protected readonly preview = signal<TakingsPreview | null>(null);

  protected amount: number | null = this.data.amount ?? null;
  protected date = this.today;
  protected revenueDate = this.data.revenueDate ?? this.today;
  protected source: FundingSource | null = this.sources[0] ?? null;
  protected supplierId: string | null = this.data.supplierId ?? null;
  protected expenseItemId: string | null = null;
  protected readonly employeeId = signal<string | null>(this.data.employeeId ?? null);
  // An advance's or a salary's: who it is for, and the safe or a day's takings paying it.
  protected readonly forEmployee = this.data.kind === 'EmployeeAdvance' || this.data.kind === 'SalaryPayment';
  protected paidFrom: 'Safe' | 'DayRevenue' = this.data.revenueDate ? 'DayRevenue' : 'Safe';
  protected notes = '';
  protected includesTax = this.data.includesTax ?? false;

  protected readonly explanation = {
    OpeningBalance: 'The cash in the safe as the treasury starts: carried over from before, or put in by the owners.',
    Funding: 'Money put into the safe from outside the sales: the owners’ financing, or another source.',
    RevenueTransfer: 'A day’s takings, gross, as the POS totals them. What was paid straight out of them is taken off; the rest goes into the safe.',
    ExpenseVoucher: 'Cash paid out of the safe for an expense: a bill, wages, petty cash. It counts on the profit and loss under its category.',
    SupplierPayment: 'Cash paid to a supplier from the safe, against what the restaurant owes them.',
    InvoicePayment: '',
    EmployeeAdvance:
      'Cash advanced to an employee (سلفة), from the safe or a day’s takings. It is not an expense: it comes off their pay for the month, on the payroll. ' +
      'Naming no one makes it a lump sum handed out as advances, given out to employees on HR → Advances.',
    SalaryPayment: 'Cash paid to an employee, from the safe or a day’s takings.',
  }[this.data.kind];

  protected readonly explanationOutside =
    'An advance paid outside the till: from the owner’s own pocket, say. No cash leaves the safe or a day’s takings, and Financials never sees it. ' +
    'It comes off the employee’s pay for the month, on the payroll. Naming no one makes it a lump sum, given out a share each on this tab.';

  constructor() {
    void this.load();
  }

  // "October 2026", from its first day.
  protected monthName(month: string): string {
    return new Date(`${month}T12:00:00`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
  }

  protected owed(): number | null {
    return this.balances().find((b) => b.supplierId === this.supplierId)?.balance ?? null;
  }

  // The takings before tax and the tax in them, while they are entered as including it.
  protected taxSplit(): { net: number; tax: number } | null {
    const rate = this.data.taxPercent;
    return this.includesTax && rate != null && this.amount !== null && this.amount > 0 ? taxIn(this.amount, rate) : null;
  }

  // What of the gross goes into the safe: it less what was paid out of the day's takings.
  protected netIntoSafe(): number {
    return Math.round(((this.amount ?? 0) - (this.preview()?.paidOut ?? 0)) * 100) / 100;
  }

  protected takingsDayChosen(): void {
    void this.loadPreview();
  }

  // "5 invoices, 1 supplier payment, 2 to employees".
  protected paidOutCount(p: TakingsPreview): string {
    const count = (n: number | undefined, one: string, many: string) => (n ? `${n} ${n === 1 ? one : many}` : '');
    return [
      count(p.invoices, 'invoice', 'invoices'),
      count(p.supplierPayments, 'supplier payment', 'supplier payments'),
      count(p.employeePayments, 'to an employee', 'to employees'),
    ]
      .filter(Boolean)
      .join(', ');
  }

  // A supplier chosen: what they are owed, to pay in full unless less is typed.
  protected supplierChosen(): void {
    this.amount = this.owed();
  }

  // What stops it being sent, said plainly.
  protected problem(): string | null {
    const opening = this.data.kind === 'OpeningBalance';
    if (this.amount === null || this.amount < 0 || (!opening && this.amount === 0)) {
      return opening ? null : 'Enter an amount more than nothing.';
    }
    if (!this.date) {
      return 'Enter the date.';
    }
    if (this.sources.length && !this.source) {
      return 'Choose where the money came from.';
    }
    if (this.data.kind === 'ExpenseVoucher' && !this.expenseItemId) {
      return 'Choose what the expense is.';
    }
    if (this.forEmployee) {
      // An advance may name no one: a lump sum, given out on HR → Advances.
      if (!this.employeeId() && this.data.kind === 'SalaryPayment') {
        return 'Choose the employee.';
      }
      if (!this.data.outsideTill && this.paidFrom === 'DayRevenue' && (!this.revenueDate || this.revenueDate > this.date)) {
        return `Choose the day whose takings pay it: that day or one before the ${this.data.kind === 'SalaryPayment' ? 'payment' : 'advance'}.`;
      }
    }
    if (this.data.kind === 'SupplierPayment') {
      const owed = this.owed();
      if (!this.supplierId) {
        return 'Choose the supplier.';
      }
      if (owed !== null && this.amount > owed) {
        return 'That is more than they are owed.';
      }
    }
    if (this.data.kind === 'RevenueTransfer' && (!this.revenueDate || this.revenueDate > this.date)) {
      return 'Choose the day whose takings these are: that day or one before the entry.';
    }
    const entered = this.data.kind === 'RevenueTransfer' ? this.preview()?.entered : null;
    if (entered != null) {
      return `These takings are entered already: ${new MoneyPipe().transform(entered)}. To change them, void that entry on the Safe tab and enter them again.`;
    }
    return null;
  }

  protected done(): void {
    if (this.problem() || this.amount === null) {
      return;
    }
    const voucher: SafeVoucherInput = {
      requestId: crypto.randomUUID(),
      transactionId: crypto.randomUUID(),
      kind: this.data.kind,
      amount: this.amount,
      date: this.date,
      source: this.sources.length ? this.source : null,
      revenueDate:
        this.data.kind === 'RevenueTransfer' || (this.forEmployee && !this.data.outsideTill && this.paidFrom === 'DayRevenue') ? this.revenueDate : null,
      supplierId: this.data.kind === 'SupplierPayment' ? this.supplierId : null,
      expenseItemId: this.data.kind === 'ExpenseVoucher' ? this.expenseItemId : null,
      notes: this.notes.trim() || null,
      reason: null,
      includesTax: this.data.kind === 'RevenueTransfer' && this.includesTax && this.data.taxPercent != null,
      employeeId: this.forEmployee ? this.employeeId() : null,
      payrollMonth: this.data.kind === 'SalaryPayment' ? (this.data.payrollMonth ?? null) : null,
    };
    this.ref.close(voucher);
  }

  private async load(): Promise<void> {
    if (this.data.kind === 'RevenueTransfer') {
      return this.loadPreview();
    }
    if (this.data.kind !== 'ExpenseVoucher' && this.data.kind !== 'SupplierPayment' && !this.forEmployee) {
      return;
    }
    this.loading.set(true);
    try {
      if (this.forEmployee) {
        this.employees.set(await firstValueFrom(this.hr.employees()));
      } else if (this.data.kind === 'ExpenseVoucher') {
        this.expenseItems.set(await firstValueFrom(this.api.expenseItems()));
      } else {
        this.balances.set(await firstValueFrom(this.api.supplierBalances()));
        if (this.supplierId && this.amount === null) {
          this.amount = this.owed();
        }
      }
    } finally {
      this.loading.set(false);
    }
  }

  // Without it the breakdown is not shown; the server takes off what was paid out all the same.
  private async loadPreview(): Promise<void> {
    const day = this.revenueDate;
    if (!day) {
      this.preview.set(null);
      return;
    }
    try {
      const preview = await firstValueFrom(this.api.takingsPreview(day));
      if (this.revenueDate === day) this.preview.set(preview);
    } catch {
      this.preview.set(null);
    }
  }
}

export interface PayInvoiceDialogData {
  what: string;
  total: number;
  hasSupplier: boolean;
  canApprove: boolean;
  today?: string;
}

// How an unpaid cash invoice is paid now: from the safe, from a day's takings, or put on credit.
@Component({
  selector: 'app-pay-invoice-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Pay {{ data.what }} <span class="ar">سداد الفاتورة</span></h2>
    <mat-dialog-content>
      <p>{{ data.total | money }}, not paid yet: its cash payment was rejected or voided.</p>
      <mat-form-field class="wide">
        <mat-label>Paid · طريقة الدفع</mat-label>
        <mat-select [(ngModel)]="method" aria-label="How it is paid">
          @for (m of methods; track m) {
            <mat-option [value]="m" [disabled]="m === 'Credit' && !data.hasSupplier">{{ labels[m].en }} · {{ labels[m].ar }}</mat-option>
          }
        </mat-select>
        @if (!data.hasSupplier) {
          <mat-hint>On credit needs a supplier on the invoice.</mat-hint>
        }
      </mat-form-field>
      @if (method === 'DayRevenue') {
        <mat-form-field>
          <mat-label>Takings of · إيراد يوم</mat-label>
          <input matInput type="date" [(ngModel)]="revenueDate" [max]="today" aria-label="Takings of" />
        </mat-form-field>
      }
      @if (!data.canApprove && method !== 'Credit') {
        <p class="muted">A manager approves the payment: no cash moves until then.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!method || (method === 'DayRevenue' && !revenueDate)"
        [mat-dialog-close]="{ method, revenueDate: method === 'DayRevenue' ? revenueDate : null }">
        {{ data.canApprove || method === 'Credit' ? 'Pay' : 'Send for approval' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide {
      width: 100%;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
  `,
})
export class PayInvoiceDialog {
  protected readonly data = inject<PayInvoiceDialogData>(MAT_DIALOG_DATA);
  protected readonly methods: PurchasePaymentMethod[] = ['Safe', 'DayRevenue', 'Credit'];
  protected readonly labels = paymentMethodLabels;
  protected readonly today = this.data.today ?? todayIso();
  protected method: PurchasePaymentMethod | null = 'Safe';
  protected revenueDate = this.today;
}

// What a supplier was owed before the treasury: an opening debt.
@Component({
  selector: 'app-supplier-opening-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Owed before the treasury <span class="ar">رصيد افتتاحي للمورد</span></h2>
    <mat-dialog-content>
      <p class="muted">What {{ data.supplier }} was owed from invoices entered before the treasury. No cash moves.</p>
      <mat-form-field>
        <mat-label>Owed · المبلغ المستحق</mat-label>
        <input matInput type="number" min="0" step="any" [(ngModel)]="owed" aria-label="Owed" />
        <span matTextSuffix>&nbsp;L.E</span>
      </mat-form-field>
      <mat-form-field>
        <mat-label>As of · بتاريخ</mat-label>
        <input matInput type="date" [(ngModel)]="date" aria-label="As of" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!owed || owed <= 0 || !date" [mat-dialog-close]="{ owed, date }">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
  `,
})
export class SupplierOpeningDialog {
  protected readonly data = inject<{ supplier: string; today?: string }>(MAT_DIALOG_DATA);
  protected owed: number | null = null;
  protected date = this.data.today ?? todayIso();
}

// Enters safe entries: at once for a manager, as a request for anyone else; and pays invoices.
@Injectable({ providedIn: 'root' })
export class Treasury {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);
  private readonly businessDay = inject(BusinessDay);

  // Asks for it, then sends it. True when something was entered or asked for.
  async enter(kind: SafeTransactionKind, preset: Partial<SafeVoucherDialogData> = {}): Promise<boolean> {
    const today = await this.businessDay.today();
    // A day's takings may be entered with the tax in them, taken off at the till's rate.
    const tax = kind === 'RevenueTransfer' ? { taxPercent: await this.taxPercent() } : {};
    const voucher = await firstValueFrom(
      this.dialog
        .open<SafeVoucherDialog, SafeVoucherDialogData, SafeVoucherInput>(SafeVoucherDialog, {
          data: { ...preset, ...tax, kind, canApprove: this.approvals.canApprove(), today },
          width: '520px',
          maxWidth: '95vw',
        })
        .afterClosed(),
    );
    if (!voucher) {
      return false;
    }
    try {
      const result = await firstValueFrom(this.api.recordVoucher(voucher));
      if (isRequested(result)) {
        await this.approvals.requested(result);
      } else {
        const money = (n: number) => n.toLocaleString('en-EG', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        const tax = result.includesTax ? `, tax in it ${money(result.taxAmount)} L.E` : '';
        this.notify.info(`${safeKindLabels[kind].en} entered: ${money(voucher.amount)} L.E${tax}.`);
      }
      return true;
    } catch (error) {
      this.notify.error(error, 'The entry could not be saved.');
      return false;
    }
  }

  // The bills' tax rate, or null before the till has one (or when it cannot be asked).
  private async taxPercent(): Promise<number | null> {
    try {
      return (await firstValueFrom(this.api.financialSettings())).taxPercent;
    } catch {
      return null;
    }
  }

  // An unpaid cash invoice, paid now. The status it is left in, or null when cancelled or refused.
  async payInvoice(invoiceId: string, data: Omit<PayInvoiceDialogData, 'canApprove' | 'today'>): Promise<InvoicePaymentStatus | null> {
    const today = await this.businessDay.today();
    const answer = await firstValueFrom(
      this.dialog
        .open<PayInvoiceDialog, PayInvoiceDialogData, { method: PurchasePaymentMethod; revenueDate: string | null }>(PayInvoiceDialog, {
          data: { ...data, canApprove: this.approvals.canApprove(), today },
          width: '440px',
        })
        .afterClosed(),
    );
    if (!answer) {
      return null;
    }
    try {
      const { paymentStatus } = await firstValueFrom(this.api.payPurchaseInvoice(invoiceId, crypto.randomUUID(), answer.method, answer.revenueDate));
      this.notify.info(
        paymentStatus === 'Pending'
          ? 'Payment submitted to the manager for approval. No cash moves until then.'
          : paymentStatus === 'OnCredit'
            ? 'Put on credit: owed to the supplier.'
            : 'Paid.',
      );
      await this.approvals.refresh();
      return paymentStatus;
    } catch (error) {
      this.notify.error(error, errorMessage(error, 'The invoice could not be paid.'));
      return null;
    }
  }

  async supplierOpening(supplierId: string, supplier: string): Promise<boolean> {
    const today = await this.businessDay.today();
    const answer = await firstValueFrom(
      this.dialog
        .open<SupplierOpeningDialog, { supplier: string; today: string }, { owed: number; date: string }>(SupplierOpeningDialog, { data: { supplier, today } })
        .afterClosed(),
    );
    if (!answer) {
      return false;
    }
    try {
      await firstValueFrom(this.api.supplierOpeningBalance(supplierId, crypto.randomUUID(), answer.owed, answer.date));
      this.notify.info(`${supplier}: owed ${answer.owed.toLocaleString('en-EG', { minimumFractionDigits: 2 })} L.E from before the treasury.`);
      return true;
    } catch (error) {
      this.notify.error(error, 'The opening debt could not be saved.');
      return false;
    }
  }
}
