import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CostingApi, UnitOfMeasure } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { PurchaseCategoryField } from './purchase-category-field';

export interface QuickMaterialData {
  // What was typed in the invoice line.
  name: string;
}

// The new material, as the till saved it: its id goes on the invoice line.
export interface QuickMaterialResult {
  id: string;
  name: string;
}

// A raw material missing from the list, added from the invoice without leaving it: only what a
// delivery needs (name, category, units, yield). Its price is the invoice line's own. The code,
// reorder level and the rest can be filled in later on the Raw materials tab.
@Component({
  selector: 'app-quick-material-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, PurchaseCategoryField, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>Add a raw material <span class="ar">إضافة خامة</span></h2>
    <mat-dialog-content>
      <p class="hint">Added now, it goes on this line of the invoice. Its price comes from the line.</p>
      <form [formGroup]="form" (ngSubmit)="save()" id="quick-material" class="form">
        <mat-form-field class="wide">
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" cdkFocusInitial />
        </mat-form-field>
        <app-purchase-category-field class="wide" formControlName="categoryId" />
        <mat-form-field>
          <mat-label>Counted in · وحدة العد</mat-label>
          <mat-select formControlName="unit" (selectionChange)="unitChanged($event.value)">
            <mat-option value="Gram">Grams (g)</mat-option>
            <mat-option value="Millilitre">Millilitres (ml)</mat-option>
            <mat-option value="Piece">Pieces (pc)</mat-option>
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Yield % · نسبة الصافي</mat-label>
          <input matInput type="number" min="1" max="100" step="any" formControlName="yield" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Bought by · وحدة الشراء</mat-label>
          <input matInput formControlName="purchaseUnit" placeholder="kg, carton" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ baseLabel() }} in one {{ form.value.purchaseUnit || 'unit' }}</mat-label>
          <input matInput type="number" min="0" step="any" formControlName="purchaseUnitSize" />
        </mat-form-field>
      </form>
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close [disabled]="saving()">Cancel</button>
      <button mat-flat-button type="submit" form="quick-material" [disabled]="form.invalid || saving()">Add and use it</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(180px, 1fr));
      gap: 8px 12px;
    }
    .wide {
      grid-column: 1 / -1;
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
    @media (max-width: 560px) {
      .form {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class QuickMaterialDialog {
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<QuickMaterialDialog, QuickMaterialResult>);
  protected readonly data = inject<QuickMaterialData>(MAT_DIALOG_DATA);

  private readonly defaults: Record<UnitOfMeasure, [string, number]> = { Gram: ['kg', 1000], Millilitre: ['L', 1000], Piece: ['piece', 1] };
  private readonly baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly form = new FormGroup({
    name: new FormControl(this.data.name.trim(), { nonNullable: true, validators: [Validators.required] }),
    categoryId: new FormControl<string | null>(null),
    unit: new FormControl<UnitOfMeasure>('Gram', { nonNullable: true }),
    purchaseUnit: new FormControl('kg', { nonNullable: true, validators: [Validators.required] }),
    purchaseUnitSize: new FormControl(1000, { nonNullable: true, validators: [Validators.required, Validators.min(0.001)] }),
    yield: new FormControl(100, { nonNullable: true, validators: [Validators.required, Validators.min(0.01), Validators.max(100)] }),
  });

  protected baseLabel(): string {
    return this.baseLabels[this.form.getRawValue().unit];
  }

  protected unitChanged(unit: UnitOfMeasure): void {
    const [name, size] = this.defaults[unit];
    this.form.patchValue({ purchaseUnit: name, purchaseUnitSize: size });
  }

  // Saved here, so a refusal is shown with the dialog still open and nothing typed lost.
  protected async save(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.problem.set(null);
    try {
      const created = (await firstValueFrom(
        this.api.saveMaterial(null, {
          name: v.name.trim(),
          unit: v.unit,
          reorderLevel: 0,
          code: null,
          categoryId: v.categoryId,
          purchaseUnit: v.purchaseUnit.trim(),
          purchaseUnitSize: v.purchaseUnitSize,
          defaultYieldPercent: v.yield,
          costPerPurchaseUnit: null,
        }),
      )) as { id: string; name: string };
      this.ref.close({ id: created.id, name: created.name });
    } catch (error) {
      this.problem.set(errorMessage(error, 'The material could not be added.'));
    } finally {
      this.saving.set(false);
    }
  }
}
