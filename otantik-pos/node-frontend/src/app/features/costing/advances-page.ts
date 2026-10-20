import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { AdvanceRow, CostingApi } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { Approvals, pendingLabel } from './approvals';
import { Treasury, monthBounds } from './financials';

// Employees' advances (السلفيات): cash advanced to an employee from the safe or a day's takings, a
// month at a time. Each is owed until the payslip of its payroll month deducts it; one asked for by
// someone who may not approve is listed as waiting, and no cash has moved. Each employee's total
// for the month is shown above the list.
@Component({
  selector: 'app-advances-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ monthStart() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" [disabled]="isThisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      <span class="muted">{{ total() | money }} advanced</span>
      <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> New advance <span class="inline-ar">سلفة جديدة</span></button>
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
      @if (byEmployee().length) {
        <div class="totals" aria-label="Advanced to each employee this month">
          @for (e of byEmployee(); track e.employeeId) {
            <span class="chip"><strong>{{ e.employee }}</strong> {{ e.amount | money }}</span>
          }
        </div>
      }
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Employee<span class="ar">الموظف</span></th>
              <th>Off the pay of<span class="ar">يُخصم من راتب</span></th>
              <th class="num">Amount<span class="ar">المبلغ</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (a of advances(); track a.id) {
              @let asked = approvals.pendingFor(a.id);
              <tr [class.voided]="!!a.voidedAtUtc" [class.pending-deletion]="!!asked">
                <td class="nowrap">{{ a.date | date: 'd MMM' }}</td>
                <td>
                  {{ a.employee }}
                  <div class="muted small">
                    @if (a.fromTakingsOf) {
                      From the takings of {{ a.fromTakingsOf | date: 'd MMM' }} · من إيراد يوم
                    } @else {
                      From the safe · من الخزنة
                    }
                  </div>
                  @if (a.notes) {
                    <div class="muted small">{{ a.notes }}</div>
                  }
                  @if (a.voidedAtUtc) {
                    <div><span class="voided-badge">Voided<span class="ar">ملغى</span></span>
                      <span class="muted small"> by {{ a.voidedBy }}, {{ a.voidedAtUtc | date: 'd MMM' }}</span></div>
                  } @else if (asked) {
                    <div><span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span></div>
                  }
                </td>
                <td class="nowrap">{{ a.payrollMonth | date: 'MMMM y' }}</td>
                <td class="num">{{ a.amount | money }}</td>
                <td>{{ a.recordedBy || '—' }}@if (a.approvedBy) {<div class="muted small">approved by {{ a.approvedBy }}</div>}</td>
                <td class="num">
                  @if (!a.voidedAtUtc) {
                    @if (asked) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval">lock_clock</mat-icon>
                    } @else {
                      <button mat-icon-button class="danger" (click)="voidAdvance(a)" matTooltip="Void" [attr.aria-label]="'Void the advance to ' + a.employee">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="muted">No advances this month.</td></tr>
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
    .totals {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: 12px;
    }
    .chip {
      display: inline-flex;
      gap: 6px;
      padding: 4px 10px;
      border-radius: 999px;
      background: var(--mat-sys-surface-container);
      font-variant-numeric: tabular-nums;
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
export class AdvancesPage {
  private readonly api = inject(CostingApi);
  private readonly treasury = inject(Treasury);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  protected readonly year = signal(new Date().getFullYear());
  protected readonly month = signal(new Date().getMonth() + 1);
  protected readonly advances = signal<AdvanceRow[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly monthStart = computed(() => new Date(this.year(), this.month() - 1, 1));
  protected readonly isThisMonth = computed(() => this.year() === new Date().getFullYear() && this.month() === new Date().getMonth() + 1);
  private readonly standing = computed(() => this.advances().filter((a) => !a.voidedAtUtc));
  protected readonly total = computed(() => this.standing().reduce((sum, a) => sum + a.amount, 0));
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('Treasury').filter((r) => r.summary.startsWith('Employee advance')));

  // Each employee's total, largest first.
  protected readonly byEmployee = computed(() => {
    const totals = new Map<string, { employeeId: string; employee: string; amount: number }>();
    for (const a of this.standing()) {
      const row = totals.get(a.employeeId) ?? { employeeId: a.employeeId, employee: a.employee, amount: 0 };
      row.amount = Math.round((row.amount + a.amount) * 100) / 100;
      totals.set(a.employeeId, row);
    }
    return [...totals.values()].sort((a, b) => b.amount - a.amount);
  });

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
    if (await this.treasury.enter('EmployeeAdvance')) {
      await this.load();
    }
  }

  protected async voidAdvance(advance: AdvanceRow): Promise<void> {
    const day = new Date(advance.date).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
    if ((await this.approvals.delete('Treasury', advance.id, `the advance to ${advance.employee} of ${day}`)) === 'Applied') {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    const { from, to } = monthBounds(this.year(), this.month());
    try {
      this.advances.set(await firstValueFrom(this.api.advances(from, to)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The advances could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
