import { Component, computed, inject, signal } from '@angular/core';
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
  order: Order;
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
    <h2 mat-dialog-title>Checkout · {{ label }}</h2>
    <mat-dialog-content>
      <p class="due">
        <span>Amount due</span>
        <strong class="amount">{{ order.totalAmount | money }}</strong>
      </p>
      @if (order.pointsRedeemed > 0) {
        <p class="note">
          Includes {{ order.pointsRedeemed }} loyalty points ({{ order.pointsDiscountAmount | money }} off), spent from the
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
  private readonly dialogRef = inject(MatDialogRef<CheckoutDialog, PaymentMethod>);
  protected readonly order = inject<CheckoutDialogData>(MAT_DIALOG_DATA).order;
  protected readonly label = orderLabel(this.order);

  protected readonly method = signal<PaymentMethod>('Cash');
  protected readonly tendered = signal<number | null>(null);

  // Only a help for the cashier: the API records the method, not the cash handed over.
  protected readonly change = computed(() => {
    const tendered = this.tendered();
    return tendered === null || tendered < this.order.totalAmount ? null : tendered - this.order.totalAmount;
  });

  protected readonly short = computed(() => {
    const tendered = this.tendered();
    return this.method() === 'Cash' && tendered !== null && tendered < this.order.totalAmount;
  });

  protected confirm(): void {
    this.dialogRef.close(this.method());
  }
}
