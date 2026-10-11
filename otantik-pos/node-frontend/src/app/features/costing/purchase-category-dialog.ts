import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CostingApi, ExpenseLine, PurchaseCategory, expenseLineLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';

// A category to change, or none for a new one; fromItem when added from a material's or an
// expense's form, which then goes into it.
export interface PurchaseCategoryDialogData {
  category?: PurchaseCategory;
  fromItem?: boolean;
}

// A purchase category: its name, in English and optionally Arabic, and where expenses filed
// under it count in the profit and loss. Closes with the category as the till saved it; a
// refusal (the name already taken) keeps it open with all typed.
@Component({
  selector: 'app-purchase-category-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.category) {
        Edit category <span class="ar">تعديل الفئة</span>
      } @else {
        Add a purchase category <span class="ar">إضافة فئة مشتريات</span>
      }
    </h2>
    <mat-dialog-content>
      @if (data.fromItem) {
        <p class="hint">Added now, the item goes into it.</p>
      }
      <form [formGroup]="form" (ngSubmit)="save()" id="purchase-category-form" class="form">
        <mat-form-field>
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" maxlength="100" placeholder="Utilities" cdkFocusInitial />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Arabic name · الاسم بالعربية</mat-label>
          <input matInput formControlName="nameAr" maxlength="100" dir="rtl" placeholder="المرافق" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Expenses count as · تُحتسب المصروفات ضمن</mat-label>
          <mat-select formControlName="expenseLine">
            @for (line of lines; track line) {
              <mat-option [value]="line">{{ labels[line].en }} · {{ labels[line].ar }}</mat-option>
            }
          </mat-select>
          <mat-hint>{{ labels[form.controls.expenseLine.value].hint }}. Stock bought under it is cost of sales instead.</mat-hint>
        </mat-form-field>
      </form>
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close [disabled]="saving()">Cancel</button>
      <button mat-flat-button type="submit" form="purchase-category-form" [disabled]="form.invalid || saving()">
        {{ data.fromItem ? 'Add and use it' : 'Save' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      gap: 4px;
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
export class PurchaseCategoryDialog {
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<PurchaseCategoryDialog, PurchaseCategory>);
  protected readonly data = inject<PurchaseCategoryDialogData>(MAT_DIALOG_DATA);

  protected readonly labels = expenseLineLabels;
  protected readonly lines = Object.keys(expenseLineLabels) as ExpenseLine[];
  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly form = new FormGroup({
    name: new FormControl(this.data.category?.name ?? '', { nonNullable: true, validators: [Validators.required, Validators.pattern(/\S/)] }),
    nameAr: new FormControl(this.data.category?.nameAr ?? '', { nonNullable: true }),
    expenseLine: new FormControl<ExpenseLine>(this.data.category?.expenseLine ?? 'Controllable', { nonNullable: true }),
  });

  // The id is made here for a new one: a retry after a lost answer adds it once.
  private readonly id = this.data.category?.id ?? crypto.randomUUID();

  protected async save(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.problem.set(null);
    try {
      this.ref.close(
        await firstValueFrom(this.api.savePurchaseCategory(this.id, { name: v.name.trim(), nameAr: v.nameAr.trim() || null, expenseLine: v.expenseLine })),
      );
    } catch (error) {
      this.problem.set(errorMessage(error, 'The category could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
