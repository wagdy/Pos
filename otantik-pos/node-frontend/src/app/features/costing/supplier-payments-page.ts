import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, SupplierPaymentRow } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { Approvals, pendingLabel } from './approvals';
import { Treasury, monthBounds } from './financials';

// Supplier payment vouchers (أذونات صرف الموردين): cash paid from the safe to a supplier, each
// taken off what they are owed, a month at a time. A payment asked for by someone who may not
// approve is listed as waiting: no cash has moved and the debt is as it was.
@Component({
  selector: 'app-supplier-payments-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      <span class="muted">{{ total() | money }} paid</span>
      <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> New payment voucher</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
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
              <th>Supplier<span class="ar">المورد</span></th>
              <th class="num">Amount<span class="ar">المبلغ</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of payments(); track p.id) {
              @let asked = approvals.pendingFor(p.id);
              <tr [class.voided]="!!p.voidedAtUtc" [class.pending-deletion]="!!asked">
                <td class="nowrap">{{ p.date | date: 'd MMM' }}</td>
                <td>
                  <a [routerLink]="['../suppliers']" [queryParams]="{ supplier: p.supplierId }">{{ p.supplier }}</a>
                  @if (p.notes) {
                    <div class="muted small">{{ p.notes }}</div>
                  }
                  @if (p.voidedAtUtc) {
                    <div><span class="voided-badge">Voided<span class="ar">ملغى</span></span>
                      <span class="muted small"> by {{ p.voidedBy }}, {{ p.voidedAtUtc | date: 'd MMM' }}</span></div>
                  } @else if (asked) {
                    <div><span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span></div>
                  }
                </td>
                <td class="num">{{ p.amount | money }}</td>
                <td>{{ p.recordedBy || '—' }}@if (p.approvedBy) {<div class="muted small">approved by {{ p.approvedBy }}</div>}</td>
                <td class="num">
                  @if (!p.voidedAtUtc) {
                    @if (asked) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval">lock_clock</mat-icon>
                    } @else {
                      <button mat-icon-button class="danger" (click)="voidPayment(p)" matTooltip="Void" [attr.aria-label]="'Void the payment to ' + p.supplier">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="muted">No payments to suppliers this month.</td></tr>
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
export class SupplierPaymentsPage {
  private readonly api = inject(CostingApi);
  private readonly treasury = inject(Treasury);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly payments = signal<SupplierPaymentRow[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);
  protected readonly total = computed(() => this.payments().filter((p) => !p.voidedAtUtc).reduce((sum, p) => sum + p.amount, 0));
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('Treasury').filter((r) => r.summary.startsWith('Supplier payment')));

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
    if (await this.treasury.enter('SupplierPayment')) {
      await this.load();
    }
  }

  protected async voidPayment(payment: SupplierPaymentRow): Promise<void> {
    const day = new Date(payment.date).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
    if ((await this.approvals.delete('Treasury', payment.id, `the payment to ${payment.supplier} of ${day}`)) === 'Applied') {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    const { from, to } = monthBounds(this.year(), this.month());
    try {
      this.payments.set(await firstValueFrom(this.api.supplierPayments(from, to)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The payments could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
