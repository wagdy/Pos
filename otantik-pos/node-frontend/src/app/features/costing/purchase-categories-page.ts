import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PurchaseCategory, expenseLineLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { PurchaseCategoryDialog, PurchaseCategoryDialogData } from './purchase-category-dialog';

// The purchase categories (أقسام المشتريات والخامات): what raw materials and expense items are
// filed under, so purchases add up by category, and where each category's expenses count in the
// profit and loss. One holding anything is deleted only after choosing where it moves, so nothing
// is left without one; General, where anything not yet sorted goes, is never deleted.
@Component({
  selector: 'app-purchase-categories-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule],
  template: `
    <p class="note">
      How purchases are grouped: Meat, Dairy, Packaging for raw materials; Utilities, Cleaning, Rent for expense items. Every raw
      material and expense item is in one, and spending is added up by them. For expenses, the category also says where they count in
      the profit and loss.
      <span class="inline-ar">تصنيف المشتريات والخامات والمصروفات.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        <span class="spacer"></span>
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add category</button>
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Category<span class="ar">الفئة</span></th>
              <th>Arabic name<span class="ar">الاسم بالعربية</span></th>
              <th>Expenses count as<span class="ar">تُحتسب المصروفات ضمن</span></th>
              <th class="num">Raw materials<span class="ar">الخامات</span></th>
              <th class="num">Expense items<span class="ar">بنود المصروفات</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of categories(); track c.id) {
              <tr>
                <td>
                  {{ c.name }}
                  @if (c.isGeneral) {
                    <span class="badge">Default · افتراضي</span>
                  }
                </td>
                <td dir="rtl" class="ar-cell">{{ c.nameAr ?? '—' }}</td>
                <td>{{ lines[c.expenseLine].en }}<span class="ar">{{ lines[c.expenseLine].ar }}</span></td>
                <td class="num">{{ c.materials }}</td>
                <td class="num">{{ c.expenseItems }}</td>
                <td class="num actions">
                  <button mat-icon-button (click)="edit(c)" [attr.aria-label]="'Edit ' + c.name"><mat-icon>edit</mat-icon></button>
                  @if (!c.isGeneral) {
                    <button mat-icon-button class="danger" (click)="ask(c)" [disabled]="confirming() === c.id" [attr.aria-label]="'Delete ' + c.name">
                      <mat-icon>delete</mat-icon>
                    </button>
                  }
                </td>
              </tr>
              @if (confirming() === c.id) {
                <tr class="confirm-row">
                  <td colspan="6">
                    <div class="confirm">
                      @if (filed(c); as n) {
                        <span>Delete {{ c.name }}? Its {{ n }} {{ n === 1 ? 'item moves' : 'items move' }}, with their purchases, to</span>
                        <mat-form-field subscriptSizing="dynamic" class="move">
                          <mat-select [(ngModel)]="moveTo" placeholder="Choose a category" aria-label="Move what is in it to">
                            @for (o of others(c); track o.id) {
                              <mat-option [value]="o.id">{{ o.name }}</mat-option>
                            }
                          </mat-select>
                        </mat-form-field>
                      } @else {
                        <span>Delete {{ c.name }}? Nothing is in it.</span>
                      }
                      <span class="spacer"></span>
                      <button mat-button (click)="confirming.set(null)" [disabled]="saving()">Cancel</button>
                      <button mat-flat-button class="danger-fill" (click)="remove(c)" [disabled]="saving() || (filed(c) > 0 && !moveTo)">
                        {{ filed(c) ? 'Move and delete' : 'Delete' }}
                      </button>
                    </div>
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="6" class="muted">No categories yet.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .ar-cell {
      text-align: start;
    }
    .actions {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .danger-fill:not(:disabled) {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
    tr.confirm-row td {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .confirm {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .move {
      min-width: 200px;
    }
    .badge {
      margin-left: 6px;
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
    .spacer {
      flex: 1;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class PurchaseCategoriesPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly lines = expenseLineLabels;
  protected readonly categories = signal<PurchaseCategory[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  // The category whose delete is being asked about, and where what is in it goes.
  protected readonly confirming = signal<string | null>(null);
  protected moveTo: string | null = null;

  constructor() {
    void this.load();
  }

  protected others(category: PurchaseCategory): PurchaseCategory[] {
    return this.categories().filter((c) => c.id !== category.id);
  }

  // Raw materials and expense items filed under it.
  protected filed(category: PurchaseCategory): number {
    return category.materials + category.expenseItems;
  }

  protected async add(): Promise<void> {
    if (await this.open({})) {
      await this.load();
    }
  }

  protected async edit(category: PurchaseCategory): Promise<void> {
    if (await this.open({ category })) {
      await this.load();
    }
  }

  protected ask(category: PurchaseCategory): void {
    this.moveTo = null;
    this.confirming.set(category.id);
  }

  protected async remove(category: PurchaseCategory): Promise<void> {
    this.saving.set(true);
    try {
      const moving = this.filed(category) > 0;
      await firstValueFrom(this.api.deletePurchaseCategory(category.id, moving ? this.moveTo : null));
      const to = this.categories().find((c) => c.id === this.moveTo)?.name;
      this.notify.info(moving && to ? `${category.name} deleted; what was in it is in ${to} now.` : `${category.name} deleted.`);
      this.confirming.set(null);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The category could not be deleted.');
    } finally {
      this.saving.set(false);
    }
  }

  private async open(data: PurchaseCategoryDialogData): Promise<PurchaseCategory | undefined> {
    return firstValueFrom(
      this.dialog
        .open<PurchaseCategoryDialog, PurchaseCategoryDialogData, PurchaseCategory>(PurchaseCategoryDialog, { data, width: '460px' })
        .afterClosed(),
    );
  }

  private async load(): Promise<void> {
    try {
      this.categories.set(await firstValueFrom(this.api.purchaseCategories()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The categories could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
