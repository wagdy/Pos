import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { OrderType } from '../../core/api/models';

export interface NewOrderChoice {
  type: OrderType;
  tableNumber?: string;
}

// Step one of a new order: what kind. A dine-in table only needs its number. Takeaway and
// delivery go on to the customer's mobile number, which the caller asks for next, every time.
@Component({
  selector: 'app-new-order-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>New order</h2>
    <mat-dialog-content>
      @if (!dineIn()) {
        <div class="types">
          <button mat-stroked-button class="type" (click)="dineIn.set(true)">
            <mat-icon>table_restaurant</mat-icon>
            Dine-in
          </button>
          <button mat-stroked-button class="type" (click)="choose('Takeaway')">
            <mat-icon>takeout_dining</mat-icon>
            Takeaway
          </button>
          <button mat-stroked-button class="type" (click)="choose('Delivery')">
            <mat-icon>delivery_dining</mat-icon>
            Delivery
          </button>
        </div>
      } @else {
        <!-- [formGroup] is what makes (ngSubmit) an Angular event that stops the browser's own
             submit. Without it, Open table (and Enter) reloaded the page instead. -->
        <form [formGroup]="form" (ngSubmit)="openTable()" id="table-form">
          <mat-form-field class="full">
            <mat-label>Table number</mat-label>
            <input matInput formControlName="table" inputmode="numeric" autocomplete="off" cdkFocusInitial />
            @if (table.hasError('required') && table.touched) {
              <mat-error>Enter the table number.</mat-error>
            }
          </mat-form-field>
        </form>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (dineIn()) {
        <button mat-button (click)="dineIn.set(false)">Back</button>
        <button mat-flat-button type="submit" form="table-form">Open table</button>
      } @else {
        <button mat-button mat-dialog-close>Cancel</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .types {
      display: grid;
      grid-template-columns: repeat(3, minmax(120px, 1fr));
      gap: 12px;
      padding-top: 4px;
    }
    .type {
      height: 110px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      font: var(--mat-sys-title-medium);
    }
    .type mat-icon {
      transform: scale(1.6);
      margin-bottom: 6px;
    }
    .full {
      width: 100%;
    }
    @media (max-width: 480px) {
      .types {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class NewOrderDialog {
  private readonly dialogRef = inject(MatDialogRef<NewOrderDialog, NewOrderChoice>);

  protected readonly dineIn = signal(false);
  protected readonly table = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  protected readonly form = new FormGroup({ table: this.table });

  protected choose(type: OrderType): void {
    this.dialogRef.close({ type });
  }

  protected openTable(): void {
    const tableNumber = this.table.value.trim();
    if (!tableNumber) {
      this.table.markAsTouched();
      this.table.setErrors({ required: true });
      return;
    }
    this.dialogRef.close({ type: 'DineIn', tableNumber });
  }
}
