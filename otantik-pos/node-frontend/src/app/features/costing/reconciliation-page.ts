import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CostingApi, ReconciliationReport } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { monthBounds } from './financials';

// The reconciliation (مطابقة الإيرادات): each business day's cash before tax against the cash its
// sales should have brought in. A is the takings entered for the day, with what was paid straight
// out of them, less the tax in them. B is every item sold at its price, from costing's sales; less
// what did not come in as cash and plus delivery fees, all before tax, it is the cash expected.
// The difference shows a day short (items sold, cash missing) or over (cash for items not rung up).
@Component({
  selector: 'app-reconciliation-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MoneyPipe, NgTemplateOutlet],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      <div class="tiles">
        <div class="tile key">
          <span class="label">Cash before tax (A) · صافي النقدية</span>
          <strong>{{ r.total.netCash | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Items sold (B) · مبيعات الأصناف</span>
          <strong>{{ r.total.itemSales | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Cash expected · النقدية المتوقعة</span>
          <strong>{{ r.total.expectedCash | money }}</strong>
        </div>
        <div class="tile difference" [class.short]="r.total.difference < 0" [class.over]="r.total.difference > 0">
          <span class="label">Difference · الفرق</span>
          <strong>{{ r.total.difference | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Tax collected · الضريبة المحصلة</span>
          <strong>{{ r.total.tax | money }}</strong>
        </div>
      </div>
      <p class="note">
        A: the cash entered as each day's takings, with what was paid straight out of them, less the tax in them{{ rateNote(r) }}. B: every
        item sold at its menu price, before tax, at the till and online. Less discounts, points, refunds and what was paid by
        card or online, plus delivery fees, it is the cash the day should have brought in. Below zero, cash is missing for items sold;
        above it, cash came in for items not rung up.
        <span class="inline-ar">الفرق بالسالب عجز في النقدية، وبالموجب نقدية لأصناف لم تسجل.</span>
      </p>

      <ng-template #cells let-d>
        <td class="num">{{ d.takingsEntered | money }}</td>
        <td class="num">{{ d.paidOut ? (d.paidOut | money) : '—' }}</td>
        <td class="num">{{ d.tax ? (d.tax | money) : '—' }}</td>
        <td class="num strong">{{ d.netCash | money }}</td>
        <td class="num strong">{{ d.itemSales | money }}</td>
        <td class="num">{{ d.discounts + d.points ? (d.discounts + d.points | money) : '—' }}</td>
        <td class="num">{{ d.deliveryFees ? (d.deliveryFees | money) : '—' }}</td>
        <td class="num">{{ d.refunds ? (d.refunds | money) : '—' }}</td>
        <td class="num">{{ d.cardAndOnline ? (d.cardAndOnline | money) : '—' }}</td>
        <td class="num strong">{{ d.expectedCash | money }}</td>
        <td class="num strong" [class.negative]="d.difference < 0" [class.over]="d.difference > 0">
          {{ d.difference | money }}
          @if (d.difference < 0) {
            <div class="small">short · عجز</div>
          } @else if (d.difference > 0) {
            <div class="small">not rung up · زيادة</div>
          }
        </td>
      </ng-template>

      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Day<span class="ar">اليوم</span></th>
              <th class="num">Cash entered<span class="ar">المورد</span></th>
              <th class="num">Paid out of it<span class="ar">مدفوع منه</span></th>
              <th class="num">Tax in it<span class="ar">الضريبة</span></th>
              <th class="num">Cash before tax (A)<span class="ar">صافي النقدية</span></th>
              <th class="num">Items sold (B)<span class="ar">مبيعات الأصناف</span></th>
              <th class="num">Less discounts, points<span class="ar">الخصومات والنقاط</span></th>
              <th class="num">Plus delivery fees<span class="ar">رسوم التوصيل</span></th>
              <th class="num">Less refunds<span class="ar">المرتجعات</span></th>
              <th class="num">Less card, online<span class="ar">بطاقة وأونلاين</span></th>
              <th class="num">Cash expected<span class="ar">النقدية المتوقعة</span></th>
              <th class="num">Difference<span class="ar">الفرق</span></th>
            </tr>
          </thead>
          <tbody>
            @for (d of r.days; track d.date) {
              <tr>
                <td class="nowrap">{{ d.date | date: 'EEE d MMM' }}</td>
                <ng-container *ngTemplateOutlet="cells; context: { $implicit: d }" />
              </tr>
            } @empty {
              <tr><td colspan="12" class="muted">Nothing sold or entered this month.</td></tr>
            }
          </tbody>
          @if (r.days.length) {
            <tfoot>
              <tr>
                <td>The month</td>
                <ng-container *ngTemplateOutlet="cells; context: { $implicit: r.total }" />
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
    .negative,
    .tile.short strong {
      color: var(--mat-sys-error);
    }
    .over,
    .tile.over strong {
      color: var(--till-warn);
    }
    .strong {
      font-weight: 600;
    }
    .small {
      font-size: 0.8em;
      font-weight: 400;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class ReconciliationPage {
  private readonly api = inject(CostingApi);

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly report = signal<ReconciliationReport | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);

  constructor() {
    void this.load();
  }

  // " (at the till's 14%)", once the till has a rate.
  protected rateNote(r: ReconciliationReport): string {
    return r.taxPercent === null ? '' : ` (at the till's ${r.taxPercent}%)`;
  }

  protected move(by: number): void {
    const next = new Date(this.year(), this.month() - 1 + by, 1);
    this.year.set(next.getFullYear());
    this.month.set(next.getMonth() + 1);
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    const { from, to } = monthBounds(this.year(), this.month());
    try {
      this.report.set(await firstValueFrom(this.api.reconciliation(from, to)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The reconciliation could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
