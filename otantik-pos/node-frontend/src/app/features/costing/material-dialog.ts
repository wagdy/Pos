import { Component, inject } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MaterialCost, SaveMaterial, UnitOfMeasure, unitLabels } from '../../core/costing/costing.api';

// A raw material, new or to change. Its unit is fixed once it exists: every recipe and stock
// entry for it is in that unit. Cost and reorder level are entered per purchase unit (a kg, a
// carton), as they are bought.
@Component({
  selector: 'app-material-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Change ' + data.name : 'New raw material' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" (ngSubmit)="save()" id="material-form" class="form">
        <mat-form-field class="wide">
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" cdkFocusInitial />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Code · الكود</mat-label>
          <input matInput formControlName="code" placeholder="ING-001" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Category · الفئة</mat-label>
          <input matInput formControlName="category" placeholder="Dairy" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Counted in · وحدة العد</mat-label>
          <mat-select formControlName="unit" (selectionChange)="unitChanged($event.value)">
            <mat-option value="Gram">Grams (g)</mat-option>
            <mat-option value="Millilitre">Millilitres (ml)</mat-option>
            <mat-option value="Piece">Pieces (pc)</mat-option>
          </mat-select>
          @if (data) {
            <mat-hint>Fixed once created</mat-hint>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label>Bought by · وحدة الشراء</mat-label>
          <input matInput formControlName="purchaseUnit" placeholder="kg, carton" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ unitLabel() }} in one {{ form.value.purchaseUnit || 'unit' }}</mat-label>
          <input matInput type="number" min="0" step="any" formControlName="purchaseUnitSize" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>L.E per {{ form.value.purchaseUnit || 'unit' }} (AP$) · تكلفة الشراء</mat-label>
          <input matInput type="number" min="0" step="any" formControlName="cost" />
          <mat-hint>{{ data ? 'Replaces the average cost' : 'Priced purchases update it' }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Yield % · نسبة الصافي</mat-label>
          <input matInput type="number" min="1" max="100" step="any" formControlName="yield" />
          <mat-hint>Usable after trimming</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Reorder at ({{ form.value.purchaseUnit || 'units' }}) · حد الطلب</mat-label>
          <input matInput type="number" min="0" step="any" formControlName="reorderAt" />
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="material-form" [disabled]="form.invalid">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(200px, 1fr));
      gap: 8px 12px;
      padding-top: 4px;
    }
    .wide {
      grid-column: 1 / -1;
    }
    @media (max-width: 560px) {
      .form {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class MaterialDialog {
  protected readonly data = inject<MaterialCost | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<MaterialDialog, SaveMaterial>);

  private readonly defaults: Record<UnitOfMeasure, [string, number]> = { Gram: ['kg', 1000], Millilitre: ['L', 1000], Piece: ['piece', 1] };

  protected readonly form = new FormGroup({
    name: new FormControl(this.data?.name ?? '', { nonNullable: true, validators: [Validators.required] }),
    code: new FormControl(this.data?.code ?? '', { nonNullable: true }),
    category: new FormControl(this.data?.category ?? '', { nonNullable: true }),
    unit: new FormControl<UnitOfMeasure>({ value: this.data?.unit ?? 'Gram', disabled: !!this.data }, { nonNullable: true }),
    purchaseUnit: new FormControl(this.data?.purchaseUnit ?? 'kg', { nonNullable: true, validators: [Validators.required] }),
    purchaseUnitSize: new FormControl(this.data?.purchaseUnitSize ?? 1000, {
      nonNullable: true,
      validators: [Validators.required, Validators.min(0.001)],
    }),
    cost: new FormControl<number | null>(this.data?.costPerPurchaseUnit ?? null, { validators: [Validators.min(0)] }),
    yield: new FormControl(this.data?.defaultYieldPercent ?? 100, {
      nonNullable: true,
      validators: [Validators.required, Validators.min(0.01), Validators.max(100)],
    }),
    reorderAt: new FormControl(this.data ? this.data.reorderLevel / this.data.purchaseUnitSize : 0, {
      nonNullable: true,
      validators: [Validators.min(0)],
    }),
  });

  protected unitLabel(): string {
    return unitLabels[this.form.getRawValue().unit];
  }

  protected unitChanged(unit: UnitOfMeasure): void {
    const [name, size] = this.defaults[unit];
    this.form.patchValue({ purchaseUnit: name, purchaseUnitSize: size });
  }

  protected save(): void {
    if (this.form.invalid) {
      return;
    }
    const v = this.form.getRawValue();
    // An unchanged cost is not sent: sending it would reset the average to the rounded figure shown.
    const costChanged = !this.data || v.cost !== this.data.costPerPurchaseUnit;
    this.ref.close({
      name: v.name.trim(),
      unit: this.data ? undefined : v.unit,
      code: v.code.trim() || null,
      category: v.category.trim() || null,
      purchaseUnit: v.purchaseUnit.trim(),
      purchaseUnitSize: v.purchaseUnitSize,
      defaultYieldPercent: v.yield,
      costPerPurchaseUnit: costChanged ? v.cost : null,
      reorderLevel: v.reorderAt * v.purchaseUnitSize,
    });
  }
}
