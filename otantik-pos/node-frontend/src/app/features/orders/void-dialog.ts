import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { Order, OrderItem, VoidType } from '../../core/api/models';
import { itemName, orderLabel } from '../../core/orders/order-rules';

export interface VoidDialogData {
  order: Order;
  // Null to void the whole order.
  item: OrderItem | null;
  type: VoidType;
}

export interface VoidRequest {
  quantity: number;
  reason: string | null;
}

// Every kind of void, one dialog. Which kind it is was decided by the order's state, not by the
// cashier: before the kitchen, after it, or after payment (a refund). Only the first may go
// without a reason. Each is audited with who, what and why.
@Component({
  selector: 'app-void-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <mat-dialog-content>
      <p class="kind" [class.refund]="data.type === 'AfterPayment'">
        <mat-icon>{{ icon }}</mat-icon>
        <span>{{ explanation }}</span>
      </p>

      @if (data.item && data.item.quantity > 1) {
        <div class="quantity">
          <span>How many?</span>
          <button mat-icon-button (click)="step(-1)" [disabled]="quantity() <= 1" aria-label="One fewer">
            <mat-icon>remove</mat-icon>
          </button>
          <span class="count" aria-live="polite">{{ quantity() }} of {{ data.item.quantity }}</span>
          <button mat-icon-button (click)="step(1)" [disabled]="quantity() >= data.item.quantity" aria-label="One more">
            <mat-icon>add</mat-icon>
          </button>
        </div>
      }

      <mat-form-field class="full">
        <mat-label>Reason{{ reasonRequired ? '' : ' (optional)' }}</mat-label>
        <input matInput [ngModel]="reason()" (ngModelChange)="reason.set($event)" maxlength="200" cdkFocusInitial />
        @if (reasonRequired) {
          <mat-hint>Required once the kitchen has it or it has been paid for.</mat-hint>
        }
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Keep it</button>
      <button mat-flat-button class="danger" (click)="confirm()" [disabled]="!valid()">
        {{ data.type === 'AfterPayment' ? 'Refund' : 'Void' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .kind {
      display: flex;
      gap: 10px;
      padding: 12px;
      border-radius: 12px;
      margin-top: 0;
      background: var(--mat-sys-surface-container-high);
    }
    .kind.refund {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .quantity {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }
    .count {
      min-width: 64px;
      text-align: center;
    }
    .full {
      width: 100%;
    }
    .danger {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
    }
  `,
})
export class VoidDialog {
  private readonly dialogRef = inject(MatDialogRef<VoidDialog, VoidRequest>);
  protected readonly data = inject<VoidDialogData>(MAT_DIALOG_DATA);

  protected readonly reasonRequired = this.data.type !== 'BeforeKitchen';
  protected readonly quantity = signal(this.data.item?.quantity ?? 1);
  protected readonly reason = signal('');
  protected readonly valid = computed(() => !this.reasonRequired || this.reason().trim().length > 0);

  private readonly verb = this.data.type === 'AfterPayment' ? 'Refund' : 'Void';

  protected readonly title = this.data.item
    ? `${this.verb} ${itemName(this.data.item)}`
    : `${this.verb} the whole order · ${orderLabel(this.data.order)}`;

  protected readonly icon =
    this.data.type === 'AfterPayment' ? 'currency_exchange' : this.data.type === 'AfterKitchen' ? 'soup_kitchen' : 'remove_shopping_cart';

  protected readonly explanation = (() => {
    const customer = this.data.order.userId !== null;
    switch (this.data.type) {
      case 'BeforeKitchen':
        return this.data.item ? 'Not in the kitchen yet: it simply comes off the bill.' : 'Nothing has been paid: the order is cancelled.';
      case 'AfterKitchen':
        return 'The kitchen already has it: it comes off the bill, the kitchen gets a void ticket, and what was made is written off as waste.';
      case 'AfterPayment':
        return this.data.item
          ? `Already paid: this refunds its share of what was charged.${
              customer ? " Its share of any points redeemed goes back to the customer's account, and what it earned is taken back." : ''
            }`
          : `Already paid: everything charged is refunded.${
              customer ? " Any points redeemed go back to the customer's account, and what the order earned is taken back." : ''
            }`;
    }
  })();

  protected step(by: number): void {
    const max = this.data.item?.quantity ?? 1;
    this.quantity.update((q) => Math.min(max, Math.max(1, q + by)));
  }

  protected confirm(): void {
    if (this.valid()) {
      this.dialogRef.close({ quantity: this.quantity(), reason: this.reason().trim() || null });
    }
  }
}
