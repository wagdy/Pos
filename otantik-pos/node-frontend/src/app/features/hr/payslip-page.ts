import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { AdjustmentKind, HrApi, PayrollAdjustment, PayrollSlip, adjustmentLabels, duration } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { Treasury, isoDay } from '../costing/financials';

export interface AdjustmentInput {
  kind: AdjustmentKind;
  amount: number;
  reason: string | null;
}

// An incentive or a leave allowance for the month: how much, and why.
@Component({
  selector: 'app-adjustment-dialog',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>Add to {{ data.employee }}’s pay <span class="ar">إضافة للراتب</span></h2>
    <mat-dialog-content>
      <mat-radio-group [(ngModel)]="kind" aria-label="What it is">
        <mat-radio-button value="Incentive">Incentive or bonus <span class="ar">حوافز</span></mat-radio-button>
        <mat-radio-button value="LeaveAllowance">Leave allowance: a holiday worked, or other <span class="ar">بدل إجازات</span></mat-radio-button>
      </mat-radio-group>
      <mat-form-field class="wide">
        <mat-label>Amount · المبلغ</mat-label>
        <input matInput type="number" min="0" step="any" [(ngModel)]="amount" aria-label="Amount" />
        <span matTextSuffix>&nbsp;L.E</span>
        @if (kind === 'LeaveAllowance') {
          <mat-hint>A day’s pay is {{ data.dailyRate | number: '1.2-2' }}. Days off worked are counted by themselves.</mat-hint>
        }
      </mat-form-field>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Why" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!(amount && amount > 0)" [mat-dialog-close]="{ kind, amount, reason: reason.trim() || null }">Add</button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide {
      width: 100%;
    }
    mat-radio-group {
      display: grid;
      gap: 4px;
      margin-bottom: 12px;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
  `,
})
export class AdjustmentDialog {
  protected readonly data = inject<{ employee: string; dailyRate: number }>(MAT_DIALOG_DATA);
  protected kind: AdjustmentKind = 'Incentive';
  protected amount: number | null = null;
  protected reason = '';
}

// Before finalizing: what it fixes.
@Component({
  selector: 'app-finalize-dialog',
  imports: [MatButtonModule, MatDialogModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Finalize {{ data.employee }}’s pay? <span class="ar">اعتماد الراتب</span></h2>
    <mat-dialog-content>
      <p>Net payable <strong>{{ data.net | money }}</strong>. Once finalized, the slip stays as it is: its days, incentives and advances are not changed after it, and what is paid or advanced later counts next month.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [mat-dialog-close]="true">Finalize</button>
    </mat-dialog-actions>
  `,
  styles: `
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
  `,
})
export class FinalizeDialog {
  protected readonly data = inject<{ employee: string; net: number }>(MAT_DIALOG_DATA);
}

// An employee's payslip for a month (قسيمة الراتب): the formula, line by line, from the base salary
// to the net payable; the days it was worked out from; the incentives, allowances and advances in
// it; and what has been paid of it. A manager adds incentives and allowances and finalizes it once
// the month is over and every day is settled; then it is paid from the safe or a day's takings.
// It prints as a payslip.
@Component({
  selector: 'app-payslip-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar no-print">
      <a mat-button routerLink="../" [queryParams]="{ month: month() }"><mat-icon>arrow_back</mat-icon> Payroll · مسير الرواتب</a>
      <span class="spacer"></span>
      @if (slip(); as s) {
        <a mat-button routerLink="/hr/attendance" [queryParams]="{ employee: s.employee.id, month: month() }"><mat-icon>schedule</mat-icon> Timesheet</a>
        <button mat-button (click)="print()"><mat-icon>print</mat-icon> Print</button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (slip(); as s) {
      @let f = s.figures;
      <article class="slip">
        <header>
          <div>
            <h2>{{ s.employee.name }}</h2>
            <div class="muted">{{ s.employee.jobTitle }} · {{ month() | date: 'MMMM y' }}</div>
          </div>
          <div class="state">
            <span class="status" [class]="s.status ?? 'none'">{{ status().en }}<span class="ar">{{ status().ar }}</span></span>
            @if (s.finalizedBy) {
              <div class="muted small">by {{ s.finalizedBy }}, {{ s.finalizedAtUtc | date: 'd MMM' }}</div>
            }
          </div>
        </header>
        <h3>Payslip <span class="inline-ar">قسيمة الراتب</span></h3>

        <table class="breakdown" aria-label="How the net payable is made">
          <tbody>
            <tr>
              <th>Base salary <span class="ar">الراتب الأساسي</span>
                <div class="how">{{ f.monthlySalary | money }} a month{{ f.daysEmployed < f.daysInMonth ? ', ' + f.daysEmployed + ' of ' + f.daysInMonth + ' days employed' : '' }}</div>
              </th>
              <td>{{ f.baseSalary | money }}</td>
            </tr>
            <tr class="plus">
              <th>+ Incentives <span class="ar">حوافز</span></th>
              <td>{{ f.incentives | money }}</td>
            </tr>
            <tr class="plus">
              <th>+ Days off worked <span class="ar">بدل أيام الراحة</span>
                <div class="how">{{ f.daysOffWorked }} {{ f.daysOffWorked === 1 ? 'day' : 'days' }}: a day's pay for half a shift or more, the hours for less</div>
              </th>
              <td>{{ f.daysOffWorkedPay | money }}</td>
            </tr>
            <tr class="plus">
              <th>+ Leave allowances <span class="ar">بدل إجازات</span><div class="how">as entered below</div></th>
              <td>{{ f.leaveAllowances | money }}</td>
            </tr>
            <tr class="plus">
              <th>+ Overtime <span class="ar">إضافي</span><div class="how">{{ duration(f.overtimeMinutes) }} at {{ s.employee.hourlyRate | money }} an hour</div></th>
              <td>{{ f.overtimePay | money }}</td>
            </tr>
            <tr class="subtotal">
              <th>= Gross pay <span class="ar">إجمالي المستحق</span></th>
              <td>{{ f.gross | money }}</td>
            </tr>
            <tr class="minus">
              <th>− Advances <span class="ar">السلفيات</span><div class="how">{{ s.advances.length }} {{ s.advances.length === 1 ? 'advance' : 'advances' }} this month</div></th>
              <td>{{ f.advances | money }}</td>
            </tr>
            @if (f.broughtForward) {
              <tr class="minus">
                <th>− Owed from last month <span class="ar">مرحل من الشهر السابق</span></th>
                <td>{{ f.broughtForward | money }}</td>
              </tr>
            }
            <tr class="minus">
              <th>− Lateness <span class="ar">خصم التأخير</span><div class="how">{{ duration(f.shortMinutes) }} late, left early or away</div></th>
              <td>{{ f.latenessPenalty | money }}</td>
            </tr>
            <tr class="minus">
              <th>− Absence <span class="ar">خصم الغياب</span><div class="how">{{ f.absentDays }} {{ f.absentDays === 1 ? 'day' : 'days' }} at {{ s.employee.dailyRate | money }}</div></th>
              <td>{{ f.absencePenalty | money }}</td>
            </tr>
            @if (f.administrativePenalty) {
              <tr class="minus">
                <th>− Administrative penalties <span class="ar">جزاءات إدارية</span><div class="how">{{ f.administrativePenaltyHours }} hours at {{ s.employee.hourlyRate | money }}</div></th>
                <td>{{ f.administrativePenalty | money }}</td>
              </tr>
            }
            <tr class="total">
              <th>= Net payable <span class="ar">صافي المستحق</span></th>
              <td>{{ f.netPayable | money }}</td>
            </tr>
            @if (f.carriedForward > 0) {
              <tr class="carried">
                <th>Carried to next month <span class="ar">يُرحل للشهر التالي</span><div class="how">The deductions are more than the pay</div></th>
                <td>{{ f.carriedForward | money }}</td>
              </tr>
            }
          </tbody>
        </table>

        <dl class="days">
          <div><dt>Days worked</dt><dd>{{ f.daysWorked }}</dd></div>
          <div><dt>Days off worked</dt><dd>{{ f.daysOffWorked }}</dd></div>
          <div><dt>Absent</dt><dd>{{ f.absentDays }}</dd></div>
          <div><dt>Suspended</dt><dd [class.warn]="f.missingPunchDays > 0">{{ f.missingPunchDays }}</dd></div>
          @if (f.openDays) {
            <div><dt>Not over yet</dt><dd>{{ f.openDays }}</dd></div>
          }
        </dl>

        @if (s.status === 'Finalized') {
          <p class="paid">
            Paid <strong>{{ s.salaryPaid | money }}</strong>
            @if (s.salaryOwed > 0) {
              · <strong>{{ s.salaryOwed | money }}</strong> still to pay
            } @else {
              · paid in full
            }
          </p>
        }
      </article>

      <div class="actions no-print">
        @if (canDecide() && s.status !== 'Finalized') {
          <button mat-stroked-button (click)="addAdjustment(s)"><mat-icon>add</mat-icon> Incentive or allowance <span class="inline-ar">حوافز أو بدل</span></button>
          <span [matTooltip]="finalizeBlocked(s) ?? ''" [matTooltipDisabled]="!finalizeBlocked(s)">
            <button mat-flat-button (click)="finalize(s)" [disabled]="!!finalizeBlocked(s)"><mat-icon>verified</mat-icon> Finalize <span class="inline-ar">اعتماد</span></button>
          </span>
        }
        @if (s.status === 'Finalized' && s.salaryOwed > 0) {
          <button mat-flat-button (click)="pay(s)"><mat-icon>payments</mat-icon> Pay salary <span class="inline-ar">صرف الراتب</span></button>
        }
      </div>
      @if (s.status !== 'Finalized' && finalizeBlocked(s); as why) {
        <p class="note no-print">{{ why }}</p>
      }

      <section class="lists">
        <div>
          <h3>Incentives and allowances <span class="inline-ar">الحوافز والبدلات</span></h3>
          @for (a of s.adjustments; track a.id) {
            <div class="line" [class.voided]="!!a.voidedAtUtc">
              <span>{{ labels[a.kind].en }}<span class="ar">{{ labels[a.kind].ar }}</span>@if (a.reason) { <span class="muted"> · {{ a.reason }}</span>}</span>
              <span class="amount">{{ a.amount | money }}</span>
              @if (canDecide() && !a.voidedAtUtc && s.status !== 'Finalized') {
                <button mat-icon-button class="no-print" (click)="voidAdjustment(a)" matTooltip="Void" [attr.aria-label]="'Void the ' + labels[a.kind].en.toLowerCase() + ' of ' + a.amount">
                  <mat-icon>delete</mat-icon>
                </button>
              }
            </div>
          } @empty {
            <p class="muted">None this month.</p>
          }
        </div>
        <div>
          <h3>Advances <span class="inline-ar">السلفيات</span></h3>
          @for (a of s.advances; track a.id) {
            <div class="line" [class.voided]="!!a.voidedAtUtc">
              <span>{{ a.date | date: 'd MMM' }}<span class="muted"> · {{ a.fromTakingsOf ? 'from the takings of ' + (a.fromTakingsOf | date: 'd MMM') : 'from the safe' }}</span></span>
              <span class="amount">{{ a.amount | money }}</span>
            </div>
          } @empty {
            <p class="muted">None this month.</p>
          }
        </div>
        @if (s.salaries.length) {
          <div>
            <h3>Salary paid <span class="inline-ar">الراتب المصروف</span></h3>
            @for (p of s.salaries; track p.id) {
              <div class="line" [class.voided]="!!p.voidedAtUtc">
                <span>{{ p.date | date: 'd MMM' }}<span class="muted"> · {{ p.fromTakingsOf ? 'from the takings of ' + (p.fromTakingsOf | date: 'd MMM') : 'from the safe' }}</span></span>
                <span class="amount">{{ p.amount | money }}</span>
              </div>
            }
          </div>
        }
      </section>
    } @else if (!loading()) {
      <p class="muted">They were not employed in {{ month() | date: 'MMMM y' }}.</p>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .slip {
      max-width: 640px;
      padding: 16px 20px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      header {
        display: flex;
        justify-content: space-between;
        gap: 12px;
      }
      h2 {
        margin: 0;
        font: var(--mat-sys-headline-small);
      }
      h3 {
        margin: 16px 0 8px;
        font: var(--mat-sys-title-small);
        color: var(--mat-sys-on-surface-variant);
      }
      .state {
        text-align: end;
      }
    }
    .breakdown {
      width: 100%;
      border-collapse: collapse;

      th,
      td {
        padding: 6px 0;
        border-bottom: 1px solid var(--mat-sys-outline-variant);
        vertical-align: top;
      }
      th {
        text-align: start;
        font-weight: 500;
      }
      td {
        text-align: end;
        white-space: nowrap;
        font-variant-numeric: tabular-nums;
      }
      .how {
        font: var(--mat-sys-body-small);
        color: var(--mat-sys-on-surface-variant);
      }
      tr.minus td {
        color: var(--mat-sys-error);
      }
      tr.subtotal th,
      tr.subtotal td {
        border-bottom-width: 2px;
      }
      tr.total th,
      tr.total td {
        padding-top: 10px;
        font: var(--mat-sys-title-large);
        border-bottom: none;
      }
      tr.carried td {
        color: var(--mat-sys-error);
      }
    }
    .days {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 24px;
      margin: 16px 0 0;

      dt {
        font: var(--mat-sys-label-small);
        color: var(--mat-sys-on-surface-variant);
      }
      dd {
        margin: 0;
        font: var(--mat-sys-title-medium);
      }
      .warn {
        color: var(--till-warn);
      }
    }
    .paid {
      margin: 16px 0 0;
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--mat-sys-surface-container);
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
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin: 16px 0 8px;
    }
    .lists {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: 16px;
      max-width: 900px;
      margin-top: 16px;

      h3 {
        margin: 0 0 6px;
        font: var(--mat-sys-title-small);
      }
    }
    .line {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 4px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);

      .amount {
        margin-left: auto;
        font-variant-numeric: tabular-nums;
      }
      &.voided {
        text-decoration: line-through;
        color: var(--mat-sys-on-surface-variant);
      }
    }
    .small {
      font-size: 0.85em;
    }
    @media print {
      .slip {
        border: none;
        max-width: none;
      }
    }
  `,
})
export class PayslipPage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly treasury = inject(Treasury);
  private readonly approvals = inject(Approvals);

  readonly employeeId = input.required<string>();
  readonly startMonth = input<string>(undefined, { alias: 'month' });

  protected readonly labels = adjustmentLabels;
  protected readonly duration = duration;
  protected readonly canDecide = this.approvals.canApprove;
  protected readonly month = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly slip = signal<PayrollSlip | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly status = computed(() => {
    const s = this.slip()?.status;
    return s === 'Finalized' ? { en: 'Finalized', ar: 'نهائي' } : s === 'Draft' ? { en: 'Draft', ar: 'مسودة' } : { en: 'Not calculated', ar: 'لم يُحتسب' };
  });

  constructor() {
    effect(() => {
      const [, month] = [this.employeeId(), this.startMonth()];
      untracked(() => {
        if (month && /^\d{4}-\d{2}-\d{2}$/.test(month)) {
          this.month.set(`${month.slice(0, 8)}01`);
        }
        void this.load();
      });
    });
  }

  // What keeps it from being finalized yet, said plainly; null when nothing does.
  protected finalizeBlocked(s: PayrollSlip): string | null {
    if (s.figures.openDays > 0) {
      return 'The month is not over yet: a payslip is finalized once all its days are.';
    }
    if (s.figures.missingPunchDays > 0) {
      const n = s.figures.missingPunchDays;
      return `${n} ${n === 1 ? 'day is' : 'days are'} suspended, a punch-in with no punch-out: enter the punch-out on the attendance first.`;
    }
    return null;
  }

  protected async addAdjustment(s: PayrollSlip): Promise<void> {
    const answer = await firstValueFrom(
      this.dialog
        .open<AdjustmentDialog, { employee: string; dailyRate: number }, AdjustmentInput>(AdjustmentDialog, {
          data: { employee: s.employee.name, dailyRate: s.employee.dailyRate },
          width: '460px',
        })
        .afterClosed(),
    );
    if (answer) {
      await this.act(() => firstValueFrom(this.api.addAdjustment(s.employee.id, this.month(), answer.kind, answer.amount, answer.reason)), 'Added to the pay.');
    }
  }

  protected async voidAdjustment(a: PayrollAdjustment): Promise<void> {
    await this.act(() => firstValueFrom(this.api.voidAdjustment(a.id)), 'Voided.');
  }

  protected async finalize(s: PayrollSlip): Promise<void> {
    const sure = await firstValueFrom(
      this.dialog
        .open<FinalizeDialog, { employee: string; net: number }, boolean>(FinalizeDialog, { data: { employee: s.employee.name, net: s.figures.netPayable }, width: '460px' })
        .afterClosed(),
    );
    if (sure) {
      await this.act(() => firstValueFrom(this.api.finalizeSlip(s.employee.id, this.month())), `${s.employee.name}’s pay is finalized.`);
    }
  }

  protected async pay(s: PayrollSlip): Promise<void> {
    if (await this.treasury.enter('SalaryPayment', { employeeId: s.employee.id, payrollMonth: this.month(), amount: s.salaryOwed })) {
      await this.load();
    }
  }

  protected print(): void {
    window.print();
  }

  private async act(call: () => Promise<unknown>, done: string): Promise<void> {
    try {
      await call();
      this.notify.info(done);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const payroll = await firstValueFrom(this.api.payroll(this.month()));
      this.slip.set(payroll.slips.find((s) => s.employee.id === this.employeeId()) ?? null);
    } catch (error) {
      this.error.set(errorMessage(error, 'The payslip could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
