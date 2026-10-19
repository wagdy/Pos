import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CategoryKind, CostingApi, ExpenseLine, PurchaseCategory, categoryKindLabels, expenseLineLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';

// A category to change, or none for a new one; fromItem when added from a material's or an
// expense's form, which then goes into it, and kind the kind that form files (then not asked).
export interface PurchaseCategoryDialogData {
  category?: PurchaseCategory;
  fromItem?: boolean;
  kind?: CategoryKind;
}

// A purchase category: what it files (raw materials or general expenses, asked of a new one), its
// name, in English and optionally Arabic, and, for expenses, where they count in the profit and
// loss. Closes with the category as the till saved it; a refusal (the name already taken, or a
// kind it cannot take while it holds the other) keeps it open with all typed.
@Component({
  selector: 'app-purchase-category-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatRadioModule, MatSelectModule, ReactiveFormsModule],
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
        <div class="kind">
          <span class="kind-label" id="category-kind-label">Is this category for · هذا القسم لـ</span>
          <mat-radio-group formControlName="kind" aria-labelledby="category-kind-label">
            @for (k of kinds; track k) {
              <mat-radio-button [value]="k">{{ kindLabels[k].en }} · {{ kindLabels[k].ar }}</mat-radio-button>
            }
          </mat-radio-group>
          @if (data.category?.isGeneral) {
            <span class="hint">Where anything not yet sorted goes: it stays {{ kindLabels[data.category!.kind].en.toLowerCase() }}.</span>
          }
        </div>
        <mat-form-field>
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" maxlength="100" placeholder="Utilities" cdkFocusInitial />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Arabic name · الاسم بالعربية</mat-label>
          <input matInput formControlName="nameAr" maxlength="100" dir="rtl" placeholder="المرافق" />
        </mat-form-field>
        @if (form.controls.kind.value === 'Expenses') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Expenses count as · تُحتسب المصروفات ضمن</mat-label>
            <mat-select formControlName="expenseLine">
              @for (line of lines; track line) {
                <mat-option [value]="line">{{ labels[line].en }} · {{ labels[line].ar }}</mat-option>
              }
            </mat-select>
            <mat-hint>{{ labels[form.controls.expenseLine.value].hint }}.</mat-hint>
          </mat-form-field>
        }
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
    .kind {
      display: flex;
      flex-direction: column;
      gap: 2px;
      margin-bottom: 8px;
    }
    .kind mat-radio-group {
      display: flex;
      flex-wrap: wrap;
      gap: 0 12px;
    }
    .kind-label {
      font: var(--mat-sys-title-small);
    }
  `,
})
export class PurchaseCategoryDialog {
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<PurchaseCategoryDialog, PurchaseCategory>);
  protected readonly data = inject<PurchaseCategoryDialogData>(MAT_DIALOG_DATA);

  protected readonly labels = expenseLineLabels;
  protected readonly lines = Object.keys(expenseLineLabels) as ExpenseLine[];
  protected readonly kindLabels = categoryKindLabels;
  protected readonly kinds: CategoryKind[] = ['RawMaterials', 'Expenses'];
  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly form = new FormGroup({
    name: new FormControl(this.data.category?.name ?? '', { nonNullable: true, validators: [Validators.required, Validators.pattern(/\S/)] }),
    nameAr: new FormControl(this.data.category?.nameAr ?? '', { nonNullable: true }),
    expenseLine: new FormControl<ExpenseLine>(this.data.category?.expenseLine ?? 'Controllable', { nonNullable: true }),
    // Asked of a new one; given by the form it is added from, and a General's is fixed.
    kind: new FormControl<CategoryKind | null>(
      { value: this.data.category?.kind ?? this.data.kind ?? null, disabled: !!this.data.kind || !!this.data.category?.isGeneral },
      { validators: [Validators.required] },
    ),
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
        await firstValueFrom(
          this.api.savePurchaseCategory(this.id, { name: v.name.trim(), nameAr: v.nameAr.trim() || null, expenseLine: v.expenseLine, kind: v.kind! }),
        ),
      );
    } catch (error) {
      this.problem.set(errorMessage(error, 'The category could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
