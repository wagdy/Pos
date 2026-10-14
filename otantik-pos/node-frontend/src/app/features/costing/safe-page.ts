import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { CostingApi, SafeEntry, SafeMonth, SafeTransactionKind, fundingSourceLabels, safeKindLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { Approvals, pendingLabel } from './approvals';
import { Treasury, entryTax } from './financials';

// The safe (الخزنة): what is in it now, and month by month the balance it opened with (carried
// over from the month before, or the treasury's own opening entry, and from where), what came in
// and went out, and what it closed with; every entry, with the balance after it. Entries waiting
// for a manager are listed first: no cash has moved for them. An entry is voided, never edited: a
// reversing entry dated the day of the void.
@Component({
  selector: 'app-safe-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      @if (!isThisMonth()) {
        <button mat-button (click)="thisMonth()">This month</button>
      }
      <span class="spacer"></span>
      @if (safe() && !safe()!.hasOpeningBalance) {
        <button mat-stroked-button (click)="enter('OpeningBalance')"><mat-icon>flag</mat-icon> Opening balance</button>
      }
      <button mat-stroked-button (click)="enter('Funding')"><mat-icon>savings</mat-icon> Add funding</button>
      <button mat-stroked-button (click)="enter('RevenueTransfer')"><mat-icon>point_of_sale</mat-icon> Takings in</button>
      <button mat-stroked-button (click)="enter('ExpenseVoucher')"><mat-icon>receipt</mat-icon> Expense voucher</button>
      <button mat-flat-button (click)="enter('SupplierPayment')"><mat-icon>payments</mat-icon> Pay a supplier</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (safe(); as s) {
      @if (!s.hasOpeningBalance) {
        <p class="note">
          The safe has no opening balance yet: enter the cash in it as the treasury starts, and whether it was carried over or put in by the owners.
          <span class="inline-ar">أدخل الرصيد الافتتاحي للخزنة.</span>
        </p>
      }
      <div class="tiles">
        <div class="tile key">
          <span class="label">In the safe now · رصيد الخزنة</span>
          <strong [class.negative]="s.balanceNow < 0">{{ s.balanceNow | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Opened with · الرصيد الافتتاحي</span>
          <strong>{{ s.openingBalance | money }}</strong>
          <small class="muted">{{ openingText(s) }}</small>
        </div>
        <div class="tile">
          <span class="label">In · المقبوضات</span>
          <strong class="in">{{ s.in | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Out · المدفوعات</span>
          <strong class="out">{{ s.out | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Closed with · الرصيد الختامي</span>
          <strong [class.negative]="s.closingBalance < 0">{{ s.closingBalance | money }}</strong>
          <small class="muted">{{ isThisMonth() ? 'so far' : 'carried over to the next month' }}</small>
        </div>
      </div>
      @if (s.voidedIn || s.voidedOut) {
        <p class="note voided-note">
          Not in In or Out: {{ s.voidedIn | money }} in and {{ s.voidedOut | money }} out, voided this month along with their reversals.
          <span class="inline-ar">لا تشمل القيود الملغاة.</span>
        </p>
      }

      @if (waiting().length) {
        <h3>Waiting for approval <span class="inline-ar">بانتظار موافقة المدير</span></h3>
        <ul class="waiting">
          @for (r of waiting(); track r.id) {
            <li>
              <span class="pending-badge">{{ pendingLabel(r).en }}<span class="ar">{{ pendingLabel(r).ar }}</span></span>
              {{ r.summary }} <span class="muted small">· asked by {{ r.requestedBy }}, {{ r.requestedAtUtc | date: 'd MMM, HH:mm' }}: no cash has moved</span>
            </li>
          }
        </ul>
      }

      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Entry<span class="ar">البيان</span></th>
              <th class="num">In<span class="ar">وارد</span></th>
              <th class="num">Out<span class="ar">منصرف</span></th>
              <th class="num">Balance<span class="ar">الرصيد</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            <tr class="opening">
              <td class="nowrap">{{ s.from | date: 'd MMM' }}</td>
              <td colspan="3">Opened with <span class="muted">· {{ openingText(s) }}</span></td>
              <td class="num">{{ s.openingBalance | money }}</td>
              <td colspan="2"></td>
            </tr>
            @for (e of s.entries; track e.id) {
              @let asked = approvals.pendingFor(e.id);
              <tr [class.voided]="!!e.voidedAtUtc" [class.reversal]="!!e.reversalOf" [class.pending-deletion]="!!asked">
                <td class="nowrap">{{ e.date | date: 'd MMM' }}</td>
                <td>
                  <span class="kind" [class]="e.kind">{{ kinds[e.kind].en }}</span> {{ e.description }}
                  @if (entryTax(e); as t) {
                    <div class="muted small">Tax in it at {{ t.percent }}%: {{ t.tax | money }} · {{ t.net | money }} before tax</div>
                  }
                  @if (e.notes && !e.reversalOf) {
                    <div class="muted small">{{ e.notes }}</div>
                  }
                  @if (e.voidedAtUtc) {
                    <div><span class="voided-badge">Voided<span class="ar">ملغى</span></span>
                      <span class="muted small"> by {{ e.voidedBy }}, {{ e.voidedAtUtc | date: 'd MMM' }}</span></div>
                  } @else if (asked) {
                    <div><span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span>
                      <span class="muted small"> by {{ asked.requestedBy }}</span></div>
                  }
                </td>
                <td class="num in">{{ e.in ? (e.in | money) : '' }}</td>
                <td class="num out">{{ e.out ? (e.out | money) : '' }}</td>
                <td class="num" [class.negative]="(e.balance ?? 0) < 0">{{ e.balance === null ? '' : (e.balance | money) }}</td>
                <td>
                  {{ e.recordedBy || '—' }}
                  @if (e.approvedBy) {
                    <div class="muted small">approved by {{ e.approvedBy }}</div>
                  }
                </td>
                <td class="num">
                  @if (!e.voidedAtUtc && !e.reversalOf) {
                    @if (asked) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval">lock_clock</mat-icon>
                    } @else {
                      <button mat-icon-button class="danger" (click)="voidEntry(e)" matTooltip="Void" [attr.aria-label]="'Void ' + e.description">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="muted">Nothing in or out of the safe this month.</td></tr>
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
    h3 {
      margin: 16px 0 8px;
      font: var(--mat-sys-title-small);
    }
    .waiting {
      margin: 0 0 16px;
      padding: 0;
      list-style: none;
      display: grid;
      gap: 4px;
    }
    tr.opening td {
      background: var(--mat-sys-surface-container);
      font-weight: 500;
    }
    tr.voided td {
      color: var(--mat-sys-on-surface-variant);
    }
    tr.reversal td {
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }
    .in {
      color: var(--till-ok);
    }
    .out,
    .negative {
      color: var(--mat-sys-error);
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
export class SafePage {
  private readonly api = inject(CostingApi);
  private readonly treasury = inject(Treasury);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;
  protected readonly kinds = safeKindLabels;
  protected readonly entryTax = entryTax;

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly safe = signal<SafeMonth | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);
  // New safe entries asked for, and invoices whose cash payment waits: no cash has moved for them.
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('Treasury'));

  constructor() {
    void this.load();
    void this.approvals.refresh();
  }

  protected openingText(s: SafeMonth): string {
    switch (s.openingKind) {
      case 'CarriedOver':
        return 'carried over from the month before · رصيد مرحل';
      case 'OpeningEntry':
        return `the treasury opened this month${s.openingSource ? ': ' + fundingSourceLabels[s.openingSource].en.toLowerCase() : ''}`;
      default:
        return 'no opening balance yet';
    }
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

  protected async enter(kind: SafeTransactionKind): Promise<void> {
    if (await this.treasury.enter(kind)) {
      await this.load();
    }
  }

  // Voided for a manager (its cash goes back, dated today); asked of one for anyone else.
  protected async voidEntry(entry: SafeEntry): Promise<void> {
    const what = `the ${this.kinds[entry.kind].en.toLowerCase()} of ${new Date(entry.date).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}`;
    if ((await this.approvals.delete('Treasury', entry.id, what, entry.description)) === 'Applied') {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.safe.set(await firstValueFrom(this.api.safe(this.year(), this.month())));
    } catch (error) {
      this.error.set(errorMessage(error, 'The safe could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
