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
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PrepItem, ProductionOrder, ProductionPreview, UnitOfMeasure, isRequested } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from './approvals';
import { showProductionOrder } from './production-order-dialog';

const baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

// The kitchen's prep work, logged. Choose the prep item, how much was planned (which sets the
// ingredients, by its recipe) and how much came out; review what it takes against what the
// records show and what it costs at today's averages; then Confirm & post, which takes the
// ingredients off stock and puts the prep item in at their cost over what came out.
//
// A batch moves two stocks, so it is a manager's. Anyone else sends it for approval, and nothing
// moves until a manager approves it, at the averages then; the same for a correction (the batch
// reversed and made again) and a deletion (the batch reversed). Waiting ones are listed, locked.
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
    MatTooltipModule,
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
        @if (editing(); as e) {
          <h2>Correct a batch <span class="inline-ar">تصحيح إذن تصنيع</span></h2>
          <div class="pending-banner">
            <mat-icon>edit_note</mat-icon>
            <span>
              The batch of {{ e.prepItem }} made {{ e.producedAtUtc | date: 'd MMM y, HH:mm' }}: {{ e.plannedQuantity / e.displayUnitSize | number: '1.0-3' }}
              {{ e.displayUnit }} planned, {{ e.producedQuantity / e.displayUnitSize | number: '1.0-3' }} {{ e.displayUnit }} came out. Enter what it should have
              been: {{ approvals.canApprove() ? 'posting' : 'once a manager approves, it' }} reverses the batch and makes it again with these quantities,
              at the average costs of that moment.
            </span>
          </div>
        } @else {
          <h2>Make a batch <span class="inline-ar">إذن تصنيع جديد</span></h2>
        }
        @if (!approvals.canApprove()) {
          <p class="note">
            A manager approves batches: nothing moves in stock until then, and the batch is costed at the averages on the day it is approved.
            <span class="inline-ar">يُرسل للمدير للموافقة، ولا يتحرك المخزون قبلها.</span>
          </p>
        }
        @if (items().length === 0) {
          <p class="note">No prep items with a recipe yet. <a routerLink="../prep/new">Add one</a>, such as pizza dough, first.</p>
        } @else {
          <div class="form">
            <mat-form-field class="wide">
              <mat-label>Prep item · الصنف</mat-label>
              <mat-select [(ngModel)]="prepItemId" (ngModelChange)="changed()" aria-label="Prep item" [disabled]="!!editing()">
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
              <input matInput [(ngModel)]="notes" (ngModelChange)="changed()" maxlength="200" placeholder="Morning batch" />
            </mat-form-field>
            @if (editing()) {
              <mat-form-field class="wide">
                <mat-label>Why · السبب</mat-label>
                <input matInput [(ngModel)]="reason" maxlength="500" placeholder="Weighed again" />
              </mat-form-field>
            }
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
            @if (editing()) {
              <button mat-button (click)="stopEditing()" [disabled]="saving()">Cancel the correction</button>
            }
            <span class="spacer"></span>
            @if (preview()) {
              <button mat-flat-button (click)="confirm()" [disabled]="saving() || !!preview()!.missingPrices.length">
                <mat-icon>{{ approvals.canApprove() ? 'inventory' : 'send' }}</mat-icon> {{ confirmLabel() }}
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
            @for (r of waiting(); track r.id) {
              <tr class="pending-deletion">
                <td class="nowrap">—</td>
                <td colspan="7">
                  {{ r.summary }}
                  <div>
                    <span class="pending-badge">{{ pendingLabel(r).en }}<span class="ar">{{ pendingLabel(r).ar }}</span></span>
                    <span class="muted small"> asked by {{ r.requestedBy }}, {{ r.requestedAtUtc | date: 'd MMM, HH:mm' }}: nothing has moved yet</span>
                  </div>
                </td>
                <td></td>
              </tr>
            }
            @for (o of orders(); track o.id) {
              @let asked = approvals.pendingFor(o.id);
              <tr [class.voided]="o.status === 'Voided'" [class.pending-deletion]="!!asked" [class.editing]="editing()?.id === o.id">
                <td class="nowrap">{{ o.producedAtUtc | date: 'd MMM, HH:mm' }}</td>
                <td>
                  {{ o.prepItem }}
                  @if (o.status === 'Voided') {
                    <div>
                      <span class="voided-badge">{{ o.replacedBy ? 'Corrected' : 'Voided' }}<span class="ar">{{ o.replacedBy ? 'مُصحح' : 'ملغى' }}</span></span>
                      <span class="muted small"> by {{ o.voidedBy }}, {{ o.voidedAtUtc | date: 'd MMM' }}</span>
                    </div>
                  } @else if (asked) {
                    <div>
                      <span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span>
                      <span class="muted small"> by {{ asked.requestedBy }}</span>
                    </div>
                  } @else if (o.revisionOf) {
                    <div class="muted small">A correction</div>
                  }
                </td>
                <td class="num">{{ o.plannedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</td>
                <td class="num">{{ o.producedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</td>
                <td class="num">{{ o.yieldPercent | number: '1.0-1' }}%</td>
                <td class="num">{{ o.totalCost | money }}</td>
                <td class="num">{{ o.unitCostPerDisplayUnit | money }} / {{ o.displayUnit }}</td>
                <td>
                  {{ o.producedBy || '—' }}
                  @if (o.approvedBy) {
                    <div class="muted small">approved by {{ o.approvedBy }}</div>
                  }
                </td>
                <td class="num row-actions">
                  <button mat-stroked-button class="show" (click)="show(o)">Show · عرض</button>
                  @if (o.status === 'Made' && !asked) {
                    <button mat-icon-button (click)="startEditing(o)" [disabled]="saving()" matTooltip="Correct the quantities"
                      [attr.aria-label]="'Correct the batch of ' + o.prepItem">
                      <mat-icon>edit</mat-icon>
                    </button>
                    <button mat-icon-button class="danger" (click)="remove(o)" [disabled]="saving()" matTooltip="Delete (void)"
                      [attr.aria-label]="'Delete the batch of ' + o.prepItem">
                      <mat-icon>delete</mat-icon>
                    </button>
                  } @else if (asked) {
                    <mat-icon class="locked" matTooltip="Locked until a manager decides">lock_clock</mat-icon>
                  }
                </td>
              </tr>
            } @empty {
              @if (!waiting().length) {
                <tr><td colspan="9" class="muted">No batches made yet.</td></tr>
              }
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
    .row-actions {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    tr.voided td {
      color: var(--mat-sys-on-surface-variant);
    }
    tr.editing td {
      background: var(--mat-sys-secondary-container);
    }
  `,
})
export class ProductionPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

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
  // Why a correction, for the manager.
  protected reason = '';

  // The batch being corrected, if one is: the form holds what it should have been.
  protected readonly editing = signal<ProductionOrder | null>(null);
  // Batches asked for, waiting for a manager: nothing has moved for them.
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('ProductionOrder'));

  // One batch, one id, made here: a retry after a lost answer makes it once. The same for the
  // request that asks for it, or for a correction.
  private orderId: string = crypto.randomUUID();
  private requestId: string = crypto.randomUUID();
  private readonly version = signal(0);

  protected readonly item = computed(() => {
    this.version();
    return this.items().find((p) => p.id === this.prepItemId) ?? null;
  });

  protected readonly yieldPercent = computed(() => {
    this.version();
    return this.planned && this.produced ? (this.produced * 100) / this.planned : null;
  });

  // Posting for a manager; a request for anyone else.
  protected readonly confirmLabel = computed(() =>
    this.approvals.canApprove() ? (this.editing() ? 'Correct & post' : 'Confirm & post') : 'Send for approval · إرسال للموافقة',
  );

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
    const quantities = {
      plannedQuantity: ProductionPage.toBase(this.planned!, item.displayUnitSize),
      producedQuantity: ProductionPage.toBase(this.produced!, item.displayUnitSize),
      notes: this.notes.trim() || null,
    };
    const editing = this.editing();
    this.saving.set(true);
    try {
      if (editing) {
        const result = await firstValueFrom(
          this.api.reviseProductionOrder(editing.id, {
            ...quantities,
            requestId: this.requestId,
            newOrderId: this.orderId,
            reason: this.reason.trim() || null,
          }),
        );
        if (result.outcome === 'Requested') {
          await this.approvals.requested(result);
        } else {
          this.notify.info(`Corrected: the batch of ${item.name} is reversed and made again.`);
        }
      } else {
        const made = await firstValueFrom(
          this.api.makeProductionOrder(this.orderId, { ...quantities, prepItemId: item.id, requestId: this.requestId }),
        );
        if (isRequested(made)) {
          await this.approvals.requested(made);
        } else {
          this.notify.info(
            `Made ${(made.producedQuantity / made.displayUnitSize).toLocaleString('en-EG', { maximumFractionDigits: 3 })} ${made.displayUnit} of ${made.prepItem}, ` +
              `at ${made.unitCostPerDisplayUnit.toFixed(2)} L.E a ${made.displayUnit}.`,
          );
        }
      }
      this.reset();
      this.orders.set(await firstValueFrom(this.api.productionOrders()));
    } catch (error) {
      this.notify.error(error, editing ? 'The batch could not be corrected.' : 'The batch could not be made.');
    } finally {
      this.saving.set(false);
    }
  }

  // The form filled with the batch as it was, to enter what it should have been.
  protected startEditing(order: ProductionOrder): void {
    this.reset();
    this.editing.set(order);
    this.prepItemId = order.prepItemId;
    this.planned = order.plannedQuantity / order.displayUnitSize;
    this.produced = order.producedQuantity / order.displayUnitSize;
    this.notes = order.notes ?? '';
    this.changed();
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  protected stopEditing(): void {
    this.reset();
  }

  // Voided for a manager (its entries reversed); asked of one for anyone else.
  protected async remove(order: ProductionOrder): Promise<void> {
    const made = new Date(order.producedAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
    if ((await this.approvals.delete('ProductionOrder', order.id, `the batch of ${order.prepItem} made ${made}`)) === 'Applied') {
      this.orders.set(await firstValueFrom(this.api.productionOrders()));
    }
  }

  // A fresh form, with new ids: the next batch or request is another one.
  private reset(): void {
    this.editing.set(null);
    this.orderId = crypto.randomUUID();
    this.requestId = crypto.randomUUID();
    this.planned = null;
    this.produced = null;
    this.notes = '';
    this.reason = '';
    this.changed();
  }

  protected show(order: ProductionOrder): void {
    showProductionOrder(this.dialog, order);
  }

  private async load(chosen: string | undefined): Promise<void> {
    void this.approvals.refresh();
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
