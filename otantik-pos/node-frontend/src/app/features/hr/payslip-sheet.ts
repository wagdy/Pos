import { DatePipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { PayrollAdjustment, PayrollSlip, adjustmentLabels, duration } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

// A payslip as it reads (قسيمة الراتب): the formula line by line, from the base salary to the net
// payable; the days it was worked out from; what is paid of it; and the incentives, allowances,
// advances and salary paid in it. Read-only, but for voiding an incentive (canVoid): the Payroll
// tab's slip and the employee portal's alike. What is put inside it (the Payroll tab's buttons)
// comes between the slip and its lists.
@Component({
  selector: 'app-payslip-sheet',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatTooltipModule, MoneyPipe],
  template: `
    @let s = slip();
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
            @if (f.employmentType === 'Daily') {
              <th>Days' wages <span class="ar">أجر الأيام</span>
                @let paidDays = f.daysWorked - f.daysOffWorked;
                <div class="how">{{ paidDays }} {{ paidDays === 1 ? 'day' : 'days' }} worked at {{ f.dailyWage | money }} a day, days off apart</div>
              </th>
            } @else {
              <th>Base salary <span class="ar">الراتب الأساسي</span>
                <div class="how">{{ f.monthlySalary | money }} a month{{ f.daysEmployed < f.daysInMonth ? ', ' + f.daysEmployed + ' of ' + f.daysInMonth + ' days employed' : '' }}</div>
              </th>
            }
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
          @if (f.holidaysWorked) {
            <tr class="plus">
              <th>+ Holidays worked <span class="ar">بدل العطلات الرسمية</span>
                <div class="how">{{ f.holidaysWorked }} {{ f.holidaysWorked === 1 ? 'holiday' : 'holidays' }}: a day's pay for half a shift or more, the hours for less</div>
              </th>
              <td>{{ f.holidaysWorkedPay | money }}</td>
            </tr>
          }
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
            <th>− Absence <span class="ar">خصم الغياب</span>
              @if (f.employmentType === 'Daily') {
                <div class="how">Daily pay: a day not worked is not paid, nor deducted</div>
              } @else {
                <div class="how">{{ f.absentDays }} {{ f.absentDays === 1 ? 'day' : 'days' }} at {{ s.employee.dailyRate | money }}</div>
              }
            </th>
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
        @if (f.holidaysWorked) {
          <div><dt>Holidays worked</dt><dd>{{ f.holidaysWorked }}</dd></div>
        }
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
    <ng-content />
    <section class="lists">
      <div>
        <h3>Incentives and allowances <span class="inline-ar">الحوافز والبدلات</span></h3>
        @for (a of s.adjustments; track a.id) {
          <div class="line" [class.voided]="!!a.voidedAtUtc">
            <span>{{ labels[a.kind].en }}<span class="ar">{{ labels[a.kind].ar }}</span>@if (a.reason) { <span class="muted"> · {{ a.reason }}</span>}</span>
            <span class="amount">{{ a.amount | money }}</span>
            @if (canVoid() && !a.voidedAtUtc && s.status !== 'Finalized') {
              <button mat-icon-button class="no-print" (click)="voidAdjustment.emit(a)" matTooltip="Void" [attr.aria-label]="'Void the ' + labels[a.kind].en.toLowerCase() + ' of ' + a.amount">
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
            <span>{{ a.date | date: 'd MMM' }}<span class="muted"> · {{ a.outsideTill ? 'outside the till' : a.fromTakingsOf ? 'from the takings of ' + (a.fromTakingsOf | date: 'd MMM') : 'from the safe' }}</span></span>
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
export class PayslipSheet {
  readonly slip = input.required<PayrollSlip>();
  // Its month's first day.
  readonly month = input.required<string>();
  readonly canVoid = input(false);
  readonly voidAdjustment = output<PayrollAdjustment>();

  protected readonly labels = adjustmentLabels;
  protected readonly duration = duration;

  protected readonly status = computed(() => {
    const s = this.slip().status;
    return s === 'Finalized' ? { en: 'Finalized', ar: 'نهائي' } : s === 'Draft' ? { en: 'Draft', ar: 'مسودة' } : { en: 'Not calculated', ar: 'لم يُحتسب' };
  });
}
