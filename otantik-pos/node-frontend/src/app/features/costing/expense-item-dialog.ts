import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { CostingApi, ExpenseItem } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { PurchaseCategoryField } from './purchase-category-field';

// One of: an expense item to change, a name typed on an invoice to add, or neither for a new one.
export interface ExpenseItemDialogData {
  item?: ExpenseItem;
  name?: string;
  // Opened from an invoice: the expense goes on the line once saved.
  fromInvoice?: boolean;
}

// An expense item (المصروفات العمومية): something bought that is never stock, such as cleaning
// supplies, a repair or a bill. Its name, its category, which says where it counts in the profit
// and loss, and what a line of it is usually counted in. Closes with the item as the till saved
// it; a refusal (the name already taken) keeps it open with all typed.
@Component({
  selector: 'app-expense-item-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, PurchaseCategoryField, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.item) {
        Edit expense item <span class="ar">تعديل بند مصروف</span>
      } @else {
        Add an expense item <span class="ar">إضافة بند مصروف</span>
      }
    </h2>
    <mat-dialog-content>
      <p class="hint">
        @if (data.fromInvoice) {
          Added now, it goes on this line of the invoice.
        }
        Never stock: not counted, not in recipes. Posted, an invoice records only what it cost.
      </p>
      <form [formGroup]="form" (ngSubmit)="save()" id="expense-item-form" class="form">
        <mat-form-field>
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" maxlength="200" placeholder="Electricity" cdkFocusInitial />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Arabic name · الاسم بالعربية</mat-label>
          <input matInput formControlName="nameAr" maxlength="200" dir="rtl" placeholder="الكهرباء" />
        </mat-form-field>
        <app-purchase-category-field formControlName="categoryId" kind="Expenses" hint="Its category says where it counts in the P&L." />
        <mat-form-field>
          <mat-label>Usually counted in · الوحدة</mat-label>
          <input matInput formControlName="unit" maxlength="32" placeholder="bottle, visit, month" />
          <mat-hint>Optional: a bill is just its amount.</mat-hint>
        </mat-form-field>
      </form>
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close [disabled]="saving()">Cancel</button>
      <button mat-flat-button type="submit" form="expense-item-form" [disabled]="form.invalid || saving()">
        {{ data.fromInvoice ? 'Add and use it' : 'Save' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      gap: 8px;
      min-width: min(400px, 80vw);
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .hint {
      margin: 0 0 12px;
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class ExpenseItemDialog {
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<ExpenseItemDialog, ExpenseItem>);
  protected readonly data = inject<ExpenseItemDialogData>(MAT_DIALOG_DATA);

  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly form = new FormGroup({
    name: new FormControl((this.data.item?.name ?? this.data.name ?? '').trim(), {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/)],
    }),
    nameAr: new FormControl(this.data.item?.nameAr ?? '', { nonNullable: true }),
    categoryId: new FormControl<string | null>(this.data.item?.categoryId ?? null),
    unit: new FormControl(this.data.item?.unit ?? '', { nonNullable: true }),
  });

  // The id is made here for a new one: a retry after a lost answer adds it once.
  private readonly id = this.data.item?.id ?? crypto.randomUUID();

  protected async save(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.problem.set(null);
    try {
      this.ref.close(
        await firstValueFrom(
          this.api.saveExpenseItem(this.id, { name: v.name.trim(), nameAr: v.nameAr.trim() || null, categoryId: v.categoryId, unit: v.unit.trim() || null }),
        ),
      );
    } catch (error) {
      this.problem.set(errorMessage(error, 'The expense item could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
