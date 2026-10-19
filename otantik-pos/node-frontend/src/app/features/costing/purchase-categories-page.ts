import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { ApprovalRequest, CostingApi, PurchaseCategory, categoryKindLabels, expenseLineLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from './approvals';
import { CategoryDeleteDialog, CategoryDeleteDialogData } from './category-delete-dialog';
import { PurchaseCategoryDialog, PurchaseCategoryDialogData } from './purchase-category-dialog';

// The purchase categories (أقسام المشتريات والخامات): what raw materials and expense items are
// filed under, so purchases add up by category, and where each category's expenses count in the
// profit and loss. One holding anything is deleted only after choosing where it moves, so nothing
// is left without one, and on the way its raw materials can be made expense items or its expense
// items raw materials (CategoryDeleteDialog); General, where anything not yet sorted goes, is never
// deleted. One whose deletion waits for a manager is marked, and locked.
@Component({
  selector: 'app-purchase-categories-page',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <p class="note">
      How purchases are grouped: Meat, Dairy, Packaging for raw materials; Utilities, Cleaning, Rent for expense items. Each category
      is for one kind: a raw material goes in a raw materials category, an expense item in a general expenses one, and spending is
      added up by them. For expenses, the category also says where they count in the profit and loss.
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
              <th>For<span class="ar">النوع</span></th>
              <th>Expenses count as<span class="ar">تُحتسب المصروفات ضمن</span></th>
              <th class="num">Raw materials<span class="ar">الخامات</span></th>
              <th class="num">Expense items<span class="ar">بنود المصروفات</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of categories(); track c.id) {
              <tr [class.pending-deletion]="!!pendingOf(c)">
                <td>
                  {{ c.name }}
                  @if (c.isGeneral) {
                    <span class="badge">Default · افتراضي</span>
                  }
                  @if (c.misplaced) {
                    <div class="misplaced">
                      {{ c.misplaced }} {{ c.kind === 'RawMaterials' ? (c.misplaced === 1 ? 'expense item does' : 'expense items do') : (c.misplaced === 1 ? 'raw material does' : 'raw materials do') }}
                      not belong here: move or convert {{ c.misplaced === 1 ? 'it' : 'them' }}.
                    </div>
                  }
                  @if (pendingOf(c); as p) {
                    <div><span class="pending-badge">{{ pendingLabel(p).en }}<span class="ar">{{ pendingLabel(p).ar }}</span></span>
                      <span class="muted small"> by {{ p.requestedBy }}</span></div>
                  }
                </td>
                <td dir="rtl" class="ar-cell">{{ c.nameAr ?? '—' }}</td>
                <td>{{ kinds[c.kind].en }}<span class="ar">{{ kinds[c.kind].ar }}</span></td>
                <td>
                  @if (c.kind === 'Expenses') {
                    {{ lines[c.expenseLine].en }}<span class="ar">{{ lines[c.expenseLine].ar }}</span>
                  } @else {
                    <span class="muted">—</span>
                  }
                </td>
                <td class="num">{{ c.materials }}</td>
                <td class="num">{{ c.expenseItems }}</td>
                <td class="num actions">
                  @if (pendingOf(c)) {
                    <mat-icon class="locked" matTooltip="Waiting for a manager's approval: locked until decided" aria-label="Locked: waiting for a manager">lock_clock</mat-icon>
                  } @else {
                    <button mat-icon-button (click)="edit(c)" [attr.aria-label]="'Edit ' + c.name"><mat-icon>edit</mat-icon></button>
                    @if (!c.isGeneral) {
                      <button mat-icon-button class="danger" (click)="remove(c)" [attr.aria-label]="'Delete ' + c.name">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="muted">No categories yet.</td></tr>
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
    .misplaced {
      margin-top: 2px;
      font-size: 0.85em;
      color: var(--mat-sys-error);
    }
    .actions {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
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
  private readonly approvals = inject(Approvals);

  protected readonly lines = expenseLineLabels;
  protected readonly kinds = categoryKindLabels;
  protected readonly pendingLabel = pendingLabel;
  protected readonly categories = signal<PurchaseCategory[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected pendingOf(category: PurchaseCategory): ApprovalRequest | undefined {
    return this.approvals.pendingFor(category.id);
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

  // Where what is in it goes, and what it becomes on the way: asked in the delete dialog, which
  // deletes it (or sends it to a manager) before it closes.
  protected async remove(category: PurchaseCategory): Promise<void> {
    const done = await firstValueFrom(
      this.dialog
        .open<CategoryDeleteDialog, CategoryDeleteDialogData, boolean>(CategoryDeleteDialog, {
          data: { category, categories: this.categories() },
          width: '720px',
          maxWidth: '95vw',
        })
        .afterClosed(),
    );
    if (done) {
      await this.load();
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
    void this.approvals.refresh();
    try {
      this.categories.set(await firstValueFrom(this.api.purchaseCategories()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The categories could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
