import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CustomerLookupResult, Order } from '../../core/api/models';
import { errorMessage } from '../../core/http/error-message';
import { maxPointsFor, pointsDiscount } from '../../core/orders/order-rules';
import { CustomersApi } from '../../core/orders/orders.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

export interface PointsDialogData {
  order: Order;
}

// Loyalty points off the bill: Discount = Points / 10 at the default rate, whatever rate the
// delivery system has set. The balance and the rate are fetched fresh, because the points are
// the customer's everywhere: they may have spent some online since the order was opened.
@Component({
  selector: 'app-points-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Loyalty points</h2>
    <mat-dialog-content>
      @if (lookup(); as result) {
        @if (result.profile; as profile) {
          <p class="balance">
            <mat-icon>loyalty</mat-icon>
            <span>
              <strong>{{ profile.fullName }}</strong> has {{ profile.pointsBalance }} points, worth
              {{ result.pointsValue | money }}.
            </span>
          </p>
          <p class="rate">
            {{ pointsPerPound() }} points = L.E 1 · this bill can take up to {{ max() }} points.
          </p>
          <mat-form-field class="full">
            <mat-label>Points to use</mat-label>
            <input matInput type="number" min="1" [max]="max()" [ngModel]="points()" (ngModelChange)="points.set($event)" cdkFocusInitial />
            <mat-hint>{{ discount() | money }} off</mat-hint>
          </mat-form-field>
          <div class="quick">
            <button mat-stroked-button (click)="points.set(max())" [disabled]="max() === 0">Use the most ({{ max() }})</button>
          </div>
        } @else {
          <p>This number has no account in the delivery system, so there are no points to use.</p>
        }
      } @else if (error(); as message) {
        <p class="error" role="alert">{{ message }}</p>
      } @else {
        <mat-progress-bar mode="indeterminate" />
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="apply()" [disabled]="!valid()">Apply {{ discount() | money }} off</button>
    </mat-dialog-actions>
  `,
  styles: `
    .balance {
      display: flex;
      gap: 10px;
      padding: 12px;
      border-radius: 12px;
      margin-top: 0;
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .rate {
      color: var(--mat-sys-on-surface-variant);
    }
    .full {
      width: 100%;
    }
    .quick {
      margin-top: 8px;
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class PointsDialog implements OnInit {
  private readonly dialogRef = inject(MatDialogRef<PointsDialog, number>);
  private readonly customers = inject(CustomersApi);
  private readonly order = inject<PointsDialogData>(MAT_DIALOG_DATA).order;

  protected readonly lookup = signal<CustomerLookupResult | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly points = signal<number | null>(null);

  private readonly rate = computed(() => this.lookup()?.redemptionValuePer100Points ?? 0);
  protected readonly pointsPerPound = computed(() => (this.rate() > 0 ? 100 / this.rate() : 0));

  // The bill before points, as OrderPricing measures the cap.
  protected readonly max = computed(() => {
    const balance = this.lookup()?.profile?.pointsBalance ?? 0;
    const billBeforePoints = this.order.totalAmount + this.order.pointsDiscountAmount;
    return Math.min(balance, maxPointsFor(billBeforePoints, this.rate()));
  });

  protected readonly discount = computed(() => pointsDiscount(this.points() ?? 0, this.rate()));

  protected readonly valid = computed(() => {
    const points = this.points();
    return points !== null && Number.isInteger(points) && points >= 1 && points <= this.max();
  });

  async ngOnInit(): Promise<void> {
    try {
      const result = await firstValueFrom(this.customers.findByPhone(this.order.customerPhone));
      if (result.status === 'Unavailable') {
        this.error.set('The delivery system cannot be reached, so points cannot be used right now.');
        return;
      }
      this.lookup.set(result);
    } catch (error) {
      this.error.set(errorMessage(error, "The customer's points could not be fetched."));
    }
  }

  protected apply(): void {
    if (this.valid()) {
      this.dialogRef.close(this.points()!);
    }
  }
}
