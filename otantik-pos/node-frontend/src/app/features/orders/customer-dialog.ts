import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CustomerLookupResult, OrderType } from '../../core/api/models';
import { ConnectivityService } from '../../core/connectivity/connectivity.service';
import { errorMessage } from '../../core/http/error-message';
import { CustomersApi } from '../../core/orders/orders.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

export interface CustomerDialogData {
  type: OrderType;
}

export interface CustomerDetails {
  customerPhone: string;
  customerName: string | null;
  deliveryAddress: string | null;
  deliveryFee: number;
  lookup: CustomerLookupResult | null;
}

const phonePattern = /^\+?[0-9]{8,15}$/;

// The mobile-number prompt for takeaway and delivery. Mandatory: opened with disableClose, so
// there is no way past it but entering a number or cancelling the order.
//
// The number finds the customer's profile in the delivery system, their points, and what the
// points are worth, so the cashier can offer them. With the delivery system offline the number
// is still taken and goes on the order; the profile and points wait until it is back.
@Component({
  selector: 'app-customer-dialog',
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MoneyPipe,
    ReactiveFormsModule,
  ],
  templateUrl: './customer-dialog.html',
  styleUrl: './customer-dialog.scss',
})
export class CustomerDialog {
  private readonly dialogRef = inject(MatDialogRef<CustomerDialog, CustomerDetails>);
  private readonly customers = inject(CustomersApi);
  protected readonly connectivity = inject(ConnectivityService);
  protected readonly data = inject<CustomerDialogData>(MAT_DIALOG_DATA);

  protected readonly isDelivery = this.data.type === 'Delivery';

  protected readonly form = new FormGroup({
    phone: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(phonePattern)] }),
    name: new FormControl('', { nonNullable: true }),
    address: new FormControl('', { nonNullable: true, validators: this.isDelivery ? [Validators.required] : [] }),
    fee: new FormControl(0, { nonNullable: true, validators: [Validators.min(0)] }),
  });

  protected readonly looking = signal(false);
  protected readonly lookup = signal<CustomerLookupResult | null>(null);
  protected readonly lookupError = signal<string | null>(null);

  private readonly formStatus = toSignal(this.form.statusChanges, { initialValue: this.form.status });
  private readonly phone = toSignal(this.form.controls.phone.valueChanges, { initialValue: '' });
  private readonly lookedUpPhone = signal<string | null>(null);

  // Looked up for this very number, or no way to look it up right now.
  protected readonly canContinue = computed(
    () =>
      this.formStatus() === 'VALID' &&
      (!this.connectivity.cloudOnline() || this.lookedUpPhone() === this.phone().trim()),
  );

  protected async lookUp(): Promise<void> {
    const phone = this.form.controls.phone;
    phone.markAsTouched();
    if (phone.invalid || this.looking()) {
      return;
    }
    this.looking.set(true);
    this.lookupError.set(null);
    try {
      const result = await firstValueFrom(this.customers.findByPhone(phone.value));
      this.lookup.set(result);
      this.lookedUpPhone.set(phone.value.trim());
      if (result.profile && !this.form.controls.name.value) {
        this.form.controls.name.setValue(result.profile.fullName);
      }
    } catch (error) {
      this.lookupError.set(errorMessage(error, 'The customer could not be looked up.'));
    } finally {
      this.looking.set(false);
    }
  }

  // A different number makes the last lookup about someone else.
  protected phoneEdited(): void {
    if (this.lookedUpPhone() !== null && this.lookedUpPhone() !== this.form.controls.phone.value.trim()) {
      this.lookup.set(null);
      this.lookedUpPhone.set(null);
    }
  }

  protected confirm(): void {
    if (!this.canContinue()) {
      this.form.markAllAsTouched();
      return;
    }
    const value = this.form.getRawValue();
    this.dialogRef.close({
      customerPhone: value.phone.trim(),
      customerName: value.name.trim() || null,
      deliveryAddress: this.isDelivery ? value.address.trim() : null,
      deliveryFee: this.isDelivery ? value.fee : 0,
      lookup: this.lookup(),
    });
  }
}
