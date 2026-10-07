import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { BreakEvenColumn, BreakEvenWorksheet, CostingApi, breakEvenColumn } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

type Row = { label: string; ar: string; value: (c: BreakEvenColumn) => number | null; indent?: boolean; strong?: boolean; percent?: boolean; profit?: boolean };

// The template's rows, top to bottom.
const rows: Row[] = [
  { label: 'Gross sales (a year)', ar: 'إجمالي المبيعات السنوية', value: (c) => c.grossSales },
  { label: 'Less: cost of sales', ar: 'ناقص: تكلفة المبيعات', value: (c) => c.costOfSales, indent: true },
  { label: 'Gross profit', ar: 'الربح الإجمالي', value: (c) => c.grossProfit, strong: true },
  { label: 'Profit margin', ar: 'هامش الربح', value: (c) => c.profitMarginPercent, indent: true, percent: true },
  { label: 'Controllable costs', ar: 'التكاليف القابلة للتحكم', value: (c) => c.controllableCosts },
  { label: 'Occupation cost', ar: 'تكلفة الإشغال', value: (c) => c.occupationCost, indent: true },
  { label: 'Interest', ar: 'الفوائد', value: (c) => c.interest, indent: true },
  { label: 'Depreciation', ar: 'الإهلاك', value: (c) => c.depreciation, indent: true },
  { label: 'Total fixed costs', ar: 'إجمالي التكاليف الثابتة', value: (c) => c.totalFixedCosts, strong: true },
  { label: 'Restaurant profit', ar: 'ربح المطعم', value: (c) => c.restaurantProfit, strong: true, profit: true },
];

// The chart's frame, in SVG units.
const chart = { width: 680, height: 320, left: 78, right: 16, top: 16, bottom: 40 };

// The sixth template: the Break-Even Analysis and Scenario Worksheet. The Income Statement
// column is the chosen months at a year's pace; the four options try other weekly sales against
// the same costs, at the same cost of sales ratio. The chart shows where sales meet total costs.
@Component({
  selector: 'app-break-even-page',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>From · من</mat-label>
        <input matInput type="month" [ngModel]="from()" (ngModelChange)="pick('from', $event)" [max]="thisMonth" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>To · إلى</mat-label>
        <input matInput type="month" [ngModel]="to()" (ngModelChange)="pick('to', $event)" [max]="thisMonth" />
      </mat-form-field>
      <span class="spacer"></span>
      @if (canEdit() && optionsChanged()) {
        <button mat-button (click)="saveOptions()" [disabled]="saving()"><mat-icon>bookmark</mat-icon> Keep these options</button>
      }
      <button mat-button (click)="print()" [disabled]="!sheet()"><mat-icon>print</mat-icon> Print</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (sheet(); as s) {
      <header class="report-head">
        <h2>Break-even analysis and scenario worksheet <span class="inline-ar">تحليل نقطة التعادل والسيناريوهات</span></h2>
        <div class="muted">
          Income statement from {{ s.days }} {{ s.days === 1 ? 'day' : 'days' }} of sales, {{ s.from }} to {{ s.to }}, at a year's pace
          (weekly × 52); costs from the months' entered costs × 12.
        </div>
      </header>

      @if (s.monthsWithoutCosts.length) {
        <p class="note warn">
          No costs entered for {{ s.monthsWithoutCosts.join(', ') }}: the fixed costs come from the other months.
          Enter them on the <a routerLink="/costing/kpis">KPIs</a> tab.
        </p>
      }

      <div class="table-wrap">
        <table class="grid sheet">
          <thead>
            <tr>
              <th></th>
              <th class="num">Income statement<span class="ar">قائمة الدخل</span></th>
              @for (o of options(); track $index) {
                <th class="num">Option #{{ $index + 1 }}<span class="ar">خيار {{ $index + 1 }}</span></th>
              }
            </tr>
          </thead>
          <tbody>
            <tr class="weekly">
              <td>Weekly sales<span class="ar">المبيعات الأسبوعية</span></td>
              <td class="num strong">{{ s.weeklySales | money }}</td>
              @for (o of options(); track $index; let i = $index) {
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="option">
                    <span matTextPrefix>L.E&nbsp;</span>
                    <input matInput type="number" min="0" step="any" [ngModel]="o" (ngModelChange)="setOption(i, $event)" [attr.aria-label]="'Option ' + (i + 1) + ' weekly sales'" />
                  </mat-form-field>
                </td>
              }
            </tr>
            @for (row of rows; track row.label) {
              <tr [class.strong]="row.strong">
                <td [class.indent]="row.indent">{{ row.label }}<span class="ar">{{ row.ar }}</span></td>
                @for (c of columns(); track $index) {
                  @let v = row.value(c);
                  <td class="num" [class.positive]="row.profit && v !== null && v > 0" [class.negative]="row.profit && v !== null && v < 0">
                    {{ v === null ? '—' : row.percent ? (v | number: '1.0-1') + '%' : (v | money) }}
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>
      </div>

      <section class="chart">
        <h3>Break-even chart <span class="inline-ar">رسم نقطة التعادل</span></h3>
        @if (s.breakEvenYearlySales !== null) {
          <p class="muted">
            Break even at <strong>{{ s.breakEvenYearlySales | money }}</strong> yearly sales, which is
            <strong>{{ s.breakEvenWeeklySales | money }}</strong> weekly sales.
            <span class="inline-ar">نقطة التعادل</span>
          </p>
          @if (plot(); as p) {
            <svg [attr.viewBox]="'0 0 ' + frame.width + ' ' + frame.height" role="img"
              [attr.aria-label]="'Break-even chart: break even at ' + (s.breakEvenWeeklySales | money) + ' weekly sales'">
              @for (t of p.yTicks; track t.value) {
                <line class="gridline" [attr.x1]="frame.left" [attr.x2]="frame.width - frame.right" [attr.y1]="t.y" [attr.y2]="t.y" />
                <text class="tick" [attr.x]="frame.left - 6" [attr.y]="t.y + 4" text-anchor="end">{{ t.label }}</text>
              }
              @for (t of p.xTicks; track t.value) {
                <text class="tick" [attr.x]="t.x" [attr.y]="frame.height - frame.bottom + 16" text-anchor="middle">{{ t.label }}</text>
              }
              <text class="axis" [attr.x]="(frame.left + frame.width - frame.right) / 2" [attr.y]="frame.height - 4" text-anchor="middle">
                Weekly sales (L.E) · المبيعات الأسبوعية
              </text>
              <line class="fixed" [attr.x1]="p.x(0)" [attr.y1]="p.y(s.totalFixedCosts)" [attr.x2]="p.x(p.xMax)" [attr.y2]="p.y(s.totalFixedCosts)" />
              <line class="costs" [attr.x1]="p.x(0)" [attr.y1]="p.y(s.totalFixedCosts)" [attr.x2]="p.x(p.xMax)" [attr.y2]="p.y(s.totalFixedCosts + p.xMax * 52 * s.costOfSalesRatio)" />
              <line class="sales" [attr.x1]="p.x(0)" [attr.y1]="p.y(0)" [attr.x2]="p.x(p.xMax)" [attr.y2]="p.y(p.xMax * 52)" />
              @for (o of p.points; track o.label) {
                <circle [attr.class]="o.profit >= 0 ? 'point positive' : 'point negative'" [attr.cx]="p.x(o.weekly)" [attr.cy]="p.y(o.weekly * 52)" r="5" />
                <text class="point-label" [attr.x]="p.x(o.weekly)" [attr.y]="p.y(o.weekly * 52) - 9" text-anchor="middle">{{ o.label }}</text>
              }
              <rect class="breakeven" [attr.x]="p.x(s.breakEvenWeeklySales!) - 6" [attr.y]="p.y(s.breakEvenYearlySales) - 6" width="12" height="12" />
            </svg>
          }
          <div class="legend">
            <span><i class="key sales"></i>Sales · المبيعات</span>
            <span><i class="key costs"></i>Total costs · إجمالي التكاليف</span>
            <span><i class="key fixed"></i>Fixed costs · التكاليف الثابتة</span>
            <span><i class="key breakeven"></i>Break even · التعادل</span>
            <span><i class="key positive"></i>Profit · ربح</span>
            <span><i class="key negative"></i>Loss · خسارة</span>
          </div>
        } @else if (s.monthsWithoutCosts.length && s.totalFixedCosts === 0) {
          <p class="note">Enter the months' costs on the <a routerLink="/costing/kpis">KPIs</a> tab: break-even needs the fixed costs.</p>
        } @else {
          <p class="error">Every sale costs more than it brings in at these months' cost of sales: no level of sales breaks even.</p>
        }
      </section>
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
    }
    h3 {
      margin: 20px 0 4px;
      font: var(--mat-sys-title-medium);
    }
    .sheet {
      td.indent {
        padding-left: 28px;
      }
      tr.strong td,
      .strong {
        font-weight: 600;
      }
      tr.weekly td {
        background: color-mix(in srgb, #f5d000 18%, var(--mat-sys-surface));
      }
      td.positive {
        background: color-mix(in srgb, var(--till-ok) 18%, var(--mat-sys-surface));
      }
      td.negative {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, var(--mat-sys-surface));
        color: var(--mat-sys-error);
      }
    }
    .option {
      width: 150px;
    }
    .chart svg {
      width: 100%;
      max-width: 820px;
      height: auto;
      display: block;
    }
    .gridline {
      stroke: var(--mat-sys-outline-variant);
      stroke-width: 1;
    }
    .tick,
    .axis,
    .point-label {
      font-size: 11px;
      fill: var(--mat-sys-on-surface-variant);
    }
    .point-label {
      fill: var(--mat-sys-on-surface);
      font-weight: 600;
    }
    .sales {
      stroke: #1e88e5;
      stroke-width: 2.5;
    }
    .costs {
      stroke: #263238;
      stroke-width: 2;
      stroke-dasharray: 6 5;
    }
    .fixed {
      stroke: #ef6c00;
      stroke-width: 2;
      stroke-dasharray: 10 6;
    }
    .breakeven {
      fill: var(--mat-sys-surface);
      stroke: #263238;
      stroke-width: 2;
    }
    .point.positive {
      fill: var(--till-ok);
    }
    .point.negative {
      fill: var(--mat-sys-error);
    }
    .legend {
      display: flex;
      flex-wrap: wrap;
      gap: 6px 16px;
      font: var(--mat-sys-body-small);
      margin-top: 6px;

      .key {
        display: inline-block;
        width: 18px;
        height: 4px;
        margin-right: 6px;
        vertical-align: middle;
        border-radius: 2px;

        &.sales {
          background: #1e88e5;
        }
        &.costs {
          background: #263238;
        }
        &.fixed {
          background: #ef6c00;
        }
        &.breakeven {
          width: 10px;
          height: 10px;
          border: 2px solid #263238;
          background: transparent;
        }
        &.positive {
          background: var(--till-ok);
          height: 10px;
          width: 10px;
          border-radius: 50%;
        }
        &.negative {
          background: var(--mat-sys-error);
          height: 10px;
          width: 10px;
          border-radius: 50%;
        }
      }
    }
    .warn {
      color: var(--mat-sys-on-surface);
    }
  `,
})
export class BreakEvenPage {
  private readonly api = inject(CostingApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  protected readonly rows = rows;
  protected readonly frame = chart;
  protected readonly canEdit = computed(() => this.auth.can(Permissions.InventoryManage));
  protected readonly thisMonth = BreakEvenPage.monthOf(new Date());

  protected readonly from = signal(this.thisMonth);
  protected readonly to = signal(this.thisMonth);
  protected readonly sheet = signal<BreakEvenWorksheet | null>(null);
  protected readonly options = signal<number[]>([]);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  // The income statement, then each option, worked out as the template does.
  protected readonly columns = computed<BreakEvenColumn[]>(() => {
    const sheet = this.sheet();
    if (!sheet) {
      return [];
    }
    const base: BreakEvenColumn = {
      weeklySales: sheet.weeklySales,
      grossSales: sheet.grossSales,
      costOfSales: sheet.costOfSales,
      grossProfit: sheet.grossProfit,
      profitMarginPercent: sheet.grossSales > 0 ? (sheet.grossProfit / sheet.grossSales) * 100 : null,
      controllableCosts: sheet.controllableCosts,
      occupationCost: sheet.occupationCost,
      interest: sheet.interest,
      depreciation: sheet.depreciation,
      totalFixedCosts: sheet.totalFixedCosts,
      restaurantProfit: sheet.restaurantProfit,
    };
    return [base, ...this.options().map((weekly) => breakEvenColumn(sheet, weekly ?? 0))];
  });

  protected readonly optionsChanged = computed(() => {
    const saved = this.sheet()?.scenarios ?? [];
    return this.options().join('|') !== saved.join('|');
  });

  // Weekly sales across, a year's money up; ticks at round numbers.
  protected readonly plot = computed(() => {
    const sheet = this.sheet();
    if (!sheet || sheet.breakEvenWeeklySales === null || sheet.breakEvenYearlySales === null) {
      return null;
    }
    const columns = this.columns();
    const xMax = BreakEvenPage.nice(Math.max(sheet.breakEvenWeeklySales, ...columns.map((c) => c.weeklySales)) * 1.2);
    const yTop = Math.max(xMax * 52, sheet.totalFixedCosts + xMax * 52 * sheet.costOfSalesRatio);
    const yMax = BreakEvenPage.nice(yTop * 1.05);
    const plotWidth = chart.width - chart.left - chart.right;
    const plotHeight = chart.height - chart.top - chart.bottom;
    const x = (weekly: number) => chart.left + (weekly / xMax) * plotWidth;
    const y = (yearly: number) => chart.top + plotHeight - (Math.max(0, Math.min(yearly, yMax)) / yMax) * plotHeight;
    const ticks = (max: number) => [0, 0.25, 0.5, 0.75, 1].map((f) => max * f);
    return {
      xMax,
      x,
      y,
      xTicks: ticks(xMax).map((value) => ({ value, x: x(value), label: BreakEvenPage.short(value) })),
      yTicks: ticks(yMax).map((value) => ({ value, y: y(value), label: BreakEvenPage.short(value) })),
      points: columns.map((c, i) => ({ label: i === 0 ? 'Now' : `#${i}`, weekly: c.weeklySales, profit: c.restaurantProfit })),
    };
  });

  constructor() {
    effect(() => {
      const from = this.from();
      const to = this.to();
      untracked(() => void this.load(from, to));
    });
  }

  protected pick(end: 'from' | 'to', month: string): void {
    if (!/^\d{4}-\d{2}$/.test(month)) {
      return;
    }
    // Keep the run the right way round.
    if (end === 'from') {
      this.from.set(month);
      if (month > this.to()) {
        this.to.set(month);
      }
    } else {
      this.to.set(month);
      if (month < this.from()) {
        this.from.set(month);
      }
    }
  }

  protected setOption(index: number, value: number | string | null): void {
    const weekly = value === '' || value === null ? 0 : Number(value);
    this.options.update((options) => options.map((o, i) => (i === index ? (Number.isFinite(weekly) && weekly >= 0 ? weekly : 0) : o)));
  }

  protected async saveOptions(): Promise<void> {
    this.saving.set(true);
    try {
      const saved = await firstValueFrom(this.api.saveBreakEvenScenarios(this.options()));
      this.sheet.update((sheet) => (sheet ? { ...sheet, scenarios: saved } : sheet));
      this.options.set([...saved]);
      this.notify.info('Options kept.');
    } catch (error) {
      this.notify.error(error, 'The options could not be kept.');
    } finally {
      this.saving.set(false);
    }
  }

  protected print(): void {
    window.print();
  }

  private async load(from: string, to: string): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const sheet = await firstValueFrom(this.api.breakEven(from, to));
      this.sheet.set(sheet);
      // Until the manager keeps their own: more, much more, less and much less than now.
      this.options.set(
        sheet.scenarios.length === 4 ? [...sheet.scenarios] : [1.2, 1.5, 0.75, 0.6].map((f) => BreakEvenPage.round(sheet.weeklySales * f)),
      );
    } catch (error) {
      this.sheet.set(null);
      this.error.set(errorMessage(error, 'The worksheet could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  // A round figure for an option: to 10 under 1,000, to 100 under 10,000, then to 1,000.
  private static round(value: number): number {
    const step = value < 1000 ? 10 : value < 10000 ? 100 : 1000;
    return Math.round(value / step) * step;
  }

  // The next round number above, for an axis that ends on one without much empty space: 2.73M
  // ends at 3M, not 5M.
  private static nice(value: number): number {
    if (value <= 0) {
      return 1;
    }
    const power = 10 ** Math.floor(Math.log10(value));
    const step = [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].find((s) => s * power >= value)!;
    return step * power;
  }

  private static short(value: number): string {
    return value >= 1_000_000 ? `${+(value / 1_000_000).toFixed(2)}M` : value >= 1000 ? `${+(value / 1000).toFixed(1)}k` : `${Math.round(value)}`;
  }

  private static monthOf(date: Date): string {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
  }
}
