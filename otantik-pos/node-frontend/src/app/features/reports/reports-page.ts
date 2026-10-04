import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DayReport, Order, OrderType, PaymentMethod } from '../../core/api/models';
import { errorMessage } from '../../core/http/error-message';
import { isSettled, orderLabel, statusText } from '../../core/orders/order-rules';
import { OrdersStore } from '../../core/orders/orders.store';
import { ReportsApi } from '../../core/reports/reports.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

// One business day at this till: what was taken, refunded and written off, by payment method and
// by kind of order, and every order of the day, each one a tap away for a reprint or a refund.
//
// From the till server's own database, so it works with the internet down. Today's report keeps
// itself current: any change to an order, at this till or another, brings a fresh copy.
@Component({
  selector: 'app-reports-page',
  imports: [DatePipe, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, MatTableModule, MatTooltipModule, MoneyPipe],
  templateUrl: './reports-page.html',
  styleUrl: './reports-page.scss',
})
export class ReportsPage {
  private readonly api = inject(ReportsApi);
  private readonly store = inject(OrdersStore);
  private readonly router = inject(Router);

  // Null is today, whatever date that is when asked.
  protected readonly date = signal<string | null>(null);
  protected readonly report = signal<DayReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  // The business date the till server calls today, learned from the first report it sends.
  private readonly today = signal<string | null>(null);
  protected readonly isToday = computed(() => this.date() === null || this.date() === this.today());

  protected readonly orderColumns = ['opened', 'order', 'status', 'method', 'total', 'refunded'];
  protected readonly label = orderLabel;
  protected readonly statusText = statusText;

  private refreshTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // A fresh report whenever the day changes.
    effect(() => {
      const date = this.date();
      untracked(() => void this.load(date));
    });

    // Today's report follows the orders: a checkout or a refund anywhere shows up here. A
    // second's pause gathers a burst of changes into one reload.
    let seen = this.store.changes();
    effect(() => {
      const changes = this.store.changes();
      if (changes === seen) {
        return;
      }
      seen = changes;
      untracked(() => {
        if (this.isToday()) {
          this.scheduleRefresh();
        }
      });
    });

    inject(DestroyRef).onDestroy(() => {
      if (this.refreshTimer) {
        clearTimeout(this.refreshTimer);
      }
    });
  }

  protected async load(date: string | null): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const report = await firstValueFrom(this.api.day(date));
      if (date === null) {
        this.today.set(report.businessDate);
      }
      // A slow answer for a day the cashier has already moved away from is not shown.
      if (date === this.date()) {
        this.report.set(report);
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The report could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  protected step(days: number): void {
    const current = this.report()?.businessDate ?? this.today();
    if (!current) {
      return;
    }
    const next = shiftDate(current, days);
    this.date.set(this.today() !== null && next >= this.today()! ? null : next);
  }

  protected showToday(): void {
    this.date.set(null);
  }

  protected open(order: Order): void {
    void this.router.navigate(['/orders', order.publicId]);
  }

  protected paidWith(order: Order): string {
    return isSettled(order) ? methodLabel(order.paymentMethod) : '';
  }

  protected methodLabel = methodLabel;

  protected typeLabel(type: OrderType): string {
    return type === 'DineIn' ? 'Dine-in' : type;
  }

  protected methodIcon(method: PaymentMethod): string {
    switch (method) {
      case 'Cash':
        return 'payments';
      case 'Visa':
        return 'credit_card';
      case 'Instapay':
        return 'qr_code_2';
    }
  }

  // The business date as a calendar date, read as written: no time zone shift.
  protected dayOf(report: DayReport): Date {
    const [year, month, day] = report.businessDate.split('-').map(Number);
    return new Date(year, month - 1, day);
  }

  private scheduleRefresh(): void {
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
    }
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      void this.load(this.date());
    }, 1000);
  }
}

function methodLabel(method: PaymentMethod): string {
  return method === 'Visa' ? 'Card' : method;
}

// yyyy-MM-dd plus or minus some days, in UTC so no time zone can move it.
function shiftDate(date: string, days: number): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10);
}
