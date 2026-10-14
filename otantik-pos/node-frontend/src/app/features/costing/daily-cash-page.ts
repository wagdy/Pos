import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, DailyCashDay, DailyCashReport } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { monthBounds } from './financials';

// The daily cash (يومية الإيراد): each day's takings as they were spent, month by month. The gross
// entered for a day, less the invoices paid straight out of its takings, is what of it is in the
// safe. A day opens to its takings and invoices. Read only: the safe is as it is. Expense vouchers
// are paid from the safe, never from a day's takings, so they are not here.
@Component({
  selector: 'app-daily-cash-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      @if (!isThisMonth()) {
        <button mat-button (click)="thisMonth()">This month</button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      <div class="tiles">
        <div class="tile key">
          <span class="label">Gross takings · إجمالي الإيراد</span>
          <strong>{{ r.gross | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Total deductions · إجمالي المنصرف</span>
          <strong class="out">{{ r.deductions | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Net to safe · الصافي للخزنة</span>
          <strong class="in">{{ r.netToSafe | money }}</strong>
        </div>
      </div>
      <p class="note">
        Each day's takings as entered, gross, less the invoices paid straight out of them: the rest is in the safe. Open a day for its
        invoices. Expense vouchers are paid from the safe, so they are not here; voided entries are left out.
        <span class="inline-ar">إجمالي الإيراد ناقص ما صرف منه للفواتير = الصافي للخزنة.</span>
      </p>

      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th class="toggle"><span class="visually-hidden">Open</span></th>
              <th>Date<span class="ar">تاريخ اليومية</span></th>
              <th class="num">Gross takings<span class="ar">إجمالي الإيراد المسجل</span></th>
              <th class="num">Total deductions<span class="ar">إجمالي المنصرف</span></th>
              <th class="num">Net to safe<span class="ar">الصافي للخزنة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (d of r.days; track d.date) {
              <tr class="clickable day" [class.open]="isOpen(d)" (click)="toggle(d)">
                <td class="toggle">
                  <button
                    mat-icon-button
                    [attr.aria-expanded]="isOpen(d)"
                    [attr.aria-label]="(isOpen(d) ? 'Close ' : 'Open ') + (d.date | date: 'd MMM')"
                    (click)="$event.stopPropagation(); toggle(d)"
                  >
                    <mat-icon>{{ isOpen(d) ? 'expand_more' : 'chevron_right' }}</mat-icon>
                  </button>
                </td>
                <td class="nowrap">{{ d.date | date: 'EEE d MMM' }}</td>
                <td class="num strong">{{ d.gross | money }}</td>
                <td class="num">{{ d.deductions ? (d.deductions | money) : '—' }}</td>
                <td class="num">{{ d.netToSafe ? (d.netToSafe | money) : '—' }}</td>
              </tr>
              @if (isOpen(d)) {
                <tr class="details">
                  <td colspan="5">
                    <h4>Takings entered <span class="inline-ar">الإيراد المسجل</span></h4>
                    @if (d.entered.length) {
                      <table class="lines">
                        <tbody>
                          @for (l of d.entered; track l.id) {
                            <tr class="entered">
                              <td class="when muted">{{ l.date | date: 'd MMM' }}</td>
                              <td>{{ l.description }}</td>
                              <td class="num">{{ l.amount | money }}</td>
                              <td class="by">{{ l.recordedBy }}</td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    } @else {
                      <p class="muted small">Not entered yet.</p>
                    }
                    <h4>Paid out of these takings <span class="inline-ar">المنصرف من الإيراد</span></h4>
                    @if (d.paid.length) {
                      <table class="lines">
                        <tbody>
                          @for (l of d.paid; track l.id) {
                            <tr class="paid">
                              <td class="when muted">{{ l.date | date: 'd MMM' }}</td>
                              <td>
                                @if (l.purchaseInvoiceId) {
                                  <a [routerLink]="['/costing/purchases/invoices', l.purchaseInvoiceId]">{{ l.description }}</a>
                                } @else {
                                  {{ l.description }}
                                }
                                @if (l.items.length) {
                                  <div class="muted small">{{ l.items.join(', ') }}</div>
                                }
                              </td>
                              <td class="num">{{ l.amount | money }}</td>
                              <td class="by">
                                {{ l.recordedBy }}
                                @if (l.approvedBy) {
                                  <span class="muted small">approved by {{ l.approvedBy }}</span>
                                }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    } @else {
                      <p class="muted small">Nothing was paid out of these takings.</p>
                    }
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="5" class="muted">No takings entered or paid out this month.</td></tr>
            }
          </tbody>
          @if (r.days.length) {
            <tfoot>
              <tr>
                <td></td>
                <td>The month</td>
                <td class="num">{{ r.gross | money }}</td>
                <td class="num">{{ r.deductions | money }}</td>
                <td class="num">{{ r.netToSafe | money }}</td>
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
    .in {
      color: var(--till-ok);
    }
    .out {
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
    table.grid th.toggle,
    table.grid td.toggle {
      width: 48px;
      padding: 0 0 0 4px;
    }
    tr.day.open td {
      background: var(--mat-sys-surface-container-low);
      border-bottom-color: transparent;
    }
    tr.details > td {
      padding: 4px 16px 16px 56px;
      background: var(--mat-sys-surface-container-low);
    }
    h4 {
      margin: 8px 0 4px;
      font: var(--mat-sys-title-small);
    }
    // The invoices and the counts line up: the same widths in both.
    table.lines {
      width: 100%;
      border-collapse: collapse;
      table-layout: fixed;
    }
    table.lines td {
      padding: 6px 8px;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      vertical-align: top;
    }
    table.lines tr:last-child td {
      border-bottom: none;
    }
    table.lines td.when {
      width: 64px;
      white-space: nowrap;
    }
    table.lines td.num {
      width: 120px;
    }
    table.lines td.by {
      width: 170px;
    }
    table.lines td.by .small {
      display: block;
    }
    table.lines a {
      color: var(--mat-sys-primary);
      font-weight: 500;
      text-decoration: none;
    }
    table.lines a:hover,
    table.lines a:focus-visible {
      text-decoration: underline;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
    @media (max-width: 599px) {
      tr.details > td {
        padding-inline-start: 12px;
      }
      table.lines td.when {
        width: 48px;
      }
      table.lines td.num {
        width: 96px;
      }
      table.lines td.by {
        display: none;
      }
    }
  `,
})
export class DailyCashPage {
  private readonly api = inject(CostingApi);

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly report = signal<DailyCashReport | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  private readonly open = signal<ReadonlySet<string>>(new Set());

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);

  constructor() {
    void this.load();
  }

  protected isOpen(day: DailyCashDay): boolean {
    return this.open().has(day.date);
  }

  protected toggle(day: DailyCashDay): void {
    const open = new Set(this.open());
    if (!open.delete(day.date)) open.add(day.date);
    this.open.set(open);
  }

  protected move(by: number): void {
    const next = new Date(this.year(), this.month() - 1 + by, 1);
    this.year.set(next.getFullYear());
    this.month.set(next.getMonth() + 1);
    void this.load();
  }

  protected thisMonth(): void {
    this.year.set(new Date().getFullYear());
    this.month.set(new Date().getMonth() + 1);
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    this.open.set(new Set());
    const { from, to } = monthBounds(this.year(), this.month());
    try {
      this.report.set(await firstValueFrom(this.api.dailyCash(from, to)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The daily cash could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
