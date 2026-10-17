import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { SupplierBalance } from '../../core/costing/costing.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { FormChecks, FormProblem } from './form-checks';

// Material's own: a field in error once touched and invalid.
const defaultMatcher = new ErrorStateMatcher();

// A supplier paid against what they are owed (سداد مورد): an old debt settled, no invoice and no
// stock. Its id is the payment's, made here: a retry pays once.
export interface SupplierPaymentEntry {
  id: string;
  supplierId: string | null;
  amount: number | null;
  notes: string;
}

export function newSupplierPayment(): SupplierPaymentEntry {
  return { id: crypto.randomUUID(), supplierId: null, amount: null, notes: '' };
}

// Who is paid, how much, and a note or receipt number: entered alike in the daily batch's payment
// blocks and on the new invoice screen's supplier payment. Only the suppliers owed something are
// offered, each with what they are owed; owed is what the chosen one is owed now (less the
// payments above it, in a batch: afterAbove), and more than that is flagged. Its fields sit in the
// form around it.
@Component({
  selector: 'app-supplier-payment-fields',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MoneyPipe],
  template: `
    <mat-form-field subscriptSizing="dynamic" class="supplier">
      <mat-label>Supplier · المورد</mat-label>
      <mat-select [(ngModel)]="payment().supplierId" (ngModelChange)="changed.emit()" [aria-label]="label() + ', supplier'"
        [errorStateMatcher]="marked(', supplier')">
        @for (s of suppliers(); track s.supplierId) {
          <mat-option [value]="s.supplierId">{{ s.supplier }} · owed {{ s.balance | money }}</mat-option>
        } @empty {
          <mat-option disabled>No supplier is owed anything: enter an old debt on Financials · Supplier debts</mat-option>
        }
      </mat-select>
      @if (owed(); as o) {
        <mat-hint>Owed {{ o | money }}{{ afterAbove() ? ' after the payments above' : '' }}</mat-hint>
      }
      <mat-error>{{ said(', supplier') }}</mat-error>
    </mat-form-field>
    <mat-form-field subscriptSizing="dynamic" class="amount">
      <mat-label>Payment amount · قيمة السداد</mat-label>
      <input matInput type="number" min="0" step="any" [(ngModel)]="payment().amount" (ngModelChange)="changed.emit()" [attr.aria-label]="label() + ', amount'"
        [errorStateMatcher]="marked(', amount')" />
      <span matTextSuffix>&nbsp;L.E</span>
      @if (over()) {
        <mat-hint class="warn-text">More than they are owed.</mat-hint>
      }
      <mat-error>{{ said(', amount') }}</mat-error>
    </mat-form-field>
    <mat-form-field subscriptSizing="dynamic" class="notes">
      <mat-label>Notes / receipt no. · ملاحظات</mat-label>
      <input matInput [(ngModel)]="payment().notes" (ngModelChange)="changed.emit()" maxlength="300" [attr.aria-label]="label() + ', notes'" />
    </mat-form-field>
  `,
  styles: `
    :host {
      display: contents;
    }
    .supplier {
      min-width: 240px;
    }
    .amount {
      width: 170px;
    }
    .notes {
      min-width: 200px;
    }
    .warn-text {
      color: var(--mat-sys-error);
    }
  `,
})
export class SupplierPaymentFields {
  readonly payment = input.required<SupplierPaymentEntry>();
  readonly suppliers = input.required<SupplierBalance[]>();
  readonly owed = input<number | null>(null);
  readonly afterAbove = input(false);
  // How its fields are named to a screen reader: "Supplier payment 2, amount".
  readonly label = input('Supplier payment');
  // The screen's checks, which mark a field once a send is refused for what it is missing.
  readonly checks = input<FormChecks | null>(null);
  readonly changed = output<void>();

  protected marked(field: string): ErrorStateMatcher {
    return this.checks()?.field(this.label() + field) ?? defaultMatcher;
  }

  protected said(field: string): string {
    return this.checks()?.message(this.label() + field) ?? '';
  }

  // More than they are owed: refused when sent.
  protected over(): boolean {
    return isOverOwed(this.payment(), this.owed());
  }
}

export function isOverOwed(payment: SupplierPaymentEntry, owed: number | null): boolean {
  return owed !== null && (payment.amount ?? 0) > owed + 0.001;
}

// What holds a payment up, by its fields: a supplier owed, an amount, no more than they are owed.
// label is the payment's, as its fields are named by it: "Supplier payment 2".
export function paymentProblems(payment: SupplierPaymentEntry, owed: number | null, label: string): FormProblem[] {
  const found: FormProblem[] = [];
  if (!payment.supplierId) {
    found.push({ where: label, what: 'choose the supplier', field: `${label}, supplier` });
  }
  if (!((payment.amount ?? 0) > 0)) {
    found.push({ where: label, what: 'enter the amount', field: `${label}, amount` });
  } else if (isOverOwed(payment, owed)) {
    found.push({ where: label, what: 'it is more than they are owed', field: `${label}, amount` });
  }
  return found;
}
