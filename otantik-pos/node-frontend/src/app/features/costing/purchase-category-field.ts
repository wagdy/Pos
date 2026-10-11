import { Component, forwardRef, inject, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelect, MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PurchaseCategory } from '../../core/costing/costing.api';
import { PurchaseCategoryDialog, PurchaseCategoryDialogData } from './purchase-category-dialog';

// The purchase category a material or an expense item is filed under, as a form field: General
// first, then the rest by name, and a last option to add one there and then. Its value is the
// category's id; none given is General, which every list has.
@Component({
  selector: 'app-purchase-category-field',
  imports: [MatFormFieldModule, MatSelectModule],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => PurchaseCategoryField), multi: true }],
  template: `
    <mat-form-field class="field" subscriptSizing="dynamic">
      <mat-label>Category · الفئة</mat-label>
      <mat-select #select [value]="value()" (selectionChange)="chosen($event.value, select)" [disabled]="disabled()" aria-label="Purchase category">
        @for (c of categories(); track c.id) {
          <mat-option [value]="c.id">{{ c.name }}{{ c.nameAr ? ' · ' + c.nameAr : '' }}</mat-option>
        }
        <mat-option [value]="newCategory" class="add-new">+ New category…</mat-option>
      </mat-select>
      @if (hint()) {
        <mat-hint>{{ hint() }}</mat-hint>
      }
    </mat-form-field>
  `,
  styles: `
    :host {
      display: block;
    }
    .field {
      width: 100%;
    }
  `,
})
export class PurchaseCategoryField implements ControlValueAccessor {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);

  readonly hint = input<string | null>(null);

  protected readonly newCategory = '+new';
  protected readonly categories = signal<PurchaseCategory[]>([]);
  protected readonly value = signal<string | null>(null);
  protected readonly disabled = signal(false);

  private onChange: (value: string | null) => void = () => undefined;
  private onTouched: () => void = () => undefined;

  constructor() {
    void this.load();
  }

  writeValue(value: string | null): void {
    this.value.set(value ?? null);
    this.fillGeneral();
  }

  registerOnChange(fn: (value: string | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(disabled: boolean): void {
    this.disabled.set(disabled);
  }

  // A category chosen, or the last option: a new one, added and chosen; cancelled, the field
  // keeps the one it had.
  protected async chosen(value: string, select: MatSelect): Promise<void> {
    this.onTouched();
    if (value !== this.newCategory) {
      this.set(value);
      return;
    }
    select.value = this.value();
    const created = await firstValueFrom(
      this.dialog
        .open<PurchaseCategoryDialog, PurchaseCategoryDialogData, PurchaseCategory>(PurchaseCategoryDialog, { data: { fromItem: true }, width: '460px' })
        .afterClosed(),
    );
    if (!created) {
      return;
    }
    await this.load();
    this.set(created.id);
  }

  private set(value: string | null): void {
    this.value.set(value);
    this.onChange(value);
  }

  // Nothing chosen yet: General, as the till files it.
  private fillGeneral(): void {
    if (this.value() !== null) {
      return;
    }
    const general = this.categories().find((c) => c.isGeneral);
    if (general) {
      this.set(general.id);
    }
  }

  private async load(): Promise<void> {
    try {
      this.categories.set(await firstValueFrom(this.api.purchaseCategories()));
      this.fillGeneral();
    } catch {
      // The field stays empty; saving without one files it under General.
    }
  }
}
