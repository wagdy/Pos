import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { errorMessage } from '../../core/http/error-message';
import { HrApi, PayrollMonth, PayrollSlip } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { isoDay } from '../costing/financials';

// The monthly payroll (مسير الرواتب): every employee's pay for the month, each part of it a column,
// as the formula adds them up:
//   base salary + incentives + leave allowances + overtime − advances − last month's shortfall
//   − lateness − absence = net payable.
// A draft is worked out from the month's attendance as it stands now; calculating saves the
// drafts. Each row opens the employee's payslip, where it is finalized and paid.
@Component({
  selector: 'app-payroll-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar no-print">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ month() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      @if (canCalculate()) {
        <button mat-stroked-button (click)="calculate()" [disabled]="calculating()" matTooltip="Save every draft as it stands now">
          <mat-icon>calculate</mat-icon> Calculate drafts <span class="inline-ar">احتساب المسودات</span>
        </button>
      }
      <button mat-button (click)="print()" [disabled]="!payroll()"><mat-icon>print</mat-icon> Print</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (payroll(); as p) {
      <header class="report-head">
        <h2>Payroll · {{ month() | date: 'MMMM y' }} <span class="inline-ar">مسير الرواتب</span></h2>
        <div class="muted">{{ finalized() }} of {{ p.slips.length }} finalized · drafts are worked out from the attendance as it stands now</div>
      </header>
      <div class="tiles">
        <div class="tile">
          <span class="label">Net payable · صافي المستحق</span>
          <strong>{{ p.totals.netPayable | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Paid so far · المصروف</span>
          <strong>{{ p.totals.salaryPaid | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Advances deducted · السلفيات</span>
          <strong>{{ p.totals.advances | money }}</strong>
        </div>
        <div class="tile">
          <span class="label">Wages cost · تكلفة الأجور</span>
          <strong>{{ p.totals.wagesCost | money }}</strong>
          <small class="muted">what the month's work cost: in the KPIs once finalized</small>
        </div>
      </div>

      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Employee<span class="ar">الموظف</span></th>
              <th class="num">Base salary<span class="ar">الراتب الأساسي</span></th>
              <th class="num">+ Incentives<span class="ar">حوافز</span></th>
              <th class="num">+ Leave allowances<span class="ar">بدل إجازات</span></th>
              <th class="num">+ Overtime<span class="ar">إضافي</span></th>
              <th class="num">− Advances<span class="ar">السلفيات</span></th>
              <th class="num">− Lateness<span class="ar">خصم التأخير</span></th>
              <th class="num">− Absence<span class="ar">خصم الغياب</span></th>
              <th class="num">− Last month<span class="ar">مرحل</span></th>
              <th class="num">= Net payable<span class="ar">صافي المستحق</span></th>
              <th>Status<span class="ar">الحالة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (s of p.slips; track s.employee.id) {
              @let f = s.figures;
              <tr>
                <td>
                  <a [routerLink]="[s.employee.id]" [queryParams]="{ month: p.month }"><strong>{{ s.employee.name }}</strong></a>
                  <div class="muted small">{{ s.employee.jobTitle }}@if (f.daysEmployed < f.daysInMonth) { · {{ f.daysEmployed }} of {{ f.daysInMonth }} days}</div>
                </td>
                <td class="num">{{ f.baseSalary | money }}</td>
                <td class="num">{{ f.incentives | money }}</td>
                <td class="num">{{ f.leaveAllowances | money }}</td>
                <td class="num">{{ f.overtimePay | money }}</td>
                <td class="num minus">{{ f.advances | money }}</td>
                <td class="num minus">{{ f.latenessPenalty | money }}</td>
                <td class="num minus">{{ f.absencePenalty | money }}</td>
                <td class="num minus">{{ f.broughtForward | money }}</td>
                <td class="num net">
                  <strong>{{ f.netPayable | money }}</strong>
                  @if (f.carriedForward > 0) {
                    <div class="small bad">{{ f.carriedForward | money }} to next month</div>
                  }
                </td>
                <td>
                  <span class="status" [class]="s.status ?? 'none'">{{ statusOf(s).en }}<span class="ar">{{ statusOf(s).ar }}</span></span>
                  @if (s.status === 'Finalized') {
                    <div class="muted small">{{ s.salaryOwed > 0 ? (s.salaryOwed | money) + ' to pay' : 'Paid' }}</div>
                  } @else if (f.missingPunchDays) {
                    <div class="small warn">{{ f.missingPunchDays }} {{ f.missingPunchDays === 1 ? 'day' : 'days' }} missing a punch</div>
                  } @else if (f.openDays) {
                    <div class="muted small">The month is not over</div>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="11" class="muted">No one was employed this month.</td></tr>
            }
          </tbody>
          @if (p.slips.length) {
            <tfoot>
              <tr>
                <td>Total · الإجمالي</td>
                <td class="num">{{ p.totals.baseSalaries | money }}</td>
                <td class="num">{{ p.totals.incentives | money }}</td>
                <td class="num">{{ p.totals.leaveAllowances | money }}</td>
                <td class="num">{{ p.totals.overtimePay | money }}</td>
                <td class="num">{{ p.totals.advances | money }}</td>
                <td class="num">{{ p.totals.latenessPenalties | money }}</td>
                <td class="num">{{ p.totals.absencePenalties | money }}</td>
                <td class="num">{{ p.totals.broughtForward | money }}</td>
                <td class="num">{{ p.totals.netPayable | money }}</td>
                <td></td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
      <p class="note">
        A day costs the salary over 30, an hour a day's over 9: lateness (late, leaving early, away during the shift) and absence are
        deducted at that rate, and overtime paid at it. When the deductions are more than the pay, nothing is paid and the rest comes off
        next month's. Open a payslip to add incentives and leave allowances, finalize it, and pay it.
      </p>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .month {
      min-width: 140px;
      text-align: center;
      font: var(--mat-sys-title-medium);
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
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      strong {
        font: var(--mat-sys-headline-small);
      }
    }
    td.minus {
      color: var(--mat-sys-on-surface-variant);
    }
    td.net {
      background: color-mix(in srgb, var(--mat-sys-primary) 6%, transparent);
    }
    .status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);

      &.Finalized {
        background: color-mix(in srgb, #2e7d32 16%, transparent);
      }
    }
    .small {
      font-size: 0.85em;
    }
    .bad {
      color: var(--mat-sys-error);
    }
    .warn {
      color: var(--till-warn);
    }
  `,
})
export class PayrollPage {
  private readonly api = inject(HrApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  // ?month= its first day, to open on it.
  readonly startMonth = input<string>(undefined, { alias: 'month' });

  protected readonly month = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly payroll = signal<PayrollMonth | null>(null);
  protected readonly loading = signal(false);
  protected readonly calculating = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly canCalculate = computed(() => this.auth.can(Permissions.HrManage));
  protected readonly finalized = computed(() => (this.payroll()?.slips ?? []).filter((s) => s.status === 'Finalized').length);

  constructor() {
    effect(() => {
      const month = this.startMonth();
      untracked(() => {
        if (month && /^\d{4}-\d{2}-\d{2}$/.test(month)) {
          this.month.set(`${month.slice(0, 8)}01`);
        }
        void this.load();
      });
    });
  }

  protected statusOf(slip: PayrollSlip): { en: string; ar: string } {
    return slip.status === 'Finalized'
      ? { en: 'Finalized', ar: 'نهائي' }
      : slip.status === 'Draft'
        ? { en: 'Draft', ar: 'مسودة' }
        : { en: 'Not calculated', ar: 'لم يُحتسب' };
  }

  protected move(by: number): void {
    const start = new Date(`${this.month()}T12:00:00`);
    this.month.set(isoDay(new Date(start.getFullYear(), start.getMonth() + by, 1)));
    void this.load();
  }

  protected async calculate(): Promise<void> {
    this.calculating.set(true);
    try {
      this.payroll.set(await firstValueFrom(this.api.calculatePayroll(this.month())));
      this.notify.info('The drafts are saved as they stand now.');
    } catch (error) {
      this.notify.error(error, 'The payroll could not be calculated.');
    } finally {
      this.calculating.set(false);
    }
  }

  protected print(): void {
    window.print();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.payroll.set(await firstValueFrom(this.api.payroll(this.month())));
    } catch (error) {
      this.error.set(errorMessage(error, 'The payroll could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
