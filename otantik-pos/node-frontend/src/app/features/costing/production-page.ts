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
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PrepItem, ProductionOrder, ProductionPreview, UnitOfMeasure } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { showProductionOrder } from './production-order-dialog';

const baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

// The kitchen's prep work, logged. Choose the prep item, how much was planned (which sets the
// ingredients, by its recipe) and how much came out; review what it takes against what the
// records show and what it costs at today's averages; then Confirm & post, which takes the
// ingredients off stock and puts the prep item in at their cost over what came out.
@Component({
  selector: 'app-production-page',
  imports: [
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
    RouterLink,
  ],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <section class="entry">
        <h2>Make a batch <span class="inline-ar">إذن تصنيع جديد</span></h2>
        @if (items().length === 0) {
          <p class="note">No prep items with a recipe yet. <a routerLink="../prep/new">Add one</a>, such as pizza dough, first.</p>
        } @else {
          <div class="form">
            <mat-form-field class="wide">
              <mat-label>Prep item · الصنف</mat-label>
              <mat-select [(ngModel)]="prepItemId" (ngModelChange)="changed()" aria-label="Prep item">
                @for (p of items(); track p.id) {
                  <mat-option [value]="p.id">{{ p.code ? p.code + ' · ' : '' }}{{ p.name }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <mat-form-field>
              <mat-label>Planned · الكمية المخططة</mat-label>
              <input matInput type="number" min="0" step="any" [(ngModel)]="planned" (ngModelChange)="plannedChanged()" aria-label="Planned" />
              <span matTextSuffix>&nbsp;{{ item()?.displayUnit }}</span>
              @if (item(); as p) {
                <mat-hint>A batch is {{ p.batchQuantity / p.displayUnitSize | number: '1.0-3' }} {{ p.displayUnit }}</mat-hint>
              }
            </mat-form-field>
            <mat-form-field>
              <mat-label>Came out · الكمية المنتجة</mat-label>
              <input matInput type="number" min="0" step="any" [(ngModel)]="produced" (ngModelChange)="changed()" aria-label="Came out" />
              <span matTextSuffix>&nbsp;{{ item()?.displayUnit }}</span>
              @if (yieldPercent(); as y) {
                <mat-hint [class.warn]="y < 100">{{ y | number: '1.0-1' }}% of planned</mat-hint>
              }
            </mat-form-field>
            <mat-form-field class="wide">
              <mat-label>Notes · ملاحظات</mat-label>
              <input matInput [(ngModel)]="notes" maxlength="200" placeholder="Morning batch" />
            </mat-form-field>
          </div>

          @if (preview(); as r) {
            <!-- The review: nothing has moved yet. -->
            <div class="table-wrap">
              <table class="grid">
                <thead>
                  <tr>
                    <th>Ingredient<span class="ar">المكون</span></th>
                    <th class="num">Takes<span class="ar">المطلوب</span></th>
                    <th class="num">On record<span class="ar">الرصيد الدفتري</span></th>
                    <th class="num">Average cost<span class="ar">متوسط التكلفة</span></th>
                    <th class="num">Cost<span class="ar">التكلفة</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of r.lines; track l.rawMaterialId) {
                    <tr [class.short]="l.short !== null">
                      <td>
                        {{ l.material }}
                        @if (l.isPrepItem) {
                          <span class="muted"> · prep</span>
                        }
                      </td>
                      <td class="num">{{ l.quantity | number: '1.0-3' }} {{ base(l.unit) }}</td>
                      <td class="num">
                        {{ l.onHand | number: '1.0-3' }} {{ base(l.unit) }}
                        @if (l.short !== null) {
                          <div class="warn small">{{ l.short | number: '1.0-3' }} {{ base(l.unit) }} short</div>
                        }
                      </td>
                      <td class="num muted">{{ l.costPerPurchaseUnit === null ? 'No cost yet' : (l.costPerPurchaseUnit | money) + ' / ' + l.purchaseUnit }}</td>
                      <td class="num">{{ l.cost === null ? '—' : (l.cost | money) }}</td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <td colspan="4">Batch cost · تكلفة الدفعة</td>
                    <td class="num">{{ r.totalCost === null ? '—' : (r.totalCost | money) }}</td>
                  </tr>
                  <tr>
                    <td colspan="4">
                      Per {{ r.displayUnit }} of {{ r.prepItem }}, over {{ r.producedQuantity / r.displayUnitSize | number: '1.0-3' }} {{ r.displayUnit }} made
                      <span class="inline-ar">تكلفة الوحدة</span>
                    </td>
                    <td class="num strong">{{ r.unitCostPerDisplayUnit === null ? '—' : (r.unitCostPerDisplayUnit | money) }}</td>
                  </tr>
                </tfoot>
              </table>
            </div>
            @if (r.missingPrices.length) {
              <p class="warn">{{ r.missingPrices.join(', ') }} {{ r.missingPrices.length === 1 ? 'has' : 'have' }} no cost yet, so the batch cannot be made: enter it on Raw materials, or post the invoice it came on.</p>
            } @else if (r.shortages.length) {
              <p class="caution">
                The records show too little {{ r.shortages.join(', ') }}. If it is on the shelf, make the batch: the records go below zero until the
                delivery is entered or a count puts them right.
              </p>
            }
          }

          <div class="toolbar actions">
            <span class="spacer"></span>
            @if (preview()) {
              <button mat-flat-button (click)="confirm()" [disabled]="saving() || !!preview()!.missingPrices.length">
                <mat-icon>inventory</mat-icon> Confirm &amp; post
              </button>
            } @else {
              <button mat-flat-button (click)="review()" [disabled]="!ready() || saving()"><mat-icon>fact_check</mat-icon> Review</button>
            }
          </div>
        }
      </section>

      <h3>Batches made <span class="inline-ar">سجل أذونات التصنيع</span></h3>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Made<span class="ar">التاريخ</span></th>
              <th>Prep item<span class="ar">الصنف</span></th>
              <th class="num">Planned<span class="ar">المخطط</span></th>
              <th class="num">Came out<span class="ar">المنتج</span></th>
              <th class="num">Yield<span class="ar">نسبة الإنتاج</span></th>
              <th class="num">Batch cost<span class="ar">التكلفة</span></th>
              <th class="num">Unit cost<span class="ar">تكلفة الوحدة</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (o of orders(); track o.id) {
              <tr>
                <td class="nowrap">{{ o.producedAtUtc | date: 'd MMM, HH:mm' }}</td>
                <td>{{ o.prepItem }}</td>
                <td class="num">{{ o.plannedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</td>
                <td class="num">{{ o.producedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</td>
                <td class="num">{{ o.yieldPercent | number: '1.0-1' }}%</td>
                <td class="num">{{ o.totalCost | money }}</td>
                <td class="num">{{ o.unitCostPerDisplayUnit | money }} / {{ o.displayUnit }}</td>
                <td>{{ o.producedBy || '—' }}</td>
                <td class="num"><button mat-stroked-button class="show" (click)="show(o)">Show · عرض</button></td>
              </tr>
            } @empty {
              <tr><td colspan="9" class="muted">No batches made yet.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .entry h2 {
      margin: 0 0 12px;
      font: var(--mat-sys-title-large);
    }
    h3 {
      margin: 24px 0 8px;
      font: var(--mat-sys-title-medium);
    }
    .form {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 8px 12px;
      margin-bottom: 12px;
    }
    .wide {
      grid-column: span 2;
    }
    tr.short td {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .caution {
      padding: 8px 12px;
      border-radius: 12px;
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
    .small {
      font-size: 0.85em;
    }
    .strong {
      font-weight: 600;
    }
    .nowrap,
    .show {
      white-space: nowrap;
    }
    .actions {
      margin-top: 12px;
    }
  `,
})
export class ProductionPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  // From the query: a prep item to start with, from its profile's "Make a batch".
  readonly prepItemIdParam = input<string | undefined>(undefined, { alias: 'prepItemId' });

  // The prep items that can be made: those with a recipe.
  protected readonly items = signal<PrepItem[]>([]);
  protected readonly orders = signal<ProductionOrder[]>([]);
  protected readonly preview = signal<ProductionPreview | null>(null);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected prepItemId: string | null = null;
  // In the prep item's display unit: 10 (kg).
  protected planned: number | null = null;
  protected produced: number | null = null;
  protected notes = '';

  // One batch, one id, made here: a retry after a lost answer makes it once.
  private orderId: string = crypto.randomUUID();
  private readonly version = signal(0);

  protected readonly item = computed(() => {
    this.version();
    return this.items().find((p) => p.id === this.prepItemId) ?? null;
  });

  protected readonly yieldPercent = computed(() => {
    this.version();
    return this.planned && this.produced ? (this.produced * 100) / this.planned : null;
  });

  protected readonly ready = computed(() => {
    this.version();
    return !!this.item() && (this.planned ?? 0) > 0 && (this.produced ?? 0) > 0;
  });

  constructor() {
    effect(() => {
      const chosen = this.prepItemIdParam();
      untracked(() => void this.load(chosen));
    });
  }

  protected base(unit: UnitOfMeasure): string {
    return baseLabels[unit];
  }

  // Changed after a review: reviewed again before it can be posted.
  protected changed(): void {
    this.preview.set(null);
    this.version.update((v) => v + 1);
  }

  // What came out starts as what was planned.
  protected plannedChanged(): void {
    this.produced = this.planned;
    this.changed();
  }

  protected async review(): Promise<void> {
    const item = this.item();
    if (!item || !this.ready()) {
      return;
    }
    this.saving.set(true);
    try {
      this.preview.set(
        await firstValueFrom(
          this.api.previewProduction(item.id, ProductionPage.toBase(this.planned!, item.displayUnitSize), ProductionPage.toBase(this.produced!, item.displayUnitSize)),
        ),
      );
    } catch (error) {
      this.notify.error(error, 'The batch could not be worked out.');
    } finally {
      this.saving.set(false);
    }
  }

  protected async confirm(): Promise<void> {
    const item = this.item();
    if (!item || !this.preview()) {
      return;
    }
    this.saving.set(true);
    try {
      const made = await firstValueFrom(
        this.api.makeProductionOrder(this.orderId, {
          prepItemId: item.id,
          plannedQuantity: ProductionPage.toBase(this.planned!, item.displayUnitSize),
          producedQuantity: ProductionPage.toBase(this.produced!, item.displayUnitSize),
          notes: this.notes.trim() || null,
        }),
      );
      this.notify.info(
        `Made ${(made.producedQuantity / made.displayUnitSize).toLocaleString('en-EG', { maximumFractionDigits: 3 })} ${made.displayUnit} of ${made.prepItem}, ` +
          `at ${made.unitCostPerDisplayUnit.toFixed(2)} L.E a ${made.displayUnit}.`,
      );
      this.orderId = crypto.randomUUID();
      this.planned = null;
      this.produced = null;
      this.notes = '';
      this.changed();
      this.orders.set(await firstValueFrom(this.api.productionOrders()));
    } catch (error) {
      this.notify.error(error, 'The batch could not be made.');
    } finally {
      this.saving.set(false);
    }
  }

  protected show(order: ProductionOrder): void {
    showProductionOrder(this.dialog, order);
  }

  private async load(chosen: string | undefined): Promise<void> {
    try {
      const [items, orders] = await Promise.all([firstValueFrom(this.api.prepItems()), firstValueFrom(this.api.productionOrders())]);
      this.items.set(items.filter((p) => p.ingredients.length > 0));
      this.orders.set(orders);
      if (chosen && this.items().some((p) => p.id === chosen)) {
        this.prepItemId = chosen;
        this.version.update((v) => v + 1);
        const item = this.item()!;
        // One batch, to start from.
        this.planned = this.produced = item.batchQuantity / item.displayUnitSize;
      }
      this.changed();
    } catch (error) {
      this.error.set(errorMessage(error, 'The prep items could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  // To the thousandth, as stock is kept: 9.6 kg is 9,600 g.
  private static toBase(value: number, size: number): number {
    return Math.round(value * size * 1000) / 1000;
  }
}
