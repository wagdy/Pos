import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, MaterialCost, PrepItem, ProductionOrder, RecipeCategory, SavePrepItem, UnitOfMeasure, isRequested } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { ApprovalChanges } from './approval-changes';
import { showProductionOrder } from './production-order-dialog';
import { PurchaseCategoryField } from './purchase-category-field';

interface Line {
  rawMaterialId: string | null;
  // The Edible Portion for one batch, in the ingredient's own unit.
  quantity: number | null;
  yieldPercent: number | null;
}

const baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };
const displayUnits: Record<UnitOfMeasure, [string, number]> = { Gram: ['kg', 1000], Millilitre: ['L', 1000], Piece: ['piece', 1] };

// A prep item's profile: what it is, its recipe for one batch with every ingredient's cost and
// share, what that comes to per kg by the recipe at today's prices, and against it what the
// batches on the shelf actually cost, how much is there, and the batches made. The recipe is
// written here; costs follow as it is typed.
//
// Saved at once by a manager. Anyone else's save goes to a manager: a new prep item is not made
// until approved, and a changed one stays as it was, locked, with what was asked shown here.
@Component({
  selector: 'app-prep-item-page',
  imports: [
    ApprovalChanges,
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MoneyPipe,
    PurchaseCategoryField,
    RouterLink,
  ],
  template: `
    <div class="toolbar no-print">
      <a mat-button routerLink="/costing/recipes/prep"><mat-icon>arrow_back</mat-icon> All prep items</a>
      <span class="spacer"></span>
      @if (saved(); as p) {
        @if (!pending()) {
          <button mat-button class="danger" (click)="deleteItem()" [disabled]="saving()"><mat-icon>delete</mat-icon> Delete</button>
        }
        <a mat-flat-button routerLink="/costing/recipes/production" [queryParams]="{ prepItemId: p.id }">
          <mat-icon>precision_manufacturing</mat-icon> Make a batch
        </a>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <h2 class="title">{{ saved()?.name ?? 'New prep item' }} <span class="inline-ar">صنف نصف مصنع</span></h2>
      @if (pending(); as r) {
        <div class="pending-banner">
          <mat-icon>lock_clock</mat-icon>
          <div>
            {{ r.requestedBy }} asked {{ r.action === 'Delete' ? 'to delete' : 'to change' }} this prep item on {{ r.requestedAtUtc | date: 'd MMM y, HH:mm' }}.
            It stays as it is, and cannot be changed, until a manager decides.
            <span class="inline-ar">بانتظار موافقة المدير.</span>
            @if (r.changes.length) {
              <app-approval-changes [changes]="r.changes" />
            }
          </div>
        </div>
      } @else if (!approvals.canApprove()) {
        <p class="note">
          A manager approves {{ saved() ? 'changes to prep items' : 'new prep items' }}: {{ saved() ? 'it stays as it is' : 'it is not added' }} until then.
          <span class="inline-ar">يُرسل للمدير للموافقة.</span>
        </p>
      }

      <div class="figures">
        <div class="box key">
          <span class="label">By its recipe, today<span class="ar">التكلفة المعيارية</span></span>
          <strong>{{ lineCount() ? (standardPerUnit() | money) + ' / ' + displayUnit : '—' }}</strong>
          <small>One batch of {{ batch ?? 0 | number: '1.0-3' }} {{ displayUnit }}: {{ batchCost() | money }}</small>
        </div>
        @if (saved(); as p) {
          <div class="box">
            <span class="label">In stock at<span class="ar">متوسط تكلفة المخزون</span></span>
            <strong>{{ p.averageCostPerDisplayUnit === null ? '—' : (p.averageCostPerDisplayUnit | money) + ' / ' + p.displayUnit }}</strong>
            <small class="muted">The moving average of the batches made</small>
          </div>
          <div class="box">
            <span class="label">On hand<span class="ar">الرصيد</span></span>
            <strong [class.warn]="p.quantityOnHand < 0">{{ p.quantityOnHand / p.displayUnitSize | number: '1.0-3' }} {{ p.displayUnit }}</strong>
            <small class="muted">Worth {{ p.stockValue === null ? '—' : (p.stockValue | money) }}</small>
          </div>
          <div class="box">
            <span class="label">Last batch<span class="ar">آخر دفعة</span></span>
            <strong>{{ p.lastBatchCostPerDisplayUnit === null ? '—' : (p.lastBatchCostPerDisplayUnit | money) + ' / ' + p.displayUnit }}</strong>
            <small class="muted">{{ p.lastProducedAtUtc ? (p.lastProducedAtUtc | date: 'd MMM y, HH:mm') : 'Not made yet' }}</small>
          </div>
        }
      </div>

      <section class="form">
        <mat-form-field class="wide">
          <mat-label>Name · الاسم</mat-label>
          <input matInput [(ngModel)]="name" maxlength="200" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Code · الكود</mat-label>
          <input matInput [(ngModel)]="code" maxlength="32" placeholder="PREP-01" />
        </mat-form-field>
        <app-purchase-category-field [(ngModel)]="categoryId" class="category-field" kind="RawMaterials" />
        <mat-form-field>
          <mat-label>Recipe category · قسم الوصفة</mat-label>
          <mat-select [(ngModel)]="recipeCategoryId" aria-label="Recipe category">
            <mat-option [value]="null">None · بدون قسم</mat-option>
            @for (c of recipeCategories(); track c.id) {
              <mat-option [value]="c.id">{{ c.name }}{{ c.nameAr ? ' · ' + c.nameAr : '' }}</mat-option>
            }
          </mat-select>
          <mat-hint>Sorted with the dishes' recipes</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Kept in · وحدة التخزين</mat-label>
          <mat-select [(ngModel)]="unit" (ngModelChange)="unitChanged($event)" [disabled]="!!saved()">
            <mat-option value="Gram">Grams, shown in kg</mat-option>
            <mat-option value="Millilitre">Millilitres, shown in L</mat-option>
            <mat-option value="Piece">Pieces</mat-option>
          </mat-select>
          @if (saved()) {
            <mat-hint>Fixed: its stock and batches are in it.</mat-hint>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label>One batch makes · الدفعة تنتج</mat-label>
          <input matInput type="number" min="0" step="any" [(ngModel)]="batch" (ngModelChange)="touch()" />
          <span matTextSuffix>&nbsp;{{ displayUnit }}</span>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Reorder below · حد إعادة الطلب</mat-label>
          <input matInput type="number" min="0" step="any" [(ngModel)]="reorder" />
          <span matTextSuffix>&nbsp;{{ displayUnit }}</span>
        </mat-form-field>
      </section>

      <h3>Recipe for one batch <span class="inline-ar">مكونات الدفعة</span></h3>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Ingredient<span class="ar">المكون</span></th>
              <th class="num">Quantity (EP)<span class="ar">الكمية الصافية</span></th>
              <th class="num">Yield %<span class="ar">نسبة الصافي</span></th>
              <th class="num">Takes from stock<span class="ar">الكمية الإجمالية</span></th>
              <th class="num">Average cost<span class="ar">متوسط التكلفة</span></th>
              <th class="num">Batch cost<span class="ar">تكلفة الدفعة</span></th>
              <th class="num">Share<span class="ar">النسبة</span></th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (line of lines(); track $index; let i = $index) {
              @let m = material(line);
              <tr>
                <td>
                  <mat-form-field subscriptSizing="dynamic" class="ingredient">
                    <mat-select [(ngModel)]="line.rawMaterialId" (ngModelChange)="picked(line)" [aria-label]="'Ingredient, line ' + (i + 1)" placeholder="Choose…">
                      @for (o of ingredientChoices(); track o.id) {
                        <mat-option [value]="o.id">{{ o.code ? o.code + ' · ' : '' }}{{ o.name }}{{ o.kind === 'Prep' ? ' · prep' : '' }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                </td>
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="small">
                    <input matInput type="number" min="0" step="any" [(ngModel)]="line.quantity" (ngModelChange)="touch()" [attr.aria-label]="'Quantity, line ' + (i + 1)" />
                    <span matTextSuffix>&nbsp;{{ m ? base(m.unit) : '' }}</span>
                  </mat-form-field>
                </td>
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="yield">
                    <input matInput type="number" min="1" max="100" step="any" [(ngModel)]="line.yieldPercent" (ngModelChange)="touch()" [attr.aria-label]="'Yield, line ' + (i + 1)" />
                    <span matTextSuffix>%</span>
                  </mat-form-field>
                </td>
                <td class="num">{{ asPurchased(line) === null ? '' : (asPurchased(line) | number: '1.0-3') + ' ' + base(m!.unit) }}</td>
                <td class="num muted">
                  @if (m) {
                    {{ m.costPerPurchaseUnit === null ? 'No cost yet' : (m.costPerPurchaseUnit | money) + ' / ' + m.purchaseUnit }}
                  }
                </td>
                <td class="num">{{ lineCost(line) === null ? '' : (lineCost(line) | money) }}</td>
                <td class="num">{{ share(line) === null ? '' : (share(line) | number: '1.0-1') + '%' }}</td>
                <td>
                  <button mat-icon-button (click)="remove(i)" aria-label="Remove ingredient"><mat-icon>delete</mat-icon></button>
                </td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td colspan="5"><button mat-button (click)="add()"><mat-icon>add</mat-icon> Add ingredient</button></td>
              <td class="num">{{ batchCost() | money }}</td>
              <td class="num">{{ lineCount() ? '100%' : '' }}</td>
              <td></td>
            </tr>
          </tfoot>
        </table>
      </div>
      @if (missing().length) {
        <p class="warn">No cost yet for {{ missing().join(', ') }}: the cost shown is short of it, and no batch can be made until it has one.</p>
      }
      @if (!pending()) {
        <div class="toolbar actions">
          <span class="spacer"></span>
          @if (approvals.canApprove()) {
            <button mat-flat-button (click)="save()" [disabled]="!ready() || saving()"><mat-icon>save</mat-icon> Save</button>
          } @else {
            <button mat-flat-button (click)="save()" [disabled]="!ready() || saving()">
              <mat-icon>send</mat-icon> Send for approval · إرسال للموافقة
            </button>
          }
        </div>
      }

      @if (saved(); as p) {
        <h3>Batches made <span class="inline-ar">سجل التصنيع</span></h3>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Made<span class="ar">التاريخ</span></th>
                <th class="num">Planned<span class="ar">المخطط</span></th>
                <th class="num">Came out<span class="ar">المنتج</span></th>
                <th class="num">Yield<span class="ar">نسبة الإنتاج</span></th>
                <th class="num">Batch cost<span class="ar">التكلفة</span></th>
                <th class="num">Per {{ p.displayUnit }}<span class="ar">تكلفة الوحدة</span></th>
                <th>By<span class="ar">بواسطة</span></th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (o of p.recent; track o.id) {
                <tr [class.voided]="o.status === 'Voided'">
                  <td class="nowrap">
                    {{ o.producedAtUtc | date: 'd MMM y, HH:mm' }}
                    @if (o.status === 'Voided') {
                      <div><span class="voided-badge">{{ o.replacedBy ? 'Corrected' : 'Voided' }}<span class="ar">{{ o.replacedBy ? 'مُصحح' : 'ملغى' }}</span></span></div>
                    }
                  </td>
                  <td class="num">{{ o.plannedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</td>
                  <td class="num">{{ o.producedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</td>
                  <td class="num">{{ o.yieldPercent | number: '1.0-1' }}%</td>
                  <td class="num">{{ o.totalCost | money }}</td>
                  <td class="num">{{ o.unitCostPerDisplayUnit | money }}</td>
                  <td>{{ o.producedBy || '—' }}</td>
                  <td class="num"><button mat-stroked-button (click)="show(o)">Show · عرض</button></td>
                </tr>
              } @empty {
                <tr><td colspan="8" class="muted">Not made yet. Make a batch to put it in stock.</td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .title {
      margin: 0 0 12px;
      font: var(--mat-sys-title-large);
    }
    h3 {
      margin: 20px 0 8px;
      font: var(--mat-sys-title-medium);
    }
    .figures {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
      gap: 8px;
      margin-bottom: 16px;
    }
    .box {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 10px 12px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      .label {
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      strong {
        font: var(--mat-sys-title-large);
      }
      &.key {
        background: var(--mat-sys-secondary-container);
        color: var(--mat-sys-on-secondary-container);
        border-color: transparent;
      }
    }
    .form {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 4px 12px;
    }
    .wide {
      grid-column: span 2;
    }
    .ingredient {
      min-width: 220px;
      width: 100%;
    }
    .small {
      width: 140px;
    }
    .yield {
      width: 100px;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .nowrap {
      white-space: nowrap;
    }
    .actions {
      margin-top: 12px;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    tr.voided td {
      color: var(--mat-sys-on-surface-variant);
      text-decoration: line-through;
      text-decoration-color: color-mix(in srgb, currentColor 40%, transparent);
    }
  `,
})
export class PrepItemPage {
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly approvals = inject(Approvals);

  // From the route: an id, or 'new'.
  readonly id = input.required<string>();

  protected readonly saved = signal<PrepItem | null>(null);
  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly recipeCategories = signal<RecipeCategory[]>([]);
  protected readonly lines = signal<Line[]>([this.blank()]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected name = '';
  protected code = '';
  protected categoryId: string | null = null;
  protected recipeCategoryId: string | null = null;
  protected unit: UnitOfMeasure = 'Gram';
  protected displayUnit = 'kg';
  protected displayUnitSize = 1000;
  // In the display unit: 10 (kg).
  protected batch: number | null = 10;
  protected reorder: number | null = 0;

  // The form is changed in place; this bumps the signal so the costs follow.
  private readonly version = signal(0);

  // A request is made under an id made here, so a retry is recorded once; a new one after each.
  private requestId = crypto.randomUUID();

  // What waits for a manager on this prep item: it is locked meanwhile.
  protected readonly pending = computed(() => {
    const id = this.saved()?.id;
    return id ? this.approvals.pendingFor(id) : undefined;
  });

  // What it can be made from: anything but itself.
  protected readonly ingredientChoices = computed(() => this.materials().filter((m) => m.id !== this.saved()?.id));

  protected readonly lineCount = computed(() => {
    this.version();
    return this.lines().filter((l) => l.rawMaterialId && (l.quantity ?? 0) > 0).length;
  });

  protected readonly batchCost = computed(() => {
    this.version();
    return this.lines().reduce((sum, l) => sum + (this.lineCost(l) ?? 0), 0);
  });

  protected readonly standardPerUnit = computed(() => {
    this.version();
    const batch = this.batch ?? 0;
    return batch > 0 ? this.batchCost() / batch : 0;
  });

  protected readonly missing = computed(() => {
    this.version();
    return this.lines()
      .map((l) => this.material(l))
      .filter((m): m is MaterialCost => !!m && m.costPerUnit === null)
      .map((m) => m.name);
  });

  // A name, a batch, and each line an ingredient with a quantity, none twice.
  protected readonly ready = computed(() => {
    this.version();
    const lines = this.lines().filter((l) => l.rawMaterialId || l.quantity);
    const ids = lines.map((l) => l.rawMaterialId);
    return (
      this.name.trim() !== '' &&
      (this.batch ?? 0) > 0 &&
      lines.every((l) => l.rawMaterialId && (l.quantity ?? 0) > 0 && (l.yieldPercent === null || (l.yieldPercent > 0 && l.yieldPercent <= 100))) &&
      new Set(ids).size === ids.length
    );
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected material(line: Line): MaterialCost | undefined {
    return this.materials().find((m) => m.id === line.rawMaterialId);
  }

  protected base(unit: UnitOfMeasure): string {
    return baseLabels[unit];
  }

  // What the line takes off stock for one batch: the quantity before trimming.
  protected asPurchased(line: Line): number | null {
    if (!this.material(line) || !line.quantity || line.quantity <= 0) {
      return null;
    }
    return (line.quantity * 100) / (line.yieldPercent || 100);
  }

  protected lineCost(line: Line): number | null {
    const material = this.material(line);
    const quantity = this.asPurchased(line);
    return material && quantity !== null && material.costPerUnit !== null ? quantity * material.costPerUnit : null;
  }

  protected share(line: Line): number | null {
    const cost = this.lineCost(line);
    const total = this.batchCost();
    return cost !== null && total > 0 ? (cost * 100) / total : null;
  }

  protected touch(): void {
    this.version.update((v) => v + 1);
  }

  // An ingredient chosen: its usual yield, until another is typed.
  protected picked(line: Line): void {
    line.yieldPercent = this.material(line)?.defaultYieldPercent ?? 100;
    this.touch();
  }

  protected unitChanged(unit: UnitOfMeasure): void {
    [this.displayUnit, this.displayUnitSize] = displayUnits[unit];
    this.touch();
  }

  protected add(): void {
    this.lines.update((lines) => [...lines, this.blank()]);
  }

  protected remove(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
    this.touch();
  }

  protected show(order: ProductionOrder): void {
    showProductionOrder(this.dialog, order);
  }

  protected async save(): Promise<void> {
    if (!this.ready()) {
      return;
    }
    const body: SavePrepItem = {
      name: this.name.trim(),
      unit: this.unit,
      batchQuantity: PrepItemPage.toBase(this.batch!, this.displayUnitSize),
      ingredients: this.lines()
        .filter((l) => l.rawMaterialId)
        .map((l) => ({ rawMaterialId: l.rawMaterialId!, quantity: l.quantity!, yieldPercent: l.yieldPercent })),
      code: this.code.trim() || null,
      categoryId: this.categoryId,
      recipeCategoryId: this.recipeCategoryId,
      displayUnit: this.displayUnit,
      displayUnitSize: this.displayUnitSize,
      reorderLevel: PrepItemPage.toBase(this.reorder ?? 0, this.displayUnitSize),
    };
    this.saving.set(true);
    try {
      const saved = await firstValueFrom(this.api.savePrepItem(this.saved()?.id ?? null, { ...body, requestId: this.requestId }));
      if (isRequested(saved)) {
        this.requestId = crypto.randomUUID();
        await this.approvals.requested(saved);
        if (!this.saved()) {
          await this.router.navigate(['/costing/recipes/prep']);
        }
      } else {
        this.notify.info(`${saved.name} saved.`);
        if (this.saved()) {
          this.fill(saved);
        } else {
          await this.router.navigate(['/costing/recipes/prep', saved.id], { replaceUrl: true });
        }
      }
    } catch (error) {
      this.notify.error(error, 'The prep item could not be saved.');
    } finally {
      this.saving.set(false);
    }
  }

  // Archived for a manager (it then leaves the lists); asked of one for anyone else.
  protected async deleteItem(): Promise<void> {
    const item = this.saved();
    if (item && (await this.approvals.delete('PrepItem', item.id, item.name)) === 'Applied') {
      await this.router.navigate(['/costing/recipes/prep']);
    }
  }

  private fill(item: PrepItem): void {
    this.saved.set(item);
    this.name = item.name;
    this.code = item.code ?? '';
    this.categoryId = item.categoryId;
    this.recipeCategoryId = item.recipeCategoryId ?? null;
    this.unit = item.unit;
    this.displayUnit = item.displayUnit;
    this.displayUnitSize = item.displayUnitSize;
    this.batch = item.batchQuantity / item.displayUnitSize;
    this.reorder = item.reorderLevel / item.displayUnitSize;
    this.lines.set(
      item.ingredients.length
        ? item.ingredients.map((i) => ({ rawMaterialId: i.rawMaterialId, quantity: i.quantity, yieldPercent: i.yieldPercent }))
        : [this.blank()],
    );
    this.touch();
  }

  private async load(id: string): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    void this.approvals.refresh();
    try {
      const [materials, item, recipeCategories] = await Promise.all([
        firstValueFrom(this.api.materials()),
        id === 'new' ? Promise.resolve(null) : firstValueFrom(this.api.prepItem(id)),
        firstValueFrom(this.api.recipeCategories()),
      ]);
      this.materials.set(materials);
      this.recipeCategories.set(recipeCategories);
      if (id !== 'new' && !item) {
        this.error.set('No such prep item.');
      } else if (item) {
        this.fill(item);
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The prep item could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private blank(): Line {
    return { rawMaterialId: null, quantity: null, yieldPercent: null };
  }

  // To the thousandth, as stock is kept: 0.25 kg is 250 g.
  private static toBase(value: number, size: number): number {
    return Math.round(value * size * 1000) / 1000;
  }
}
