import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { AssetVoucher, CustodyApi, reasonLabels } from '../../core/custody/custody.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { BusinessDay, isoDay } from '../costing/financials';

// The custody vouchers (الأذونات) of a run of business days, the latest first: into whose
// custody (إذن دخول), or out of it and why (إذن خروج). A breakage routed to Costing → Waste says
// so. The last 30 days to start with.
@Component({
  selector: 'app-custody-vouchers-page',
  imports: [DatePipe, FormsModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic" class="day">
        <mat-label>From · من</mat-label>
        <input matInput type="date" [ngModel]="from()" (ngModelChange)="pick('from', $event)" [max]="to()" aria-label="From" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="day">
        <mat-label>To · إلى</mat-label>
        <input matInput type="date" [ngModel]="to()" (ngModelChange)="pick('to', $event)" [min]="from()" aria-label="To" />
      </mat-form-field>
      <span class="spacer"></span>
      <span class="muted">{{ vouchers().length }} {{ vouchers().length === 1 ? 'voucher' : 'vouchers' }} · {{ inWaste() }} in Waste</span>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as problem) {
      <p class="error">{{ problem }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Asset<span class="ar">الأصل</span></th>
              <th>Voucher<span class="ar">الإذن</span></th>
              <th>Employee<span class="ar">الموظف</span></th>
              <th class="num">How many<span class="ar">العدد</span></th>
              <th class="num">Cost<span class="ar">التكلفة</span></th>
              <th>By<span class="ar">بواسطة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (v of vouchers(); track v.id) {
              <tr [class.waste]="v.routedToWaste">
                <td class="nowrap">{{ v.date | date: 'EEE d MMM' }}</td>
                <td><strong>{{ v.assetName }}</strong></td>
                <td>
                  <span class="type" [class]="v.type">{{ v.type === 'CheckIn' ? 'In · إذن دخول' : 'Out · إذن خروج' }}</span>
                  @if (v.reason) {
                    <span class="muted"> {{ reasonLabels[v.reason].en }} · {{ reasonLabels[v.reason].ar }}</span>
                  }
                  @if (v.routedToWaste) {
                    <span class="badge">In Waste · في الهالك</span>
                  }
                  @if (v.notes) {
                    <div class="muted small">{{ v.notes }}</div>
                  }
                </td>
                <td>{{ v.employeeName ?? '—' }}</td>
                <td class="num">{{ v.quantity }}</td>
                <td class="num">{{ v.cost === null ? '—' : (v.cost | money) }}</td>
                <td class="muted small">{{ v.recordedBy }}</td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="muted">No vouchers on these days.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .day {
      width: 170px;
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
    .type {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: color-mix(in srgb, var(--till-ok, #2e7d32) 16%, transparent);

      &.CheckOut {
        background: var(--mat-sys-surface-container-high);
      }
    }
    .badge {
      display: inline-block;
      margin-left: 6px;
      padding: 0 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: color-mix(in srgb, var(--till-warn) 30%, transparent);
    }
    tr.waste td {
      background: color-mix(in srgb, var(--till-warn) 7%, transparent);
    }
  `,
})
export class VouchersPage {
  private readonly api = inject(CustodyApi);
  private readonly businessDay = inject(BusinessDay);
  protected readonly reasonLabels = reasonLabels;

  protected readonly from = signal('');
  protected readonly to = signal('');
  protected readonly vouchers = signal<AssetVoucher[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly inWaste = computed(() => this.vouchers().filter((v) => v.routedToWaste).length);

  constructor() {
    void (async () => {
      const today = await this.businessDay.today();
      const start = new Date(`${today}T12:00:00`);
      start.setDate(start.getDate() - 29);
      this.to.set(today);
      this.from.set(isoDay(start));
      await this.load();
    })();
  }

  protected pick(end: 'from' | 'to', day: string): void {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(day ?? '')) {
      return;
    }
    (end === 'from' ? this.from : this.to).set(day);
    if (this.from() <= this.to()) {
      void this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.vouchers.set(await firstValueFrom(this.api.vouchers(this.from(), this.to())));
    } catch (error) {
      this.error.set(errorMessage(error, 'The vouchers could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
