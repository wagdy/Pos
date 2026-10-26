import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { AdministrativePenalty, AttendanceDay, Employee, EmployeeDay, HrApi, LatePunch, Punch, clock, dayName, duration, shift } from '../../core/hr/hr.api';
import { HrAccess } from '../../core/hr/hr-access';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { BusinessDay, isoDay } from '../costing/financials';
import { IgnorePunchDialog, PenaltyAnswer, PenaltyData, PenaltyDialog, ResolveDayData, ResolveDayDialog } from './attendance-dialogs';
import { CorrectionsCount } from './corrections-count';

// Corrections (مراجعة وجزاءات): what waits for a manager in the attendance, and the month's
// administrative penalties.
//
// Punches can also come late: the device keeps them while the till is away (the laptop off the
// network) and sends them when it is back, sometimes after their month's pay is finalized. They
// are kept, the slip stays as it was, and they are listed here until a manager has seen to them,
// settling in a later month's pay what they change.
//
// A suspended day is a punch-in left 18 hours without its punch-out: it is not worked out, and its
// month's pay can't be finalized, until a manager enters when they left, or ignores the punch when
// it was made by mistake. A penalty (جزاء إداري) is hours on a day, with the reason, taken off the
// month's pay at the employee's hourly rate; a disciplinary action's penalty deduction is listed
// with them, an amount taken off as it is, linked to its action on the employee's profile, where
// it is withdrawn. Accountants see both; managers put them right.
@Component({
  selector: 'app-corrections-page',
  imports: [
    DatePipe,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatTooltipModule,
    MoneyPipe,
    RouterLink,
  ],
  template: `
    <section class="suspended" aria-labelledby="suspended-title">
      <div class="toolbar">
        <h2 id="suspended-title">Suspended days <span class="inline-ar">أيام موقوفة للمراجعة</span></h2>
        <span class="spacer"></span>
        <mat-form-field subscriptSizing="dynamic" class="since">
          <mat-label>Since · منذ</mat-label>
          <input matInput type="date" [ngModel]="from()" (ngModelChange)="pickFrom($event)" aria-label="Since" />
        </mat-form-field>
      </div>
      <p class="note">
        A punch-in left 18 hours without its punch-out. The day isn't worked out, and its month's pay can't be finalized, until a manager enters
        when they left, or ignores the punch if it was made by mistake.
      </p>
      @if (loadingDays()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (daysError()) {
        <p class="error">{{ daysError() }}</p>
      } @else {
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Day<span class="ar">اليوم</span></th>
                <th>Employee<span class="ar">الموظف</span></th>
                <th>Shift<span class="ar">الوردية</span></th>
                <th>Punches<span class="ar">البصمات</span></th>
                <th class="num"><span class="visually-hidden">Put right</span></th>
              </tr>
            </thead>
            <tbody>
              @for (s of days(); track s.employee.id + s.day.date) {
                @let d = s.day;
                @let stray = strayPunch(d);
                <tr>
                  <td class="nowrap"><strong>{{ dayName(d.date) }}</strong></td>
                  <td>
                    <strong>{{ s.employee.name }}</strong>
                    <div class="muted small">{{ s.employee.jobTitle }}</div>
                  </td>
                  <td class="nowrap">{{ shift(d) }}</td>
                  <td>
                    <div class="punches">
                      @for (p of d.punches; track p.id) {
                        <span class="punch" [class.ignored]="p.isIgnored" [class.stray]="p.id === stray?.id">
                          <mat-icon>{{ p.source === 'Manual' ? 'edit_calendar' : 'fingerprint' }}</mat-icon>
                          <strong>{{ clock(p.punchedAt, d.date) }}</strong>
                          @if (p.id === stray?.id) {
                            <span class="small">in, never out</span>
                          } @else if (p.isIgnored) {
                            <span class="muted small">ignored</span>
                          }
                        </span>
                      }
                    </div>
                  </td>
                  <td class="num actions">
                    @if (canCorrect() && stray) {
                      <button mat-flat-button (click)="resolve(s, stray)"><mat-icon>logout</mat-icon> Punch-out <span class="inline-ar">الانصراف</span></button>
                      <button mat-button (click)="ignore(s, stray)">Ignore the punch</button>
                    }
                    @if (access.has('Attendance')) {
                      <a mat-icon-button routerLink="/hr/attendance" [queryParams]="{ employee: s.employee.id, month: d.date }" matTooltip="Their month"
                        [attr.aria-label]="s.employee.name + '’s month'">
                        <mat-icon>calendar_month</mat-icon>
                      </a>
                    }
                  </td>
                </tr>
              } @empty {
                @if (!loadingDays()) {
                  <tr>
                    <td colspan="5" class="muted"><mat-icon class="done">task_alt</mat-icon> Nothing waiting: every punch-in since {{ dayName(from()) }} has its punch-out.</td>
                  </tr>
                }
              }
            </tbody>
          </table>
        </div>
        @if (!canCorrect() && days().length) {
          <p class="note">A manager enters the punch-out, or ignores the punch.</p>
        }
      }
    </section>

    @if (lateDays().length) {
      <section class="late" aria-labelledby="late-title">
        <h2 id="late-title">Punches after the pay was finalized <span class="inline-ar">بصمات بعد اعتماد الرواتب</span></h2>
        <p class="note">
          The device kept them while the till was away, and sent them after their month's pay was finalized. The payslip stays as it was: if they
          change what was owed, settle it in a later month's pay, with an incentive or a penalty, then mark them seen to.
        </p>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Day<span class="ar">اليوم</span></th>
                <th>Employee<span class="ar">الموظف</span></th>
                <th>Punches<span class="ar">البصمات</span></th>
                <th>Pay finalized<span class="ar">اعتماد الراتب</span></th>
                @if (canCorrect()) {
                  <th class="num"><span class="visually-hidden">Seen to</span></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (l of lateDays(); track l.key) {
                <tr>
                  <td class="nowrap"><strong>{{ dayName(l.day) }}</strong></td>
                  <td><strong>{{ l.employee.name }}</strong></td>
                  <td>
                    <div class="punches">
                      @for (p of l.punches; track p.id) {
                        <span class="punch"><mat-icon>fingerprint</mat-icon><strong>{{ clock(p.punchedAt, l.day) }}</strong></span>
                      }
                    </div>
                  </td>
                  <td class="muted nowrap">{{ l.finalizedAtUtc | date: 'd MMM, HH:mm' }}</td>
                  @if (canCorrect()) {
                    <td class="num">
                      <button mat-stroked-button (click)="settle(l)"><mat-icon>done</mat-icon> Seen to <span class="inline-ar">تمت المراجعة</span></button>
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }

    <section class="penalties" aria-labelledby="penalties-title">
      <div class="toolbar">
        <h2 id="penalties-title">Administrative penalties <span class="inline-ar">الجزاءات الإدارية</span></h2>
        <span class="spacer"></span>
        <button mat-icon-button (click)="moveMonth(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
        <strong class="month">{{ month() | date: 'MMMM y' }}</strong>
        <button mat-icon-button (click)="moveMonth(1)" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
        @if (canCorrect()) {
          <button mat-flat-button (click)="addPenalty()"><mat-icon>add</mat-icon> Add a penalty <span class="inline-ar">إضافة جزاء</span></button>
        }
      </div>
      <p class="note">
        Hours on a day, with the reason, taken off the month's pay at the employee's hourly rate: a day's pay over nine. One a day; changed or taken
        off until the month's pay is finalized. A disciplinary action's penalty deduction is an amount, taken off as it is: withdrawn on the
        employee's profile, with the action.
      </p>
      @if (loadingPenalties()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (penaltiesError()) {
        <p class="error">{{ penaltiesError() }}</p>
      } @else {
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Day<span class="ar">اليوم</span></th>
                <th>Employee<span class="ar">الموظف</span></th>
                <th class="num">Hours<span class="ar">الساعات</span></th>
                <th class="num">An hour<span class="ar">أجر الساعة</span></th>
                <th class="num">Taken off<span class="ar">الخصم</span></th>
                <th>Why<span class="ar">السبب</span></th>
                <th>Set by<span class="ar">بواسطة</span></th>
                @if (canCorrect()) {
                  <th class="num"><span class="visually-hidden">Change</span></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (p of penalties(); track p.employeeId + p.date + (p.disciplinaryActionId ?? '')) {
                <tr [class.disciplinary]="!!p.disciplinaryActionId">
                  <td class="nowrap">{{ dayName(p.date) }}</td>
                  <td><strong>{{ p.employeeName }}</strong></td>
                  @if (p.disciplinaryActionId) {
                    <td class="num muted">—</td>
                    <td class="num muted">—</td>
                  } @else {
                    <td class="num">{{ p.hours }}h</td>
                    <td class="num">{{ p.hourlyRate | money }}</td>
                  }
                  <td class="num minus">{{ p.amount | money }}</td>
                  <td>
                    @if (p.disciplinaryActionId) {
                      <span class="badge">Disciplinary<span class="inline-ar">تأديبي</span></span>
                    }
                    {{ p.reason }}
                    @if (p.disciplinaryActionId && access.has('Employees')) {
                      <div>
                        <a class="action-link small" [routerLink]="['/hr/employees', p.employeeId]" fragment="performance"
                          [attr.aria-label]="'The disciplinary action on ' + p.employeeName + '’s profile'">
                          <mat-icon>open_in_new</mat-icon> The disciplinary action
                        </a>
                      </div>
                    }
                  </td>
                  <td class="muted">{{ p.setBy }}</td>
                  @if (canCorrect()) {
                    <td class="num">
                      @if (!p.disciplinaryActionId) {
                        <button mat-icon-button (click)="changePenalty(p)" matTooltip="Change or take off" [attr.aria-label]="'Change ' + p.employeeName + '’s penalty on ' + dayName(p.date)">
                          <mat-icon>edit</mat-icon>
                        </button>
                      }
                    </td>
                  }
                </tr>
              } @empty {
                @if (!loadingPenalties()) {
                  <tr>
                    <td [attr.colspan]="canCorrect() ? 8 : 7" class="muted">No penalties in {{ month() | date: 'MMMM y' }}.</td>
                  </tr>
                }
              }
            </tbody>
            @if (penalties().length) {
              <tfoot>
                <tr>
                  <td colspan="2">Total · الإجمالي</td>
                  <td class="num">{{ penaltyTotals().hours }}h</td>
                  <td></td>
                  <td class="num minus">{{ penaltyTotals().amount | money }}</td>
                  <td [attr.colspan]="canCorrect() ? 3 : 2"></td>
                </tr>
              </tfoot>
            }
          </table>
        </div>
      }
    </section>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    h2 {
      margin: 0;
      font: var(--mat-sys-title-medium);
    }
    .penalties,
    .late {
      margin-top: 28px;
    }
    .late h2 {
      margin-bottom: 4px;
    }
    .since {
      width: 170px;
    }
    .month {
      min-width: 130px;
      text-align: center;
      font: var(--mat-sys-title-medium);
    }
    .punches {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 14px;
    }
    .punch {
      display: inline-flex;
      align-items: center;
      gap: 4px;

      mat-icon {
        font-size: 18px;
        width: 18px;
        height: 18px;
        color: var(--mat-sys-on-surface-variant);
      }
      &.ignored strong {
        text-decoration: line-through;
      }
      &.stray {
        padding: 0 8px;
        border-radius: 999px;
        background: color-mix(in srgb, var(--till-warn) 24%, transparent);
      }
    }
    td.actions {
      white-space: nowrap;

      button {
        margin-left: 4px;
      }
    }
    .minus {
      color: var(--mat-sys-error);
    }
    .action-link {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--mat-sys-primary);

      mat-icon {
        font-size: 16px;
        width: 16px;
        height: 16px;
      }
    }
    .badge {
      margin: 0 6px 0 0;
    }
    .done {
      vertical-align: middle;
      color: #2e7d32;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class CorrectionsPage {
  private readonly api = inject(HrApi);
  protected readonly access = inject(HrAccess);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  private readonly correctionsCount = inject(CorrectionsCount);

  protected readonly clock = clock;
  protected readonly dayName = dayName;
  protected readonly shift = shift;
  // A manager's, or an employee's holding the tab in the portal.
  protected readonly canCorrect = computed(() => this.access.canDecide('Corrections'));

  // Since the first of last month, as the count on the tab.
  protected readonly from = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth() - 1, 1)));
  protected readonly month = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly days = signal<EmployeeDay[]>([]);
  protected readonly late = signal<LatePunch[]>([]);
  protected readonly penalties = signal<AdministrativePenalty[]>([]);
  protected readonly employees = signal<Employee[]>([]);
  protected readonly loadingDays = signal(false);
  protected readonly loadingPenalties = signal(false);
  protected readonly daysError = signal<string | null>(null);
  protected readonly penaltiesError = signal<string | null>(null);

  // The late punches by employee and day, as a manager sees to them.
  protected readonly lateDays = computed(() => {
    const days = new Map<string, { key: string; employee: Employee; day: string; finalizedAtUtc: string; punches: Punch[] }>();
    for (const l of this.late()) {
      const key = `${l.employee.id} ${l.day}`;
      const entry = days.get(key) ?? { key, employee: l.employee, day: l.day, finalizedAtUtc: l.finalizedAtUtc, punches: [] };
      entry.punches.push(l.punch);
      days.set(key, entry);
    }
    return [...days.values()];
  });

  protected readonly penaltyTotals = computed(() =>
    this.penalties().reduce((t, p) => ({ hours: Math.round((t.hours + p.hours) * 100) / 100, amount: Math.round((t.amount + p.amount) * 100) / 100 }), {
      hours: 0,
      amount: 0,
    }),
  );

  constructor() {
    void this.start();
  }

  protected pickFrom(day: string): void {
    if (/^\d{4}-\d{2}-\d{2}$/.test(day ?? '')) {
      this.from.set(day);
      void this.loadDays();
    }
  }

  protected moveMonth(by: number): void {
    const start = new Date(`${this.month()}T12:00:00`);
    this.month.set(isoDay(new Date(start.getFullYear(), start.getMonth() + by, 1)));
    void this.loadPenalties();
  }

  // The punch-in left without its punch-out: the day's last punch that counts.
  protected strayPunch(day: AttendanceDay): Punch | undefined {
    return day.punches.filter((p) => !p.isIgnored).at(-1);
  }

  protected async resolve(s: EmployeeDay, stray: Punch): Promise<void> {
    const d = s.day;
    const data: ResolveDayData = {
      employee: s.employee.name,
      date: d.date,
      shift: shift(d),
      shiftEndsAt: d.shiftEndsAt,
      dayEndsAt: d.dayEndsAt,
      punchedIn: stray.punchedAt,
    };
    const answer = await firstValueFrom(
      this.dialog.open<ResolveDayDialog, ResolveDayData, { punchedOutAt: string; reason: string }>(ResolveDayDialog, { data, width: '460px' }).afterClosed(),
    );
    if (!answer) {
      return;
    }
    try {
      const day = await firstValueFrom(this.api.resolveDay(s.employee.id, d.date, answer.punchedOutAt, answer.reason));
      this.notify.info(`Punch-out entered: ${s.employee.name} worked ${duration(day.workedMinutes)} on ${dayName(d.date)}.`);
      await this.loadDays();
    } catch (error) {
      this.notify.error(error, 'The punch-out could not be entered.');
    }
  }

  protected async ignore(s: EmployeeDay, stray: Punch): Promise<void> {
    const reason = await firstValueFrom(
      this.dialog
        .open<IgnorePunchDialog, { time: string }, string>(IgnorePunchDialog, { data: { time: clock(stray.punchedAt, s.day.date) }, width: '420px' })
        .afterClosed(),
    );
    if (reason) {
      await this.act(() => this.api.ignorePunch(stray.id, reason), 'The punch no longer counts.', () => this.loadDays());
    }
  }

  protected async settle(l: { employee: Employee; day: string; punches: Punch[] }): Promise<void> {
    await this.act(
      () => this.api.settleLatePunches(l.punches.map((p) => p.id)),
      `${l.employee.name}'s ${dayName(l.day)}: seen to.`,
      () => this.loadDays(),
    );
  }

  protected async addPenalty(): Promise<void> {
    const first = this.month();
    const start = new Date(`${first}T12:00:00`);
    const last = isoDay(new Date(start.getFullYear(), start.getMonth() + 1, 0));
    const data: PenaltyData = {
      // Whoever is employed in the month.
      employees: this.employees().filter((e) => e.hiredOn <= last && (!e.leftOn || e.leftOn >= first)),
      month: { first, last },
      // A manager's own, one a day; a disciplinary action's is its own beside it.
      taken: this.penalties()
        .filter((p) => !p.disciplinaryActionId)
        .map((p) => ({ employeeId: p.employeeId, date: p.date, hours: p.hours })),
    };
    await this.editPenalty(data, 'Penalty added.');
  }

  protected async changePenalty(p: AdministrativePenalty): Promise<void> {
    const data: PenaltyData = {
      employee: { id: p.employeeId, name: p.employeeName, hourlyRate: p.hourlyRate },
      date: p.date,
      hours: p.hours,
      reason: p.reason,
    };
    await this.editPenalty(data, 'Penalty changed.');
  }

  private async editPenalty(data: PenaltyData, done: string): Promise<void> {
    const answer = await firstValueFrom(
      this.dialog.open<PenaltyDialog, PenaltyData, PenaltyAnswer>(PenaltyDialog, { data, width: '480px' }).afterClosed(),
    );
    if (answer) {
      await this.act(
        () => this.api.setPenalty(answer.employeeId, answer.date, answer.hours, answer.reason),
        answer.hours === null ? 'Penalty taken off.' : done,
        () => this.loadPenalties(),
      );
    }
  }

  // Does it, says so, and reads the list again; a refusal is said as the till says it.
  private async act(call: () => Observable<unknown>, done: string, reload: () => Promise<void>): Promise<void> {
    try {
      await firstValueFrom(call());
      this.notify.info(done);
      await reload();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    }
  }

  private async start(): Promise<void> {
    const today = new Date(`${await this.businessDay.today()}T12:00:00`);
    this.month.set(isoDay(new Date(today.getFullYear(), today.getMonth(), 1)));
    this.from.set(isoDay(new Date(today.getFullYear(), today.getMonth() - 1, 1)));
    try {
      this.employees.set(await firstValueFrom(this.api.employees()));
    } catch {
      this.employees.set([]);
    }
    await Promise.all([this.loadDays(), this.loadPenalties()]);
  }

  private async loadDays(): Promise<void> {
    this.loadingDays.set(true);
    this.daysError.set(null);
    try {
      const [days, late] = await Promise.all([firstValueFrom(this.api.suspendedDays(this.from())), firstValueFrom(this.api.latePunches())]);
      this.days.set(days);
      this.late.set(late);
    } catch (error) {
      this.daysError.set(errorMessage(error, 'The suspended days could not be loaded.'));
    } finally {
      this.loadingDays.set(false);
    }
    void this.correctionsCount.refresh();
  }

  private async loadPenalties(): Promise<void> {
    this.loadingPenalties.set(true);
    this.penaltiesError.set(null);
    try {
      this.penalties.set(await firstValueFrom(this.api.penalties(this.month())));
    } catch (error) {
      this.penaltiesError.set(errorMessage(error, 'The penalties could not be loaded.'));
    } finally {
      this.loadingPenalties.set(false);
    }
  }
}
