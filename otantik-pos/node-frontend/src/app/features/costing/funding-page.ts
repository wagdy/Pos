import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CostingApi, DayRevenue, DayRevenueReport, FundingReport, safeKindLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { Treasury, entryTax, monthBounds } from './financials';

// Funding sources (مصادر التمويل): every sum put into the safe in a month, by where it came from:
// carried over, the owners' financing, another source, and the days' cash takings. Below, each
// day's cash: what the till and cash-on-delivery took, the takings entered for it (gross), what was
// paid straight out of them, what of them went into the safe, and the till's cash not entered. A
// day's takings are entered from its row.
@Component({
  selector: 'app-funding-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      <button mat-stroked-button (click)="enter('Funding')"><mat-icon>savings</mat-icon> Add funding</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (funding(); as f) {
      <div class="tiles">
        <div class="tile key">
          <span class="label">Put into the safe · إجمالي الوارد</span>
          <strong>{{ f.total | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Days' takings · توريد الإيرادات</span>
          <strong>{{ f.revenue | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Owners' financing · جاري الشركاء</span>
          <strong>{{ f.ownerFinancing | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Carried over · رصيد مرحل</span>
          <strong>{{ f.carriedOver | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Other · مصادر أخرى</span>
          <strong>{{ f.other | money }}</strong>
        </div>
      </div>

      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Source<span class="ar">المصدر</span></th>
              <th class="num">Amount<span class="ar">المبلغ</span></th>
              <th>By<span class="ar">بواسطة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (e of f.entries; track e.id) {
              <tr [class.voided]="!!e.voidedAtUtc || !!e.reversalOf">
                <td class="nowrap">{{ e.date | date: 'd MMM' }}</td>
                <td>
                  <span class="kind">{{ kinds[e.kind].en }}</span> {{ e.description }}
                  @if (entryTax(e); as t) {
                    <div class="muted small">Tax in it at {{ t.percent }}%: {{ t.tax | money }} · {{ t.net | money }} before tax</div>
                  }
                  @if (e.notes && !e.reversalOf) {
                    <div class="muted small">{{ e.notes }}</div>
                  }
                  @if (e.voidedAtUtc) {
                    <span class="voided-badge">Voided<span class="ar">ملغى</span></span>
                  }
                </td>
                <td class="num" [class.negative]="e.out > 0">{{ (e.in || -e.out) | money }}</td>
                <td>{{ e.recordedBy || '—' }}@if (e.approvedBy) {<div class="muted small">approved by {{ e.approvedBy }}</div>}</td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="muted">Nothing put into the safe this month.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }

    <h3>Each day's cash <span class="inline-ar">إيرادات الأيام النقدية</span></h3>
    <p class="note">
      Cash sales at the till and cash on delivery, by business day, from the treasury's opening balance on (the cash of the days before is in
      it). The takings entered are the day's gross; what was paid out of them stays out of the safe. Left is the till's cash not entered as
      takings; below zero, more was entered than the till took.
      <span class="inline-ar">المتبقي: نقدية لم تسجل كإيراد بعد.</span>
    </p>
    @if (revenue(); as r) {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Day<span class="ar">اليوم</span></th>
              <th class="num">Cash taken<span class="ar">المبيعات النقدية</span></th>
              <th class="num">Takings entered<span class="ar">الإيراد المسجل</span></th>
              <th class="num">Paid out of it<span class="ar">مدفوع منه</span></th>
              <th class="num">Into the safe<span class="ar">مورد للخزنة</span></th>
              <th class="num">Left<span class="ar">المتبقي</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (d of r.days; track d.date) {
              <tr>
                <td class="nowrap">{{ d.date | date: 'EEE d MMM' }}</td>
                <td class="num">{{ d.cashSales | money }}</td>
                <td class="num">{{ d.entered ? (d.entered | money) : '—' }}</td>
                <td class="num">{{ d.paidOut ? (d.paidOut | money) : '—' }}</td>
                <td class="num">{{ d.transferred ? (d.transferred | money) : '—' }}</td>
                <td class="num strong" [class.negative]="d.left < 0">{{ d.left | money }}</td>
                <td class="num">
                  @if (!d.entered && d.cashSales > 0) {
                    <button mat-stroked-button (click)="moveIn(d)"><mat-icon>move_to_inbox</mat-icon> Enter takings</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="muted">No cash taken this month.</td></tr>
            }
          </tbody>
          @if (r.days.length) {
            <tfoot>
              <tr>
                <td>The month</td>
                <td class="num">{{ r.cashSales | money }}</td>
                <td class="num">{{ r.entered | money }}</td>
                <td class="num">{{ r.paidOut | money }}</td>
                <td class="num">{{ r.transferred | money }}</td>
                <td class="num strong" [class.negative]="r.left < 0">{{ r.left | money }}</td>
                <td></td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .month {
      min-width: 140px;
      text-align: center;
      font: var(--mat-sys-title-medium);
    }
    h3 {
      margin: 24px 0 8px;
      font: var(--mat-sys-title-medium);
    }
    tr.voided td {
      color: var(--mat-sys-on-surface-variant);
    }
    .negative {
      color: var(--mat-sys-error);
    }
    .strong {
      font-weight: 600;
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class FundingPage {
  private readonly api = inject(CostingApi);
  private readonly treasury = inject(Treasury);
  protected readonly kinds = safeKindLabels;
  protected readonly entryTax = entryTax;

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly funding = signal<FundingReport | null>(null);
  protected readonly revenue = signal<DayRevenueReport | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);

  constructor() {
    void this.load();
  }

  protected move(by: number): void {
    const next = new Date(this.year(), this.month() - 1 + by, 1);
    this.year.set(next.getFullYear());
    this.month.set(next.getMonth() + 1);
    void this.load();
  }

  protected async enter(kind: 'Funding'): Promise<void> {
    if (await this.treasury.enter(kind)) {
      await this.load();
    }
  }

  // A day's takings entered: its gross, what the till took, unless the POS says otherwise. The
  // till's bills charge tax, so its cash has the tax in it.
  protected async moveIn(day: DayRevenue): Promise<void> {
    if (await this.treasury.enter('RevenueTransfer', { revenueDate: day.date, amount: day.cashSales, includesTax: true })) {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    const { from, to } = monthBounds(this.year(), this.month());
    try {
      const [funding, revenue] = await Promise.all([firstValueFrom(this.api.funding(from, to)), firstValueFrom(this.api.dayRevenue(from, to))]);
      this.funding.set(funding);
      this.revenue.set(revenue);
    } catch (error) {
      this.error.set(errorMessage(error, 'The funding could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
