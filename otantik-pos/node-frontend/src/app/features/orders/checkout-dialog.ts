import { Component, Signal, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { Order, PaymentMethod } from '../../core/api/models';
import { orderLabel } from '../../core/orders/order-rules';
import { MoneyPipe } from '../../core/ui/money.pipe';

export interface CheckoutDialogData {
  // The live bill: a captain's round, or another tablet, can change it while this is open.
  order: Signal<Order>;
}

export interface CheckoutResult {
  method: PaymentMethod;
  // What the cashier was shown when they took payment. The till refuses if the bill has
  // changed since, rather than charge a total nobody was told.
  total: number;
  // The cash handed over, when paying in cash and the cashier entered it: the receipt prints it
  // and the change.
  cashReceived: number | null;
}

// Taking payment. None of these methods needs the cloud: the till closes the bill on its own,
// online or not. Only points already applied to the bill need the delivery system, to be spent,
// and the order screen does not offer checkout while they cannot be.
@Component({
  selector: 'app-checkout-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MoneyPipe,
  ],
  template: `
    <h2 mat-dialog-title>Checkout · {{ label() }}</h2>
    <mat-dialog-content>
      <p class="due">
        <span>Amount due</span>
        <strong class="amount">{{ order().totalAmount | money }}</strong>
      </p>
      @if (changed()) {
        <p class="changed" role="alert">
          The bill changed while this was open: it was {{ openedTotal | money }}. Check the new amount with the customer.
        </p>
      }
      @if (order().pointsRedeemed > 0) {
        <p class="note">
          Includes {{ order().pointsRedeemed }} loyalty points ({{ order().pointsDiscountAmount | money }} off), spent from the
          customer's account when you confirm.
        </p>
      }

      <mat-button-toggle-group
        class="methods"
        [ngModel]="method()"
        (ngModelChange)="method.set($event)"
        aria-label="Payment method"
        hideSingleSelectionIndicator
      >
        <mat-button-toggle value="Cash"><mat-icon>payments</mat-icon> Cash</mat-button-toggle>
        <mat-button-toggle value="Visa"><mat-icon>credit_card</mat-icon> Card</mat-button-toggle>
        <mat-button-toggle value="Instapay"><mat-icon>qr_code_2</mat-icon> Instapay</mat-button-toggle>
      </mat-button-toggle-group>

      @if (method() === 'Cash') {
        <mat-form-field class="tendered" floatLabel="always">
          <mat-label>Cash received</mat-label>
          <span matTextPrefix>L.E&nbsp;</span>
          <input matInput type="number" min="0" step="any" [ngModel]="tendered()" (ngModelChange)="tendered.set($event)" />
        </mat-form-field>
        @if (change() !== null) {
          <p class="due change">
            <span>Change</span>
            <strong class="amount">{{ change() | money }}</strong>
          </p>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="confirm()" [disabled]="short()">
        <mat-icon>check</mat-icon>
        Take payment
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .due {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
      font: var(--mat-sys-title-large);
      margin: 0 0 8px;
    }
    .change {
      font: var(--mat-sys-title-medium);
      color: var(--till-ok);
    }
    .note {
      color: var(--mat-sys-on-surface-variant);
      margin: 0 0 12px;
    }
    .changed {
      color: var(--mat-sys-error);
      font: var(--mat-sys-title-small);
      margin: 0 0 12px;
    }
    .methods {
      width: 100%;
      margin: 8px 0 16px;
    }
    .methods mat-button-toggle {
      flex: 1;
    }
    .tendered {
      width: 100%;
    }
  `,
})
export class CheckoutDialog {
  private readonly dialogRef = inject(MatDialogRef<CheckoutDialog, CheckoutResult>);
  protected readonly order = inject<CheckoutDialogData>(MAT_DIALOG_DATA).order;
  protected readonly label = computed(() => orderLabel(this.order()));

  // What was due when this opened, to say so if it changes.
  protected readonly openedTotal = this.order().totalAmount;
  protected readonly changed = computed(() => this.order().totalAmount !== this.openedTotal);

  protected readonly method = signal<PaymentMethod>('Cash');
  protected readonly tendered = signal<number | null>(null);

  // The change, for the cashier now; the receipt prints it too.
  protected readonly change = computed(() => {
    const tendered = this.tendered();
    const due = this.order().totalAmount;
    return tendered === null || tendered < due ? null : tendered - due;
  });

  protected readonly short = computed(() => {
    const tendered = this.tendered();
    return this.method() === 'Cash' && tendered !== null && tendered < this.order().totalAmount;
  });

  protected confirm(): void {
    const method = this.method();
    this.dialogRef.close({ method, total: this.order().totalAmount, cashReceived: method === 'Cash' ? this.tendered() : null });
  }
}
