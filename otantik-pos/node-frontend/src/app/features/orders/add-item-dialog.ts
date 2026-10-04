import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { AddOrderItemRequest, MenuItem } from '../../core/api/models';
import { addItemProblem } from '../../core/orders/order-rules';
import { MoneyPipe } from '../../core/ui/money.pipe';

export interface AddItemDialogData {
  menuItem: MenuItem;
}

// Size, add-ons, how many and any note for the kitchen. The same rules as the API's
// OrderItemBuilder are checked here first, so Add is only offered when it will be accepted.
@Component({
  selector: 'app-add-item-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatRadioModule,
    MoneyPipe,
  ],
  template: `
    <h2 mat-dialog-title>{{ item.name }}</h2>
    <mat-dialog-content>
      @if (item.description) {
        <p class="description">{{ item.description }}</p>
      }

      @if (item.variants.length > 0) {
        <h3>Size</h3>
        <mat-radio-group class="options" [ngModel]="variantId()" (ngModelChange)="variantId.set($event)" aria-label="Size">
          @for (variant of item.variants; track variant.id) {
            <mat-radio-button [value]="variant.id" [disabled]="!variant.isAvailable">
              {{ variant.name }} · {{ variant.price | money }}
              @if (!variant.isAvailable) {
                <span class="sold-out">sold out</span>
              }
            </mat-radio-button>
          }
        </mat-radio-group>
      }

      @if (item.menuItemAddOns.length > 0) {
        <h3>Add-ons</h3>
        <div class="options">
          @for (link of item.menuItemAddOns; track link.addOnId) {
            <mat-checkbox [checked]="addOnIds().includes(link.addOnId)" (change)="toggle(link.addOnId, $event.checked)">
              {{ link.addOn.name }} · +{{ link.addOn.price | money }}
            </mat-checkbox>
          }
        </div>
      }

      <h3>Quantity</h3>
      <div class="quantity">
        <button mat-icon-button (click)="step(-1)" [disabled]="quantity() <= 1" aria-label="One fewer">
          <mat-icon>remove</mat-icon>
        </button>
        <span class="count" aria-live="polite">{{ quantity() }}</span>
        <button mat-icon-button (click)="step(1)" [disabled]="quantity() >= 100" aria-label="One more">
          <mat-icon>add</mat-icon>
        </button>
      </div>

      <mat-form-field class="full">
        <mat-label>Note for the kitchen</mat-label>
        <input matInput [ngModel]="notes()" (ngModelChange)="notes.set($event)" maxlength="200" autocomplete="off" />
      </mat-form-field>

      @if (problem(); as message) {
        <p class="problem" role="status">{{ message }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="add()" [disabled]="problem() !== null">
        Add · {{ total() | money }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .description {
      margin-top: 0;
      color: var(--mat-sys-on-surface-variant);
    }
    h3 {
      margin: 16px 0 4px;
      font: var(--mat-sys-title-small);
    }
    .options {
      display: grid;
      gap: 2px;
    }
    .sold-out {
      margin-left: 6px;
      color: var(--mat-sys-error);
    }
    .quantity {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .count {
      min-width: 32px;
      text-align: center;
      font: var(--mat-sys-headline-small);
    }
    .full {
      width: 100%;
      margin-top: 12px;
    }
    .problem {
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
  `,
})
export class AddItemDialog {
  private readonly dialogRef = inject(MatDialogRef<AddItemDialog, AddOrderItemRequest>);
  protected readonly item = inject<AddItemDialogData>(MAT_DIALOG_DATA).menuItem;

  // Preselected when there is only one size to have.
  protected readonly variantId = signal<number | null>(
    this.item.variants.filter((v) => v.isAvailable).length === 1 ? this.item.variants.find((v) => v.isAvailable)!.id : null,
  );
  protected readonly addOnIds = signal<number[]>([]);
  protected readonly quantity = signal(1);
  protected readonly notes = signal('');

  protected readonly problem = computed(() => addItemProblem(this.item, this.variantId(), this.addOnIds()));

  protected readonly total = computed(() => {
    const variant = this.item.variants.find((v) => v.id === this.variantId());
    const base = variant?.price ?? this.item.price;
    const addOns = this.item.menuItemAddOns
      .filter((link) => this.addOnIds().includes(link.addOnId))
      .reduce((sum, link) => sum + link.addOn.price, 0);
    // As OrderItemBuilder prices it: the size's price, or the item's, plus the add-ons.
    return this.quantity() * (base + addOns);
  });

  protected toggle(addOnId: number, checked: boolean): void {
    this.addOnIds.update((ids) => (checked ? [...ids, addOnId] : ids.filter((id) => id !== addOnId)));
  }

  protected step(by: number): void {
    this.quantity.update((q) => Math.min(100, Math.max(1, q + by)));
  }

  protected add(): void {
    if (this.problem() !== null) {
      return;
    }
    this.dialogRef.close({
      menuItemId: this.item.id,
      quantity: this.quantity(),
      variantId: this.variantId(),
      addOnIds: this.addOnIds(),
      notes: this.notes().trim() || null,
    });
  }
}
