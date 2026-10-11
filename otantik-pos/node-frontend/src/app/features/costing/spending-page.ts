import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CategorySpending, CostingApi, SpendingReport, expenseLineLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

type Period = 'month' | 'last-month' | 'quarter' | 'year' | 'custom';

// Purchases by category (المشتريات حسب الفئة) over any period: how the money went, stock and
// expenses, from the posted invoices, each on its own date. A bar per category, split into what
// went into stock and what was expenses, largest first; the table below has the figures and, opened,
// what was bought under each.
@Component({
  selector: 'app-spending-page',
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
      <mat-button-toggle-group [value]="period()" (change)="choose($event.value)" aria-label="Period" class="periods">
        <mat-button-toggle value="month">This month</mat-button-toggle>
        <mat-button-toggle value="last-month">Last month</mat-button-toggle>
        <mat-button-toggle value="quarter">3 months</mat-button-toggle>
        <mat-button-toggle value="year">This year</mat-button-toggle>
      </mat-button-toggle-group>
      <mat-form-field subscriptSizing="dynamic" class="day">
        <mat-label>From · من</mat-label>
        <input matInput type="date" [ngModel]="from()" (ngModelChange)="custom('from', $event)" [max]="to()" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="day">
        <mat-label>To · إلى</mat-label>
        <input matInput type="date" [ngModel]="to()" (ngModelChange)="custom('to', $event)" [min]="from()" />
      </mat-form-field>
      <span class="spacer"></span>
      <button mat-button (click)="exportCsv()" [disabled]="!report()?.categories?.length"><mat-icon>download</mat-icon> CSV</button>
      <button mat-button (click)="print()" [disabled]="!report()"><mat-icon>print</mat-icon> Print</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      <header class="report-head">
        <h2>Purchases by category <span class="inline-ar">المشتريات حسب الفئة</span></h2>
        <div class="muted">{{ r.from | date: 'd MMM y' }} – {{ r.to | date: 'd MMM y' }} · posted invoices, each on its own date</div>
      </header>
      <div class="tiles">
        <div class="tile">
          <span class="label">Total spent · إجمالي المشتريات</span>
          <strong>{{ r.total | money }}</strong>
        </div>
        <div class="tile">
          <span class="label"><span class="swatch stock"></span>Into stock · خامات</span>
          <strong>{{ r.inventory | money }}</strong>
          <small class="muted">cost of sales as the recipes use it</small>
        </div>
        <div class="tile">
          <span class="label"><span class="swatch expense"></span>Expenses · مصروفات</span>
          <strong>{{ r.expenses | money }}</strong>
          <small class="muted">overheads in the KPIs' profit and loss</small>
        </div>
        <div class="tile">
          <span class="label">Invoices · الفواتير</span>
          <strong>{{ r.invoices }}</strong>
        </div>
      </div>

      @if (r.categories.length) {
        <section class="chart" aria-label="Spending by category, stock and expenses">
          <div class="legend" aria-hidden="true">
            <span><span class="swatch stock"></span>Into stock</span>
            <span><span class="swatch expense"></span>Expenses</span>
          </div>
          <div class="bars">
            @for (c of r.categories; track c.categoryId) {
              <div
                class="bar-row"
                tabindex="0"
                [attr.aria-label]="describe(c)"
                (mouseenter)="hovered.set(c.categoryId)"
                (mouseleave)="hovered.set(null)"
                (focus)="hovered.set(c.categoryId)"
                (blur)="hovered.set(null)"
              >
                <span class="name">
                  {{ c.category }}
                  @if (c.categoryAr) {
                    <span class="ar">{{ c.categoryAr }}</span>
                  }
                </span>
                <span class="track">
                  @if (c.inventory > 0) {
                    <span class="segment stock" [style.width.%]="width(c.inventory)"></span>
                  }
                  @if (c.expenses > 0) {
                    <span class="segment expense" [style.width.%]="width(c.expenses)"></span>
                  }
                </span>
                <span class="value">
                  {{ c.total | money }}
                  <small class="muted">{{ c.sharePercent === null ? '' : (c.sharePercent | number: '1.1-1') + '%' }}</small>
                </span>
                @if (hovered() === c.categoryId) {
                  <span class="tip" role="tooltip">
                    <strong>{{ c.category }}</strong>
                    <span class="tip-row"><span class="swatch stock"></span>Into stock <b>{{ c.inventory | money }}</b></span>
                    <span class="tip-row"><span class="swatch expense"></span>Expenses <b>{{ c.expenses | money }}</b></span>
                    <span class="tip-row">Share of all spending <b>{{ c.sharePercent | number: '1.1-1' }}%</b></span>
                  </span>
                }
              </div>
            }
          </div>
        </section>

        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Category<span class="ar">الفئة</span></th>
                <th>Expenses count as<span class="ar">تُحتسب المصروفات ضمن</span></th>
                <th class="num">Into stock<span class="ar">خامات</span></th>
                <th class="num">Expenses<span class="ar">مصروفات</span></th>
                <th class="num">Total<span class="ar">الإجمالي</span></th>
                <th class="num">Share<span class="ar">النسبة</span></th>
              </tr>
            </thead>
            <tbody>
              @for (c of r.categories; track c.categoryId) {
                <tr class="clickable" (click)="toggle(c.categoryId)" [attr.aria-expanded]="open().has(c.categoryId)">
                  <td>
                    <mat-icon class="chevron">{{ open().has(c.categoryId) ? 'expand_more' : 'chevron_right' }}</mat-icon>
                    {{ c.category }}
                  </td>
                  <td class="muted">{{ c.expenses > 0 ? lines[c.expenseLine].en : '—' }}</td>
                  <td class="num">{{ c.inventory | money }}</td>
                  <td class="num">{{ c.expenses | money }}</td>
                  <td class="num"><strong>{{ c.total | money }}</strong></td>
                  <td class="num">{{ c.sharePercent === null ? '—' : (c.sharePercent | number: '1.1-1') + '%' }}</td>
                </tr>
                @if (open().has(c.categoryId)) {
                  @for (i of c.items; track i.itemType + i.itemId) {
                    <tr class="item">
                      <td>
                        {{ i.name }}
                        @if (i.itemType === 'Expense') {
                          <span class="badge expense">Expense · مصروف</span>
                        }
                      </td>
                      <td class="muted">{{ i.quantity | number: '1.0-3' }} {{ i.unit }} · {{ i.lines }} {{ i.lines === 1 ? 'line' : 'lines' }}</td>
                      <td class="num">{{ i.itemType === 'Inventory' ? (i.total | money) : '' }}</td>
                      <td class="num">{{ i.itemType === 'Expense' ? (i.total | money) : '' }}</td>
                      <td class="num">{{ i.total | money }}</td>
                      <td></td>
                    </tr>
                  }
                }
              }
            </tbody>
            <tfoot>
              <tr>
                <td colspan="2">Total · الإجمالي</td>
                <td class="num">{{ r.inventory | money }}</td>
                <td class="num">{{ r.expenses | money }}</td>
                <td class="num">{{ r.total | money }}</td>
                <td class="num">100%</td>
              </tr>
            </tfoot>
          </table>
        </div>
      } @else {
        <p class="note">No invoices were posted for these days.</p>
      }
      @if (r.unpricedLines) {
        <p class="note">
          {{ r.unpricedLines }} {{ r.unpricedLines === 1 ? 'line was' : 'lines were' }} received with no price: in stock, but not in these
          figures.
        </p>
      }
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    :host {
      /* The reference palette's first two categorical slots, validated together on the light surface. */
      --series-stock: #2a78d6;
      --series-expense: #eb6834;
    }
    .periods {
      flex-wrap: wrap;
    }
    .day {
      width: 160px;
    }
    .report-head {
      margin-bottom: 12px;

      h2 {
        margin: 0 0 4px;
        font: var(--mat-sys-title-large);
      }
    }
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
      gap: 8px;
      margin-bottom: 16px;
    }
    .tile {
      display: grid;
      gap: 2px;
      padding: 12px 14px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      .label {
        display: flex;
        align-items: center;
        gap: 6px;
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      strong {
        font: var(--mat-sys-headline-small);
      }
    }
    .swatch {
      display: inline-block;
      width: 10px;
      height: 10px;
      border-radius: 3px;
      flex: none;
      print-color-adjust: exact;
      -webkit-print-color-adjust: exact;

      &.stock {
        background: var(--series-stock);
      }
      &.expense {
        background: var(--series-expense);
      }
    }
    .chart {
      padding: 12px 14px;
      margin-bottom: 16px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);
    }
    .legend {
      display: flex;
      gap: 16px;
      margin-bottom: 10px;
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);

      > span {
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
    }
    .bars {
      display: grid;
      gap: 4px;
    }
    .bar-row {
      position: relative;
      display: grid;
      grid-template-columns: minmax(90px, 180px) 1fr auto;
      align-items: center;
      gap: 10px;
      padding: 4px 6px;
      border-radius: 8px;
      outline: none;

      &:hover,
      &:focus-visible {
        background: var(--mat-sys-surface-container-low);
      }
      &:focus-visible {
        box-shadow: inset 0 0 0 2px var(--mat-sys-primary);
      }
    }
    .name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;

      .ar {
        display: inline;
        margin-left: 6px;
      }
    }
    .track {
      display: flex;
      gap: 2px;
      height: 14px;
      min-width: 0;
    }
    .segment {
      height: 100%;
      min-width: 3px;
      print-color-adjust: exact;
      -webkit-print-color-adjust: exact;

      &.stock {
        background: var(--series-stock);
      }
      &.expense {
        background: var(--series-expense);
      }
      &:last-child {
        border-radius: 0 4px 4px 0;
      }
    }
    .value {
      min-width: 120px;
      text-align: end;
      white-space: nowrap;
      font-variant-numeric: tabular-nums;

      small {
        margin-left: 6px;
      }
    }
    .tip {
      position: absolute;
      z-index: 3;
      top: calc(100% - 2px);
      left: min(40%, 220px);
      display: grid;
      gap: 2px;
      min-width: 200px;
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--mat-sys-inverse-surface);
      color: var(--mat-sys-inverse-on-surface);
      font: var(--mat-sys-body-small);
      box-shadow: var(--mat-sys-level2);
      pointer-events: none;
    }
    .tip-row {
      display: flex;
      align-items: center;
      gap: 6px;

      b {
        margin-left: auto;
        padding-left: 12px;
      }
    }
    .chevron {
      vertical-align: middle;
      margin-right: 2px;
      color: var(--mat-sys-on-surface-variant);
    }
    tr.item td {
      background: var(--mat-sys-surface-container-lowest);
      font-size: 0.92em;
    }
    tr.item td:first-child {
      padding-left: 38px;
    }
    .badge.expense {
      background: color-mix(in srgb, var(--till-warn) 16%, transparent);
      color: var(--till-warn);
    }
    @media (max-width: 600px) {
      .bar-row {
        grid-template-columns: 1fr auto;
      }
      .track {
        grid-column: 1 / -1;
        grid-row: 2;
      }
    }
  `,
})
export class SpendingPage {
  private readonly api = inject(CostingApi);

  protected readonly lines = expenseLineLabels;
  protected readonly period = signal<Period>('month');
  protected readonly from = signal(SpendingPage.iso(SpendingPage.firstOfMonth(new Date())));
  protected readonly to = signal(SpendingPage.iso(new Date()));
  protected readonly report = signal<SpendingReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly hovered = signal<string | null>(null);
  protected readonly open = signal(new Set<string>());

  // The bars are drawn against the largest category, which fills the track.
  private readonly largest = computed(() => Math.max(0, ...(this.report()?.categories ?? []).map((c) => c.total)));

  constructor() {
    effect(() => {
      const from = this.from();
      const to = this.to();
      untracked(() => void this.load(from, to));
    });
  }

  protected choose(period: Period): void {
    const today = new Date();
    const first = SpendingPage.firstOfMonth(today);
    const [from, to] =
      period === 'last-month'
        ? [new Date(first.getFullYear(), first.getMonth() - 1, 1), new Date(first.getFullYear(), first.getMonth(), 0)]
        : period === 'quarter'
          ? [new Date(first.getFullYear(), first.getMonth() - 2, 1), today]
          : period === 'year'
            ? [new Date(today.getFullYear(), 0, 1), today]
            : [first, today];
    this.period.set(period);
    this.from.set(SpendingPage.iso(from));
    this.to.set(SpendingPage.iso(to));
  }

  // Days typed in: the period is whatever they say. Cleared or half typed, it stays as it was.
  protected custom(end: 'from' | 'to', value: string): void {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value ?? '')) {
      return;
    }
    this.period.set('custom');
    (end === 'from' ? this.from : this.to).set(value);
  }

  protected width(amount: number): number {
    const largest = this.largest();
    return largest > 0 ? (amount / largest) * 100 : 0;
  }

  protected describe(c: CategorySpending): string {
    return `${c.category}: ${c.total.toFixed(2)} L.E, ${c.inventory.toFixed(2)} into stock and ${c.expenses.toFixed(2)} expenses, ${c.sharePercent ?? 0}% of all spending`;
  }

  protected toggle(categoryId: string): void {
    this.open.update((open) => {
      const next = new Set(open);
      if (!next.delete(categoryId)) {
        next.add(categoryId);
      }
      return next;
    });
  }

  protected print(): void {
    window.print();
  }

  // For a spreadsheet: a byte order mark so Excel reads the Arabic names as UTF-8. A row per
  // category, then each item under it.
  protected exportCsv(): void {
    const r = this.report();
    if (!r) {
      return;
    }
    const rows: (string | number)[][] = [
      ['Category · الفئة', 'Item · الصنف', 'Type', 'Quantity', 'Unit', 'Into Stock', 'Expenses', 'Total', 'Share %'],
    ];
    for (const c of r.categories) {
      rows.push([c.category, '', '', '', '', c.inventory.toFixed(2), c.expenses.toFixed(2), c.total.toFixed(2), c.sharePercent?.toFixed(1) ?? '']);
      for (const i of c.items) {
        rows.push([
          c.category,
          i.name,
          i.itemType === 'Expense' ? 'Expense' : 'Stock',
          i.quantity,
          i.unit,
          i.itemType === 'Inventory' ? i.total.toFixed(2) : '',
          i.itemType === 'Expense' ? i.total.toFixed(2) : '',
          i.total.toFixed(2),
          '',
        ]);
      }
    }
    rows.push(['Total', '', '', '', '', r.inventory.toFixed(2), r.expenses.toFixed(2), r.total.toFixed(2), '100.0']);
    const csv = rows.map((cells) => cells.map(SpendingPage.cell).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `purchases-by-category-${r.from}-to-${r.to}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  private async load(from: string, to: string): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const report = await firstValueFrom(this.api.spending(from, to));
      // Moved on while it loaded: what came back is for other days.
      if (this.from() === from && this.to() === to) {
        this.report.set(report);
      }
    } catch (error) {
      this.report.set(null);
      this.error.set(errorMessage(error, 'The spending could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private static cell(value: string | number): string {
    const text = String(value);
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  }

  private static firstOfMonth(date: Date): Date {
    return new Date(date.getFullYear(), date.getMonth(), 1);
  }

  private static iso(date: Date): string {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
  }
}
