import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { CostingApi, ExpenseVoucherReport, ExpenseVoucherRow } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { Approvals, pendingLabel } from './approvals';
import { Treasury, monthBounds } from './financials';

// Expense vouchers (إذن صرف مصروفات): cash paid out of the safe for an expense (a bill, wages,
// petty cash), each under its expense item, so it counts on the profit and loss under its
// category, as an invoice's expense line does. A month at a time; voided ones are kept, struck out.
@Component({
  selector: 'app-expense-vouchers-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      @if (report(); as r) {
        <span class="muted">{{ r.total | money }} paid out</span>
      }
      <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> New expense voucher</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      @if (waiting().length) {
        <ul class="waiting">
          @for (w of waiting(); track w.id) {
            <li>
              <span class="pending-badge">{{ pendingLabel(w).en }}<span class="ar">{{ pendingLabel(w).ar }}</span></span>
              {{ w.summary }} <span class="muted small">· asked by {{ w.requestedBy }}: no cash has moved</span>
            </li>
          }
        </ul>
      }
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Expense<span class="ar">المصروف</span></th>
              <th>Category<span class="ar">الفئة</span></th>
              <th class="num">Amount<span class="ar">المبلغ</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (v of r.vouchers; track v.id) {
              @let asked = approvals.pendingFor(v.id);
              <tr [class.voided]="!!v.voidedAtUtc" [class.pending-deletion]="!!asked">
                <td class="nowrap">{{ v.date | date: 'd MMM' }}</td>
                <td>
                  {{ v.expenseItem }}
                  @if (v.notes) {
                    <div class="muted small">{{ v.notes }}</div>
                  }
                  @if (v.voidedAtUtc) {
                    <div><span class="voided-badge">Voided<span class="ar">ملغى</span></span>
                      <span class="muted small"> by {{ v.voidedBy }}, {{ v.voidedAtUtc | date: 'd MMM' }}</span></div>
                  } @else if (asked) {
                    <div><span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span></div>
                  }
                </td>
                <td>{{ v.category ?? '—' }}</td>
                <td class="num">{{ v.amount | money }}</td>
                <td>{{ v.recordedBy || '—' }}@if (v.approvedBy) {<div class="muted small">approved by {{ v.approvedBy }}</div>}</td>
                <td class="num">
                  @if (!v.voidedAtUtc) {
                    @if (asked) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval">lock_clock</mat-icon>
                    } @else {
                      <button mat-icon-button class="danger" (click)="voidVoucher(v)" matTooltip="Void" [attr.aria-label]="'Void ' + v.expenseItem">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="muted">No expense vouchers this month.</td></tr>
            }
          </tbody>
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
    .waiting {
      margin: 0 0 12px;
      padding: 0;
      list-style: none;
      display: grid;
      gap: 4px;
    }
    tr.voided td {
      color: var(--mat-sys-on-surface-variant);
      text-decoration: line-through;
      text-decoration-color: color-mix(in srgb, currentColor 40%, transparent);
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
  `,
})
export class ExpenseVouchersPage {
  private readonly api = inject(CostingApi);
  private readonly treasury = inject(Treasury);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly report = signal<ExpenseVoucherReport | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);
  // Vouchers asked for, not paid yet.
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('Treasury').filter((r) => r.summary.startsWith('Expense voucher')));

  constructor() {
    void this.load();
    void this.approvals.refresh();
  }

  protected move(by: number): void {
    const next = new Date(this.year(), this.month() - 1 + by, 1);
    this.year.set(next.getFullYear());
    this.month.set(next.getMonth() + 1);
    void this.load();
  }

  protected async add(): Promise<void> {
    if (await this.treasury.enter('ExpenseVoucher')) {
      await this.load();
    }
  }

  protected async voidVoucher(voucher: ExpenseVoucherRow): Promise<void> {
    const day = new Date(voucher.date).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
    if ((await this.approvals.delete('Treasury', voucher.id, `the ${voucher.expenseItem} voucher of ${day}`)) === 'Applied') {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    const { from, to } = monthBounds(this.year(), this.month());
    try {
      this.report.set(await firstValueFrom(this.api.expenseVouchers(from, to)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The expense vouchers could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
