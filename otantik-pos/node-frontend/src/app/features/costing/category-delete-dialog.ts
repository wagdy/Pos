import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import {
  CategoryConversion,
  CategoryDeletionPreview,
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

// What a category's contents end up as: raw materials (purchases and stock), expense items
// (general expenses), or each as it is.
type Domain = 'stock' | 'expenses' | 'keep';

// An expense item made a raw material, as it is being filled in.
interface StockRow {
  item: ExpenseItem;
  unit: UnitOfMeasure | null;
  purchaseUnit: string;
  purchaseUnitSize: number | null;
}

// A category deleted (حذف قسم), what is in it moved first: as it is, or across, its raw materials
// made expense items (one bought as stock by mistake) or its expense items made raw materials.
// The domain chosen first, then a category that holds that kind (or nothing yet). Moved as they
// are, it is deleted at once. Converted, the change is reviewed as the server sees it, then done
// at once for a manager or sent to one for approval. The dialog closes with true once it has
// gone, or been asked for.
@Component({
  selector: 'app-category-delete-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatRadioModule, MatSelectModule],
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
        <p>
          {{ data.category.name }} holds {{ holds() }}. They move, with their purchases, to another category.
        </p>
        <div class="step">
          <span class="step-label" id="domain-label">What they become · تصبح</span>
          <mat-radio-group [(ngModel)]="domain" (ngModelChange)="domainChanged()" aria-labelledby="domain-label" class="domains">
            <mat-radio-button value="stock">Purchases &amp; raw materials · مشتريات وخامات</mat-radio-button>
            <mat-radio-button value="expenses">General expenses · مصروفات عمومية</mat-radio-button>
            @if (mixed()) {
              <mat-radio-button value="keep">Each as it is · كما هي</mat-radio-button>
            }
          </mat-radio-group>
          <p class="hint" [class.warn]="convert() !== 'None'">{{ effect() }}</p>
        </div>
        <div class="step">
          <mat-form-field subscriptSizing="dynamic" class="target">
            <mat-label>Move to · تُنقل إلى</mat-label>
            <mat-select [(ngModel)]="moveTo" (ngModelChange)="error.set(null)" aria-label="Move what is in it to">
              @for (c of targets(); track c.id) {
                <mat-option [value]="c.id">{{ c.name }}<span class="muted"> · {{ counts(c) }}</span></mat-option>
              } @empty {
                <mat-option disabled>No category for this yet: add one first</mat-option>
              }
            </mat-select>
            @if (convert() === 'ToExpenses' && target(); as t) {
              <mat-hint>Their expenses count as {{ lines[t.expenseLine].en }}</mat-hint>
            }
          </mat-form-field>
        </div>
        @if (convert() === 'ToStock') {
          <div class="step">
            <span class="step-label">How each is counted as stock · وحدة العد</span>
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
        @if (convert() !== 'None') {
          <button mat-flat-button (click)="review()" [disabled]="saving() || loading()">Review the changes</button>
        } @else {
          <button mat-flat-button class="danger-fill" (click)="removePlain()" [disabled]="saving() || loading()">
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
    .step-label {
      display: block;
      font: var(--mat-sys-title-small);
      margin-bottom: 4px;
    }
    .domains {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .hint {
      margin: 6px 0 0;
      color: var(--mat-sys-on-surface-variant);
    }
    .hint.warn {
      color: var(--mat-sys-on-surface);
      font-weight: 500;
    }
    .target {
      min-width: 280px;
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

  // Starts as what it holds: a category of raw materials stays one, of expenses stays one.
  private readonly domainChoice = signal<Domain>(
    this.data.category.materials && this.data.category.expenseItems ? 'keep' : this.data.category.expenseItems ? 'expenses' : 'stock',
  );
  protected get domain(): Domain {
    return this.domainChoice();
  }
  protected set domain(value: Domain) {
    this.domainChoice.set(value);
  }
  private readonly target$ = signal<string | null>(null);
  protected get moveTo(): string | null {
    return this.target$();
  }
  protected set moveTo(value: string | null) {
    this.target$.set(value);
  }
  protected reason = '';
  private readonly requestId = crypto.randomUUID();

  protected readonly filed = computed(() => this.data.category.materials + this.data.category.expenseItems);
  protected readonly mixed = computed(() => this.data.category.materials > 0 && this.data.category.expenseItems > 0);

  protected readonly convert = computed((): CategoryConversion => {
    const domain = this.domainChoice();
    if (domain === 'expenses' && this.data.category.materials > 0) {
      return 'ToExpenses';
    }
    return domain === 'stock' && this.rows().length > 0 ? 'ToStock' : 'None';
  });

  // The categories it can move to, by what they hold: for expenses one holding expense items or
  // nothing yet, for raw materials one holding raw materials or nothing yet. General takes either.
  protected readonly targets = computed(() => {
    const domain = this.domainChoice();
    return this.data.categories.filter(
      (c) =>
        c.id !== this.data.category.id &&
        (domain === 'keep' ||
          c.isGeneral ||
          (domain === 'expenses' ? c.expenseItems > 0 || c.materials === 0 : c.materials > 0 || c.expenseItems === 0)),
    );
  });
  protected readonly target = computed(() => this.data.categories.find((c) => c.id === this.target$()) ?? null);

  constructor() {
    void this.load();
  }

  protected holds(): string {
    const { materials, expenseItems } = this.data.category;
    return [count(materials, 'raw material'), count(expenseItems, 'expense item')].filter(Boolean).join(' and ');
  }

  protected counts(category: PurchaseCategory): string {
    return [count(category.materials, 'raw material'), count(category.expenseItems, 'expense item')].filter(Boolean).join(', ') || 'empty';
  }

  // What choosing the domain does to what is in it.
  protected effect(): string {
    const { materials, expenseItems } = this.data.category;
    switch (this.convert()) {
      case 'ToExpenses':
        return materials === 1
          ? `Its raw material becomes an expense item: its past invoice lines are restated as expenses on their own dates, and its stock ledger is taken out.${expenseItems ? ' Its expense items move as they are.' : ''}`
          : `Its ${materials} raw materials become expense items: their past invoice lines are restated as expenses on their own dates, and their stock ledger is taken out.${expenseItems ? ' Its expense items move as they are.' : ''}`;
      case 'ToStock':
        return this.rows().length === 1
          ? `Its expense item becomes a raw material, starting with nothing on hand; its past invoices stay expenses.${materials ? ' Its raw materials move as they are.' : ''}`
          : `Its ${this.rows().length} expense items become raw materials, starting with nothing on hand; their past invoices stay expenses.${materials ? ' Its raw materials move as they are.' : ''}`;
      default:
        return 'They move as they are.';
    }
  }

  // A new domain: a category chosen for the other no longer fits.
  protected domainChanged(): void {
    this.error.set(null);
    if (this.moveTo && !this.targets().some((c) => c.id === this.moveTo)) {
      this.moveTo = null;
    }
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

  protected async send(): Promise<void> {
    const deletion = this.deletion();
    if (!deletion) {
      return;
    }
    this.saving.set(true);
    try {
      const result = await firstValueFrom(this.api.deletePurchaseCategoryConverting(this.data.category.id, deletion));
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

  // Moved as they are (or nothing in it): deleted at once, as it always was.
  protected async removePlain(): Promise<void> {
    if (this.filed() && !this.moveTo) {
      this.error.set('Choose the category they move to.');
      return;
    }
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.deletePurchaseCategory(this.data.category.id, this.filed() ? this.moveTo : null));
      this.notify.info(`${this.data.category.name} deleted.`);
      this.ref.close(true);
    } catch (error) {
      this.error.set(errorMessage(error, `${this.data.category.name} could not be deleted.`));
    } finally {
      this.saving.set(false);
    }
  }

  // What is sent, or null with what is missing said.
  private deletion(): DeleteCategoryInput | null {
    if (!this.moveTo) {
      this.error.set('Choose the category they move to.');
      return null;
    }
    const convert = this.convert();
    if (convert === 'ToStock') {
      const missing = this.rows().find((r) => !r.unit || !r.purchaseUnit.trim() || !((r.purchaseUnitSize ?? 0) > 0));
      if (missing) {
        this.error.set(`Say what ${missing.item.name} is counted in, what it is bought by, and how much one holds.`);
        return null;
      }
    }
    this.error.set(null);
    return {
      requestId: this.requestId,
      moveToId: this.moveTo,
      convert,
      units:
        convert === 'ToStock'
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

function count(n: number, what: string): string {
  return n === 0 ? '' : `${n} ${n === 1 ? what : `${what}s`}`;
}
