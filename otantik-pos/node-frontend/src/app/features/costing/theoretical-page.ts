import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, ElementRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom, forkJoin } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import {
  CostingApi,
  ManualSalesItem,
  ManualSalesSheet,
  TheoreticalCostReport,
  TheoreticalCostRow,
  TheoreticalSource,
  statusLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

// A group of the sales sheet: a recipe category, or the prep items.
interface SheetGroup {
  name: string;
  items: ManualSalesItem[];
}

// The Monthly Theoretical Cost report: what the month's sales should have cost by their recipes,
// at what the ingredients cost when each was sold. Net sales are before VAT, as menu prices are.
// The sales come from the till, or, where they were rung up elsewhere, from quantities typed in
// for the month: kept for it, and costed by today's recipes at today's prices.
@Component({
  selector: 'app-theoretical-page',
  imports: [
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MoneyPipe,
  ],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Month · الشهر</mat-label>
        <input matInput type="month" [ngModel]="month()" (ngModelChange)="pick($event)" [max]="thisMonth" />
      </mat-form-field>
      <mat-button-toggle-group class="source-toggle" [value]="source()" (change)="choose($event.value)" aria-label="Where the sales come from">
        <mat-button-toggle value="Pos">
          <span class="toggle-label">
            Fetch sales from POS<span class="ar">استقبال المبيعات من الكاشير</span>
          </span>
        </mat-button-toggle>
        <mat-button-toggle value="Manual">
          <span class="toggle-label">
            Manual sales entry<span class="ar">إدخال المبيعات يدوياً</span>
          </span>
        </mat-button-toggle>
      </mat-button-toggle-group>
      <span class="spacer"></span>
      @if (!editing()) {
        @if (source() === 'Manual' && canEdit() && report()) {
          <button mat-stroked-button (click)="edit()"><mat-icon>edit_note</mat-icon> Edit quantities</button>
        }
        <button mat-button (click)="exportCsv()" [disabled]="!report()?.rows?.length"><mat-icon>download</mat-icon> CSV</button>
        <button mat-button (click)="print()" [disabled]="!report()"><mat-icon>print</mat-icon> Print</button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (editing()) {
      @if (sheet(); as s) {
        <header class="report-head">
          <h2>Sales for {{ monthName() }} <span class="inline-ar">مبيعات الشهر</span></h2>
          <p class="note">
            Type how many of each were sold in the month: recipes in portions, prep items in their unit. Leave the rest empty.
            Enter or Tab goes to the next. They are kept for the month, and costed by today's recipes at today's prices.
            <span class="inline-ar">أدخل الكمية المباعة لكل صنف خلال الشهر.</span>
          </p>
        </header>
        @if (s.items.length) {
          <div class="table-wrap">
            <table class="grid sheet">
              <thead>
                <tr>
                  <th>Item code<span class="ar">كود الصنف</span></th>
                  <th>Item<span class="ar">الصنف</span></th>
                  <th class="num">Cost / unit<span class="ar">التكلفة/وحدة</span></th>
                  <th class="num">Price<span class="ar">السعر</span></th>
                  <th class="num qty-head">Qty sold<span class="ar">الكمية المباعة</span></th>
                  <th class="num">Sales<span class="ar">المبيعات</span></th>
                  <th class="num">Ingredient cost<span class="ar">تكلفة المكونات</span></th>
                </tr>
              </thead>
              @for (g of groups(); track g.name) {
                <tbody>
                  <tr class="group">
                    <th colspan="7">{{ g.name }}</th>
                  </tr>
                  @for (item of g.items; track key(item)) {
                    <tr [class.entered]="(quantity(item) ?? 0) > 0">
                      <td class="code">{{ item.code ?? '—' }}</td>
                      <td>
                        {{ item.name }}
                        @if (item.nameAr) {
                          <span class="ar">{{ item.nameAr }}</span>
                        }
                      </td>
                      <td class="num">{{ item.costPerUnit === null ? '—' : (item.costPerUnit | money) }}</td>
                      <td class="num">{{ item.price === null ? '—' : (item.price | money) }}</td>
                      <td class="num">
                        <span class="qty-cell">
                          <input
                            class="qty-in"
                            type="number"
                            min="0"
                            [step]="item.kind === 'Recipe' ? 1 : 'any'"
                            [attr.inputmode]="item.kind === 'Recipe' ? 'numeric' : 'decimal'"
                            placeholder="—"
                            [class.invalid]="!!problemWith(item)"
                            [attr.aria-invalid]="!!problemWith(item)"
                            [attr.aria-label]="item.name + ', sold in ' + unitOf(item)"
                            [title]="problemWith(item) ?? ''"
                            [ngModel]="quantity(item)"
                            (ngModelChange)="enter(item, $event)"
                            (focus)="select($event)"
                            (keydown.enter)="next($event)"
                          />
                          <span class="unit muted">{{ unitOf(item) }}</span>
                        </span>
                      </td>
                      <td class="num">{{ item.price !== null && quantity(item) ? (item.price * quantity(item)! | money) : '' }}</td>
                      <td class="num">{{ item.costPerUnit !== null && quantity(item) ? (item.costPerUnit * quantity(item)! | money) : '' }}</td>
                    </tr>
                  }
                </tbody>
              }
              <tfoot>
                <tr>
                  <td colspan="4">
                    Total · الإجمالي
                    <small class="muted">{{ entered() }} of {{ s.items.length }} entered</small>
                  </td>
                  <td class="num">{{ totals().portions }} {{ totals().portions === 1 ? 'portion' : 'portions' }}</td>
                  <td class="num">{{ totals().sales | money }}</td>
                  <td class="num">{{ totals().cost | money }}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        } @else {
          <p class="note">There are no recipes or prep items yet. Write the recipes first, then type their sales in here.</p>
        }
        <div class="actions">
          @if (invalid()) {
            <span class="error">{{ invalid() }}</span>
          } @else if (dirty()) {
            <span class="muted">Not saved yet: Generate report saves them.</span>
          }
          <span class="spacer"></span>
          @if (s.enteredAtUtc) {
            <button mat-button (click)="cancel()" [disabled]="saving()">Cancel</button>
          }
          <button mat-flat-button class="generate" (click)="generate()" [disabled]="saving() || !!invalid() || !s.items.length">
            <mat-icon>summarize</mat-icon> Generate report<span class="inline-ar">إنشاء التقرير</span>
          </button>
        </div>
      }
    } @else if (report(); as r) {
      <header class="report-head">
        <h2>Monthly theoretical cost <span class="inline-ar">التكلفة النظرية الشهرية</span></h2>
        <div class="muted">
          From<span class="inline-ar">من</span>: <strong>{{ r.from }}</strong> &nbsp; To<span class="inline-ar">إلى</span>:
          <strong>{{ r.to }}</strong> &nbsp;·&nbsp; Food cost target<span class="inline-ar">النسبة المستهدفة</span>:
          <strong>{{ r.foodCostTargetPercent | number: '1.0-1' }}%</strong>
        </div>
        <div class="source-line">
          @if (r.source === 'Manual') {
            <span class="source-badge manual">
              <mat-icon>edit_note</mat-icon> Data source: Manual entry<span class="inline-ar">مصدر البيانات: إدخال يدوي</span>
            </span>
            <span class="muted">
              @if (r.enteredAtUtc) {
                Entered{{ r.enteredBy ? ' by ' + r.enteredBy : '' }} on {{ r.enteredAtUtc | date: 'd MMM y, HH:mm' }} ·
              }
              costed by today's recipes at today's prices
            </span>
          } @else {
            <span class="source-badge pos">
              <mat-icon>point_of_sale</mat-icon> Data source: Live POS<span class="inline-ar">مصدر البيانات: الكاشير</span>
            </span>
          }
        </div>
      </header>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Item code<span class="ar">كود الصنف</span></th>
              @if (r.source === 'Manual') {
                <th>Item<span class="ar">الصنف</span></th>
              } @else {
                <th>Menu item<span class="ar">صنف المنيو</span></th>
              }
              <th class="num">Quantity sold<span class="ar">الكمية المباعة</span></th>
              <th class="num">Net sales<span class="ar">صافي المبيعات</span></th>
              <th class="num">Recipe cost / unit<span class="ar">تكلفة الوصفة/وحدة</span></th>
              <th class="num">Theoretical cost<span class="ar">التكلفة النظرية</span></th>
              <th class="num">Food cost %<span class="ar">نسبة تكلفة الطعام</span></th>
              <th>Status<span class="ar">الحالة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (row of r.rows; track rowKey(row)) {
              <tr>
                <td class="code">{{ row.itemCode || '—' }}</td>
                <td>
                  {{ row.menuItem }}
                  @if (row.menuItemAr) {
                    <span class="ar">{{ row.menuItemAr }}</span>
                  }
                </td>
                <td class="num">{{ row.quantitySold | number: '1.0-3' }}{{ row.unit ? ' ' + row.unit : '' }}</td>
                <td class="num">{{ row.prepItemId ? '—' : (row.netSales | money) }}</td>
                <td class="num">{{ row.status === 'NoRecipe' ? '—' : (row.recipeCostPerUnit | money) }}</td>
                <td class="num">
                  @if (row.status !== 'NoRecipe') {
                    {{ row.theoreticalCost | money }}
                    @if (row.sharedCost > 0) {
                      <small class="muted shared">incl. {{ row.sharedCost | money }} shared</small>
                    }
                  } @else if (row.sharedCost > 0) {
                    <!-- Its share of the month's oil and the like: in the total, so shown here too. -->
                    {{ row.sharedCost | money }}
                    <small class="muted shared">shared only</small>
                  } @else {
                    —
                  }
                  @if (row.quantityCostedNow > 0) {
                    <small class="muted shared">{{ row.quantityCostedNow }} at today's prices *</small>
                  }
                </td>
                <td class="num">{{ row.status === 'NoRecipe' || row.foodCostPercent === null ? '—' : (row.foodCostPercent | number: '1.1-1') + '%' }}</td>
                <td>
                  @if (row.prepItemId) {
                    <span class="status NoPrice">Prep item<span class="ar">صنف تحضير</span></span>
                  } @else {
                    <span class="status" [class]="row.status">
                      {{ statusLabels[row.status].en }}<span class="ar">{{ statusLabels[row.status].ar }}</span>
                    </span>
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="8" class="muted">
                  {{ r.source === 'Manual' ? 'No quantities have been entered for this month.' : 'Nothing was sold in this month.' }}
                </td>
              </tr>
            }
          </tbody>
          @if (r.rows.length) {
            <tfoot>
              <tr>
                <td colspan="2">Total · الإجمالي</td>
                <td class="num">{{ r.quantitySold | number: '1.0-3' }}</td>
                <td class="num">{{ r.netSales | money }}</td>
                <td></td>
                <td class="num">{{ r.theoreticalCost | money }}</td>
                <td class="num">{{ r.foodCostPercent === null ? '—' : (r.foodCostPercent | number: '1.1-1') + '%' }}</td>
                <td></td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
      @if (costedNow(r)) {
        <p class="note">
          * Sold before the dish had a recipe, so no cost was recorded at the time: costed by its recipe now, at today's
          average prices.
        </p>
      }
      @if (unrecipedSales(r); as missing) {
        <p class="note">
          {{ missing.count }} {{ missing.count === 1 ? 'menu item' : 'menu items' }} sold this month {{ missing.count === 1 ? 'has' : 'have' }}
          no recipe ({{ missing.sales | money }} of sales): {{ missing.count === 1 ? 'it counts' : 'they count' }} in net sales at no
          ingredient cost, so the total food cost % reads low until {{ missing.count === 1 ? 'it has one' : 'they have recipes' }}.
        </p>
      }
      @if (r.source === 'Manual' && unpricedCost(r); as unpriced) {
        <p class="note">
          Cost with no sales against it ({{ unpriced | money }}): prep items, and recipes off the menu with no planned price. It is in the
          theoretical cost but not in net sales, so the total food cost % reads high by it.
        </p>
      }
    } @else if (source() === 'Manual' && !loading()) {
      <p class="note">No quantities have been entered for this month. Someone who manages stock types them in.</p>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .report-head {
      margin-bottom: 12px;

      h2 {
        margin: 0 0 4px;
        font: var(--mat-sys-title-large);
      }
      .note {
        margin: 0;
      }
    }
    .shared {
      display: block;
    }
    .source-toggle .toggle-label {
      display: inline-flex;
      flex-direction: column;
      line-height: 1.25;
      padding: 4px 0;
    }
    .source-toggle .ar {
      text-align: center;
    }
    .source-line {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 12px;
      margin-top: 8px;
    }
    .source-badge {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 3px 12px 3px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-large);
      print-color-adjust: exact;
      -webkit-print-color-adjust: exact;

      mat-icon {
        width: 18px;
        height: 18px;
        font-size: 18px;
      }
      &.pos {
        background: color-mix(in srgb, var(--till-ok) 16%, transparent);
        color: var(--till-ok);
      }
      &.manual {
        background: color-mix(in srgb, var(--till-warn) 16%, transparent);
        color: var(--till-warn);
      }
    }
    table.sheet {
      tr.group th {
        background: var(--mat-sys-surface-container-low);
        color: var(--mat-sys-on-surface);
        font: var(--mat-sys-title-small);
      }
      tr.entered td {
        background: color-mix(in srgb, var(--mat-sys-primary) 6%, transparent);
      }
      td {
        padding-top: 4px;
        padding-bottom: 4px;
      }
    }
    .qty-cell {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .qty-in {
      width: 96px;
      padding: 6px 8px;
      border: 1px solid var(--mat-sys-outline);
      border-radius: 8px;
      background: var(--mat-sys-surface);
      color: var(--mat-sys-on-surface);
      font: var(--mat-sys-body-large);
      text-align: end;

      &:focus {
        outline: 2px solid var(--mat-sys-primary);
        outline-offset: -1px;
        border-color: var(--mat-sys-primary);
      }
      &.invalid {
        border-color: var(--mat-sys-error);
        outline-color: var(--mat-sys-error);
      }
    }
    .unit {
      min-width: 3.5em;
      text-align: start;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 12px;
      margin-top: 12px;

      .spacer {
        flex: 1;
      }
    }
  `,
})
export class TheoreticalPage {
  private readonly api = inject(CostingApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly statusLabels = statusLabels;
  protected readonly thisMonth = TheoreticalPage.monthOf(new Date());
  // Typing the month's sales in is for someone who manages stock; anyone here can read them.
  protected readonly canEdit = computed(() => this.auth.can(Permissions.InventoryManage));

  protected readonly month = signal(this.thisMonth);
  protected readonly source = signal<TheoreticalSource>('Pos');
  protected readonly report = signal<TheoreticalCostReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  // The month's sales sheet, and what is typed into it: kept for each month while on this page,
  // so looking at the till's figures, or another month, loses nothing.
  protected readonly sheet = signal<ManualSalesSheet | null>(null);
  protected readonly editing = signal(false);
  private readonly drafts = signal<Record<string, Record<string, number | null>>>({});

  protected readonly monthName = computed(() => {
    const [year, m] = this.month().split('-').map(Number);
    return new Date(year, m - 1, 1).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
  });

  protected readonly groups = computed<SheetGroup[]>(() => {
    const groups: SheetGroup[] = [];
    for (const item of this.sheet()?.items ?? []) {
      const last = groups.at(-1);
      if (last?.name === item.group) {
        last.items.push(item);
      } else {
        groups.push({ name: item.group, items: [item] });
      }
    }
    return groups;
  });

  private readonly typed = computed(() => this.drafts()[this.month()] ?? {});

  protected readonly entered = computed(() => (this.sheet()?.items ?? []).filter((i) => (this.quantity(i) ?? 0) > 0).length);

  protected readonly totals = computed(() => {
    let portions = 0;
    let sales = 0;
    let cost = 0;
    for (const item of this.sheet()?.items ?? []) {
      const quantity = this.quantity(item) ?? 0;
      if (quantity <= 0) {
        continue;
      }
      if (item.kind === 'Recipe') {
        portions += quantity;
      }
      sales += (item.price ?? 0) * quantity;
      cost += (item.costPerUnit ?? 0) * quantity;
    }
    return { portions, sales, cost };
  });

  // The first thing wrong with what is typed in, said so it can be put right.
  protected readonly invalid = computed(() => {
    const items = this.sheet()?.items ?? [];
    const wrong = items.filter((i) => this.problemWith(i));
    if (!wrong.length) {
      return null;
    }
    return wrong.length === 1 ? `${wrong[0].name}: ${this.problemWith(wrong[0])}` : `${wrong.length} quantities need correcting (marked in red).`;
  });

  protected readonly dirty = computed(() => this.drafts()[this.month()] !== undefined);

  constructor() {
    effect(() => {
      const month = this.month();
      const source = this.source();
      untracked(() => void this.load(month, source));
    });
  }

  protected pick(month: string): void {
    // Cleared or half typed: keep the month on screen.
    if (/^\d{4}-\d{2}$/.test(month)) {
      this.month.set(month);
    }
  }

  protected choose(source: TheoreticalSource): void {
    this.source.set(source);
  }

  protected key(item: ManualSalesItem): string {
    return `${item.kind}:${item.itemId}`;
  }

  protected rowKey(row: TheoreticalCostRow): string {
    return row.recipeId ?? row.prepItemId ?? row.itemCode;
  }

  protected unitOf(item: ManualSalesItem): string {
    return item.kind === 'Recipe' ? 'portions' : item.unit;
  }

  protected quantity(item: ManualSalesItem): number | null {
    const typed = this.typed();
    const key = this.key(item);
    return key in typed ? typed[key] : item.quantity;
  }

  protected problemWith(item: ManualSalesItem): string | null {
    const quantity = this.quantity(item);
    if (quantity === null) {
      return null;
    }
    if (!Number.isFinite(quantity) || quantity < 0) {
      return 'cannot be less than 0.';
    }
    if (item.kind === 'Recipe' && !Number.isInteger(quantity)) {
      return 'portions are whole numbers.';
    }
    return null;
  }

  protected enter(item: ManualSalesItem, value: number | string | null): void {
    const quantity = value === null || value === '' ? null : Number(value);
    const month = this.month();
    this.drafts.update((drafts) => ({ ...drafts, [month]: { ...(drafts[month] ?? {}), [this.key(item)]: quantity } }));
  }

  protected select(event: Event): void {
    (event.target as HTMLInputElement).select();
  }

  // Enter goes on down the sheet, as Tab does; Shift+Enter back up. From the last, to Generate.
  protected next(event: Event): void {
    event.preventDefault();
    const inputs = [...this.host.nativeElement.querySelectorAll<HTMLInputElement>('input.qty-in')];
    const at = inputs.indexOf(event.target as HTMLInputElement);
    const step = (event as KeyboardEvent).shiftKey ? -1 : 1;
    const to = inputs[at + step];
    if (to) {
      to.focus();
    } else if (step > 0) {
      this.host.nativeElement.querySelector<HTMLButtonElement>('button.generate')?.focus();
    }
  }

  protected edit(): void {
    this.editing.set(true);
  }

  protected cancel(): void {
    this.forget(this.month());
    this.editing.set(false);
  }

  protected async generate(): Promise<void> {
    const sheet = this.sheet();
    if (!sheet || this.invalid()) {
      return;
    }
    const lines = sheet.items
      .map((item) => ({ kind: item.kind, itemId: item.itemId, quantity: this.quantity(item) ?? 0 }))
      .filter((line) => line.quantity > 0);
    const month = this.month();
    this.saving.set(true);
    try {
      const report = await firstValueFrom(this.api.saveManualSales(sheet.year, sheet.month, lines));
      const saved = new Map(lines.map((l) => [`${l.kind}:${l.itemId}`, l.quantity]));
      this.sheet.set({
        ...sheet,
        items: sheet.items.map((item) => ({ ...item, quantity: saved.get(this.key(item)) ?? null })),
        enteredBy: report.enteredBy,
        enteredAtUtc: report.enteredAtUtc,
      });
      this.forget(month);
      this.report.set(report);
      this.editing.set(false);
      this.notify.info(lines.length ? 'Saved. The report is from the quantities entered.' : 'Saved with nothing entered.');
    } catch (error) {
      this.notify.error(error, 'The quantities could not be saved.');
    } finally {
      this.saving.set(false);
    }
  }

  protected unrecipedSales(report: TheoreticalCostReport): { count: number; sales: number } | null {
    const rows = report.rows.filter((r) => r.status === 'NoRecipe');
    return rows.length ? { count: rows.length, sales: rows.reduce((sum, r) => sum + r.netSales, 0) } : null;
  }

  protected costedNow(report: TheoreticalCostReport): boolean {
    return report.rows.some((r) => r.quantityCostedNow > 0);
  }

  // Typed in, what was costed with no price to set it against.
  protected unpricedCost(report: TheoreticalCostReport): number {
    return report.rows.filter((r) => r.netSales === 0).reduce((sum, r) => sum + r.theoreticalCost, 0);
  }

  protected print(): void {
    window.print();
  }

  // For a spreadsheet: a byte order mark so Excel reads the Arabic names as UTF-8. The last line
  // says where the sales came from.
  protected exportCsv(): void {
    const r = this.report();
    if (!r) {
      return;
    }
    const header = [
      'Item Code · كود الصنف',
      'Menu Item · صنف المنيو',
      'Menu Item (Arabic)',
      'Category',
      'Quantity Sold · الكمية المباعة',
      'Net Sales · صافي المبيعات',
      'Recipe Cost/Unit · تكلفة الوصفة/وحدة',
      'Theoretical Cost · التكلفة النظرية',
      'Shared Cost',
      "Qty Costed at Today's Prices",
      'Food Cost % · نسبة تكلفة الطعام',
      'Status · الحالة',
    ];
    const noRecipe = (status: string) => status === 'NoRecipe';
    const rows = r.rows.map((row) => [
      row.itemCode,
      row.menuItem,
      row.menuItemAr ?? '',
      row.category,
      // A prep item's quantity is in its unit, not portions: said so, rather than summed with them.
      row.unit ? `${row.quantitySold} ${row.unit}` : row.quantitySold,
      row.netSales.toFixed(2),
      noRecipe(row.status) ? '' : row.recipeCostPerUnit.toFixed(2),
      noRecipe(row.status) && row.sharedCost === 0 ? '' : row.theoreticalCost.toFixed(2),
      row.sharedCost.toFixed(2),
      row.quantityCostedNow,
      noRecipe(row.status) || row.foodCostPercent === null ? '' : row.foodCostPercent.toFixed(1),
      row.prepItemId ? 'Prep item · صنف تحضير' : `${statusLabels[row.status].en} · ${statusLabels[row.status].ar}`,
    ]);
    const total = ['Total', '', '', '', r.quantitySold, r.netSales.toFixed(2), '', r.theoreticalCost.toFixed(2), '', '', r.foodCostPercent?.toFixed(1) ?? '', ''];
    const source =
      r.source === 'Manual'
        ? [`Data Source: Manual Entry · مصدر البيانات: إدخال يدوي${r.enteredBy ? ` (${r.enteredBy}, ${r.enteredAtUtc?.slice(0, 10)})` : ''}`]
        : ['Data Source: Live POS · مصدر البيانات: الكاشير'];
    const csv = [header, ...rows, total, source].map((cells) => cells.map(TheoreticalPage.cell).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `theoretical-cost-${this.month()}${r.source === 'Manual' ? '-manual' : ''}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  private forget(month: string): void {
    this.drafts.update((drafts) => {
      const rest = { ...drafts };
      delete rest[month];
      return rest;
    });
  }

  private async load(month: string, source: TheoreticalSource): Promise<void> {
    const [year, m] = month.split('-').map(Number);
    this.loading.set(true);
    this.error.set(null);
    this.report.set(null);
    this.sheet.set(null);
    this.editing.set(false);
    // Moved on while it loaded: what came back is for another month, or the other source.
    const stale = () => this.month() !== month || this.source() !== source;
    try {
      if (source === 'Pos') {
        const report = await firstValueFrom(this.api.theoretical(year, m));
        if (!stale()) {
          this.report.set(report);
        }
        return;
      }
      const { sheet, report } = await firstValueFrom(
        forkJoin({ sheet: this.api.manualSales(year, m), report: this.api.theoretical(year, m, 'Manual') }),
      );
      if (stale()) {
        return;
      }
      this.sheet.set(sheet);
      // Never entered, or part typed before looking away: straight to the sheet.
      const neverEntered = !sheet.enteredAtUtc;
      this.report.set(neverEntered ? null : report);
      this.editing.set(this.canEdit() && (neverEntered || this.dirty()));
    } catch (error) {
      if (stale()) {
        return;
      }
      this.report.set(null);
      this.error.set(errorMessage(error, source === 'Pos' ? 'The report could not be loaded.' : 'The sales sheet could not be loaded.'));
    } finally {
      if (!stale()) {
        this.loading.set(false);
      }
    }
  }

  private static cell(value: string | number): string {
    const text = String(value);
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  }

  private static monthOf(date: Date): string {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
  }
}
