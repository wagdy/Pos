import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import {
  CategoryDeletionPreview,
  CategoryKind,
  CostingApi,
  DeleteCategoryInput,
  ExpenseItem,
  PurchaseCategory,
  UnitOfMeasure,
  expenseLineLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';

export interface CategoryDeleteDialogData {
  category: PurchaseCategory;
  categories: PurchaseCategory[];
}

// An expense item made a raw material, as it is being filled in.
interface StockRow {
  item: ExpenseItem;
  unit: UnitOfMeasure | null;
  purchaseUnit: string;
  purchaseUnitSize: number | null;
}

// A category deleted (حذف قسم), what is in it moved first: its raw materials to one category and
// its expense items to another, each listed by kind. To a category of its own kind, a kind moves as
// it is; to one of the other, it is converted on the way: raw materials made expense items (bought
// as stock by mistake), expense items made raw materials (to be counted from now on). Moved as they
// are, it is deleted at once. Converted, the change is reviewed as the server sees it, then done at
// once for a manager or sent to one for approval. The dialog closes with true once it has gone, or
// been asked for.
@Component({
  selector: 'app-category-delete-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Delete {{ data.category.name }} <span class="inline-ar">حذف القسم</span></h2>
    <mat-dialog-content>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      } @else if (preview(); as p) {
        <p class="summary">{{ p.summary }}.</p>
        <div class="table-wrap">
          <table class="grid changes">
            <tbody>
              @for (c of p.changes; track $index) {
                <tr>
                  <th scope="row">{{ c.label }}</th>
                  <td>{{ c.before ?? '' }}</td>
                  <td>{{ c.after ?? '' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (!approvals.canApprove()) {
          <p class="ask">A manager approves this: nothing changes until then, and {{ data.category.name }} is locked.</p>
          <mat-form-field class="wide" subscriptSizing="dynamic">
            <mat-label>Reason · السبب</mat-label>
            <textarea matInput [(ngModel)]="reason" maxlength="500" rows="2" placeholder="Optional: tells the manager why"></textarea>
          </mat-form-field>
        }
      } @else if (!filed()) {
        <p>Nothing is in {{ data.category.name }}. It is deleted straight away.</p>
      } @else {
        <p>They move, with their purchases, to other categories: each kind to one of its own as it is, or to one of the other kind, converted on the way.</p>
        @if (data.category.materials) {
          <div class="step">
            <mat-form-field subscriptSizing="dynamic" class="target">
              <mat-label>Its {{ count(data.category.materials, 'raw material') }} go to · تُنقل الخامات إلى</mat-label>
              <mat-select [(ngModel)]="materialsTo" (ngModelChange)="error.set(null)" aria-label="Where its raw materials go">
                <mat-optgroup label="Raw materials · خامات (as they are)">
                  @for (c of targets('RawMaterials'); track c.id) {
                    <mat-option [value]="c.id">{{ c.name }}</mat-option>
                  }
                </mat-optgroup>
                <mat-optgroup label="General expenses · مصروفات (made expense items)">
                  @for (c of targets('Expenses'); track c.id) {
                    <mat-option [value]="c.id">{{ c.name }}</mat-option>
                  }
                </mat-optgroup>
              </mat-select>
              @if (kindOf(materialsTo) === 'Expenses') {
                <mat-hint>Their expenses count as {{ lines[target(materialsTo)!.expenseLine].en }}</mat-hint>
              }
            </mat-form-field>
            <p class="hint" [class.warn]="materialsBecomeExpenses()">{{ materialsEffect() }}</p>
          </div>
        }
        @if (data.category.expenseItems) {
          <div class="step">
            <mat-form-field subscriptSizing="dynamic" class="target">
              <mat-label>Its {{ count(data.category.expenseItems, 'expense item') }} go to · تُنقل بنود المصروفات إلى</mat-label>
              <mat-select [(ngModel)]="expenseItemsTo" (ngModelChange)="error.set(null)" aria-label="Where its expense items go">
                <mat-optgroup label="General expenses · مصروفات (as they are)">
                  @for (c of targets('Expenses'); track c.id) {
                    <mat-option [value]="c.id">{{ c.name }}</mat-option>
                  }
                </mat-optgroup>
                <mat-optgroup label="Raw materials · خامات (made raw materials)">
                  @for (c of targets('RawMaterials'); track c.id) {
                    <mat-option [value]="c.id">{{ c.name }}</mat-option>
                  }
                </mat-optgroup>
              </mat-select>
            </mat-form-field>
            <p class="hint" [class.warn]="expenseItemsBecomeStock()">{{ expenseItemsEffect() }}</p>
            @if (expenseItemsBecomeStock()) {
              <div class="table-wrap">
                <table class="grid units">
                  <thead>
                    <tr>
                      <th>Expense item<span class="ar">بند المصروف</span></th>
                      <th>Counted in<span class="ar">وحدة العد</span></th>
                      <th>Bought by<span class="ar">وحدة الشراء</span></th>
                      <th class="num">How much one holds<span class="ar">تحتوي</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (r of rows(); track r.item.id) {
                      <tr>
                        <td>{{ r.item.name }}</td>
                        <td>
                          <mat-form-field subscriptSizing="dynamic" class="unit">
                            <mat-select [(ngModel)]="r.unit" [aria-label]="r.item.name + ', counted in'" placeholder="Choose">
                              <mat-option value="Gram">Grams (g)</mat-option>
                              <mat-option value="Millilitre">Millilitres (ml)</mat-option>
                              <mat-option value="Piece">Pieces (pc)</mat-option>
                            </mat-select>
                          </mat-form-field>
                        </td>
                        <td>
                          <mat-form-field subscriptSizing="dynamic" class="unit">
                            <input matInput [(ngModel)]="r.purchaseUnit" maxlength="32" [attr.aria-label]="r.item.name + ', bought by'" />
                          </mat-form-field>
                        </td>
                        <td class="num">
                          <mat-form-field subscriptSizing="dynamic" class="size">
                            <input matInput type="number" min="0" step="any" [(ngModel)]="r.purchaseUnitSize" [attr.aria-label]="r.item.name + ', how much one holds'" />
                            @if (r.unit) {
                              <span matTextSuffix>&nbsp;{{ unitNames[r.unit] }}</span>
                            }
                          </mat-form-field>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </div>
        }
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (preview()) {
        <button mat-button (click)="preview.set(null)" [disabled]="saving()">Back</button>
        <button mat-flat-button class="danger-fill" (click)="send()" [disabled]="saving()">
          {{ approvals.canApprove() ? 'Delete and convert' : 'Send for approval' }}
        </button>
      } @else {
        <button mat-button mat-dialog-close [disabled]="saving()">Cancel</button>
        @if (converts()) {
          <button mat-flat-button (click)="review()" [disabled]="saving() || loading()">Review the changes</button>
        } @else {
          <button mat-flat-button class="danger-fill" (click)="send()" [disabled]="saving() || loading()">
            {{ filed() ? 'Move and delete' : 'Delete' }}
          </button>
        }
      }
    </mat-dialog-actions>
  `,
  styleUrl: './costing.scss',
  styles: `
    .step {
      margin: 12px 0;
    }
    .hint {
      margin: 4px 0 0;
      color: var(--mat-sys-on-surface-variant);
    }
    .hint.warn {
      color: var(--mat-sys-on-surface);
      font-weight: 500;
    }
    .target {
      min-width: 300px;
    }
    .unit {
      width: 150px;
    }
    .size {
      width: 130px;
    }
    .wide {
      width: 100%;
    }
    .summary {
      font-weight: 500;
    }
    .ask {
      margin: 8px 0;
      font-weight: 500;
    }
    .changes th {
      text-align: start;
    }
    .danger-fill:not(:disabled) {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
  `,
})
export class CategoryDeleteDialog {
  protected readonly data = inject<CategoryDeleteDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CategoryDeleteDialog, boolean>);
  private readonly api = inject(CostingApi);
  private readonly notify = inject(NotifyService);
  protected readonly approvals = inject(Approvals);
  protected readonly lines = expenseLineLabels;
  protected readonly unitNames: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly preview = signal<CategoryDeletionPreview | null>(null);
  // Its expense items still in use: the ones that would become raw materials.
  protected readonly rows = signal<StockRow[]>([]);

  // Where each kind goes: chosen, never assumed.
  private readonly materialsTarget = signal<string | null>(null);
  protected get materialsTo(): string | null {
    return this.materialsTarget();
  }
  protected set materialsTo(value: string | null) {
    this.materialsTarget.set(value);
  }
  private readonly expensesTarget = signal<string | null>(null);
  protected get expenseItemsTo(): string | null {
    return this.expensesTarget();
  }
  protected set expenseItemsTo(value: string | null) {
    this.expensesTarget.set(value);
  }
  protected reason = '';
  private readonly requestId = crypto.randomUUID();

  protected readonly filed = computed(() => this.data.category.materials + this.data.category.expenseItems);
  protected readonly materialsBecomeExpenses = computed(
    () => this.data.category.materials > 0 && this.kindOf(this.materialsTarget()) === 'Expenses',
  );
  protected readonly expenseItemsBecomeStock = computed(
    () => this.rows().length > 0 && this.kindOf(this.expensesTarget()) === 'RawMaterials',
  );
  protected readonly converts = computed(() => this.materialsBecomeExpenses() || this.expenseItemsBecomeStock());

  constructor() {
    void this.load();
  }

  // The other categories of that kind, its General first.
  protected targets(kind: CategoryKind): PurchaseCategory[] {
    return this.data.categories.filter((c) => c.id !== this.data.category.id && c.kind === kind);
  }

  protected target(id: string | null): PurchaseCategory | null {
    return this.data.categories.find((c) => c.id === id) ?? null;
  }

  protected kindOf(id: string | null): CategoryKind | null {
    return this.target(id)?.kind ?? null;
  }

  protected count(n: number, what: string): string {
    return `${n} ${n === 1 ? what : `${what}s`}`;
  }

  protected materialsEffect(): string {
    const n = this.data.category.materials;
    if (!this.materialsTarget()) {
      return 'Choose where they go.';
    }
    if (!this.materialsBecomeExpenses()) {
      return 'They move as they are.';
    }
    return n === 1
      ? 'It becomes an expense item: its past invoice lines are restated as expenses on their own dates, and its stock ledger is taken out.'
      : 'They become expense items: their past invoice lines are restated as expenses on their own dates, and their stock ledger is taken out.';
  }

  protected expenseItemsEffect(): string {
    if (!this.expensesTarget()) {
      return 'Choose where they go.';
    }
    if (!this.expenseItemsBecomeStock()) {
      return 'They move as they are.';
    }
    return this.rows().length === 1
      ? 'It becomes a raw material, starting with nothing on hand; its past invoices stay expenses. Say how it is counted:'
      : 'They become raw materials, starting with nothing on hand; their past invoices stay expenses. Say how each is counted:';
  }

  protected async review(): Promise<void> {
    const deletion = this.deletion();
    if (!deletion) {
      return;
    }
    this.saving.set(true);
    try {
      this.preview.set(await firstValueFrom(this.api.previewCategoryDeletion(this.data.category.id, deletion)));
    } catch (error) {
      this.error.set(errorMessage(error, 'What deleting it would do could not be checked.'));
    } finally {
      this.saving.set(false);
    }
  }

  // Moved as they are (or nothing in it), deleted at once; converted, done or asked of a manager.
  protected async send(): Promise<void> {
    const deletion = this.deletion();
    if (!deletion) {
      return;
    }
    this.saving.set(true);
    try {
      const result = await firstValueFrom(this.api.deletePurchaseCategory(this.data.category.id, deletion));
      if (result.outcome === 'Requested' && result.request) {
        await this.approvals.requested({ outcome: result.outcome, request: result.request });
      } else {
        this.notify.info(`${this.preview()?.summary ?? `${this.data.category.name} deleted`}.`);
      }
      this.ref.close(true);
    } catch (error) {
      this.preview.set(null);
      this.error.set(errorMessage(error, `${this.data.category.name} could not be deleted.`));
    } finally {
      this.saving.set(false);
    }
  }

  // What is sent, or null with what is missing said.
  private deletion(): DeleteCategoryInput | null {
    if (this.data.category.materials > 0 && !this.materialsTo) {
      this.error.set('Choose where its raw materials go.');
      return null;
    }
    if (this.data.category.expenseItems > 0 && !this.expenseItemsTo) {
      this.error.set('Choose where its expense items go.');
      return null;
    }
    if (this.expenseItemsBecomeStock()) {
      const missing = this.rows().find((r) => !r.unit || !r.purchaseUnit.trim() || !((r.purchaseUnitSize ?? 0) > 0));
      if (missing) {
        this.error.set(`Say what ${missing.item.name} is counted in, what it is bought by, and how much one holds.`);
        return null;
      }
    }
    this.error.set(null);
    return {
      requestId: this.requestId,
      materialsTo: this.data.category.materials > 0 ? this.materialsTo : null,
      expenseItemsTo: this.data.category.expenseItems > 0 ? this.expenseItemsTo : null,
      units: this.expenseItemsBecomeStock()
        ? this.rows().map((r) => ({ expenseItemId: r.item.id, unit: r.unit!, purchaseUnit: r.purchaseUnit.trim(), purchaseUnitSize: r.purchaseUnitSize! }))
        : [],
      reason: this.reason.trim() || null,
    };
  }

  private async load(): Promise<void> {
    try {
      if (this.data.category.expenseItems > 0) {
        const items = await firstValueFrom(this.api.expenseItems(true));
        this.rows.set(
          items
            .filter((i) => i.categoryId === this.data.category.id && !i.isArchived)
            .map((item) => ({ item, unit: null, purchaseUnit: item.unit ?? 'piece', purchaseUnitSize: 1 })),
        );
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'Its expense items could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
