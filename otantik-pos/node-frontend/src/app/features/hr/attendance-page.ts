import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import {
  AttendanceDay,
  AttendanceDayReport,
  AttendancePolicy,
  DayMarkKind,
  Employee,
  HrApi,
  Punch,
  Timesheet,
  clock,
  dayStatusLabels,
  duration,
} from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay, isoDay } from '../costing/financials';
import { AddPunchData, AddPunchDialog, IgnorePunchDialog, MarkDayData, MarkDayDialog } from './attendance-dialogs';

type Mode = 'day' | 'employee';

// A row of the table: an employee's day, as everyone's day lists them or one employee's month does.
interface Row {
  key: string;
  title: string;
  sub: string;
  employee: Employee;
  day: AttendanceDay;
}

// Attendance (الحضور والانصراف): everyone's day, or one employee's month, as worked out from the
// device's punches against each one's nine-hour shift. Each day opens to show its punches. A
// manager puts a day right there: a punch the device missed, one that should not count, a day of
// leave or off. Punches under a number nobody has yet are listed under the day, waiting for the
// number to be given to someone on the Employees tab.
@Component({
  selector: 'app-attendance-page',
  imports: [
    DatePipe,
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MatTooltipModule,
  ],
  template: `
    <div class="toolbar">
      <mat-button-toggle-group [value]="mode()" (change)="setMode($event.value)" aria-label="Show">
        <mat-button-toggle value="day">By day · حسب اليوم</mat-button-toggle>
        <mat-button-toggle value="employee">By employee · حسب الموظف</mat-button-toggle>
      </mat-button-toggle-group>
      @if (mode() === 'day') {
        <button mat-icon-button (click)="moveDay(-1)" aria-label="The day before"><mat-icon>chevron_left</mat-icon></button>
        <mat-form-field subscriptSizing="dynamic" class="day">
          <mat-label>Day · اليوم</mat-label>
          <input matInput type="date" [ngModel]="day()" (ngModelChange)="pickDay($event)" aria-label="Day" />
        </mat-form-field>
        <button mat-icon-button (click)="moveDay(1)" aria-label="The day after"><mat-icon>chevron_right</mat-icon></button>
      } @else {
        <mat-form-field subscriptSizing="dynamic" class="who">
          <mat-label>Employee · الموظف</mat-label>
          <mat-select [ngModel]="employeeId()" (ngModelChange)="pickEmployee($event)" aria-label="Employee">
            @for (e of employees(); track e.id) {
              <mat-option [value]="e.id">{{ e.name }}@if (!e.isActive) { <span class="muted">· left</span> }</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <button mat-icon-button (click)="moveMonth(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
        <strong class="month">{{ month() | date: 'MMMM y' }}</strong>
        <button mat-icon-button (click)="moveMonth(1)" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    }

    @if (totals().length) {
      <div class="totals" aria-label="Totals">
        @for (t of totals(); track t.label) {
          <span class="chip" [class]="t.tone"><strong>{{ t.value }}</strong> {{ t.label }}</span>
        }
      </div>
    }

    @if (mode() === 'employee' && !employeeId()) {
      <p class="muted">Choose an employee to see their month.</p>
    } @else if (rows().length || !loading()) {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>{{ mode() === 'day' ? 'Employee' : 'Day' }}<span class="ar">{{ mode() === 'day' ? 'الموظف' : 'اليوم' }}</span></th>
              <th>Shift<span class="ar">الوردية</span></th>
              <th>In<span class="ar">حضور</span></th>
              <th>Out<span class="ar">انصراف</span></th>
              <th class="num">Worked<span class="ar">ساعات العمل</span></th>
              <th class="num">Late<span class="ar">تأخير</span></th>
              <th class="num">Left early<span class="ar">انصراف مبكر</span></th>
              <th class="num">Away<span class="ar">غياب أثناء الوردية</span></th>
              <th class="num">Overtime<span class="ar">إضافي</span></th>
              <th>Status<span class="ar">الحالة</span></th>
              <th class="num"><span class="visually-hidden">Open</span></th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.key) {
              @let d = r.day;
              <tr class="clickable" [class]="'status-' + d.status" (click)="toggle(r.key)" [attr.aria-expanded]="open().has(r.key)">
                <td>
                  <strong>{{ r.title }}</strong>
                  @if (r.sub) {
                    <div class="muted small">{{ r.sub }}</div>
                  }
                </td>
                <td class="nowrap">{{ clock(d.shiftStartsAt) }}–{{ clock(d.shiftEndsAt, d.date) }}</td>
                <td class="nowrap">{{ clock(d.firstIn, d.date) }}</td>
                <td class="nowrap">{{ clock(d.lastOut, d.date) }}</td>
                <td class="num">{{ duration(d.workedMinutes) }}</td>
                <td class="num" [class.bad]="d.lateMinutes > 0">{{ duration(d.lateMinutes) }}</td>
                <td class="num" [class.bad]="d.earlyLeaveMinutes > 0">{{ duration(d.earlyLeaveMinutes) }}</td>
                <td class="num" [class.bad]="d.awayMinutes > 0">{{ duration(d.awayMinutes) }}</td>
                <td class="num" [class.good]="d.overtimeMinutes > 0">{{ duration(d.overtimeMinutes) }}</td>
                <td>
                  <span class="status" [class]="d.status">{{ labels[d.status].en }}<span class="ar">{{ labels[d.status].ar }}</span></span>
                  @if (d.mark) {
                    <div class="muted small">{{ markLabels[d.mark] }}@if (d.markNotes) { · {{ d.markNotes }}}</div>
                  }
                  @if (d.penaltyHours) {
                    <div class="small bad-text">Penalty {{ d.penaltyHours }}h: {{ d.penaltyReason }}</div>
                  }
                </td>
                <td class="num"><mat-icon class="chevron">{{ open().has(r.key) ? 'expand_less' : 'expand_more' }}</mat-icon></td>
              </tr>
              @if (open().has(r.key)) {
                <tr class="detail">
                  <td colspan="11">
                    <div class="punches" aria-label="Punches">
                      @for (p of d.punches; track p.id) {
                        <span class="punch" [class.ignored]="p.isIgnored">
                          <mat-icon>{{ p.source === 'Manual' ? 'edit_calendar' : 'fingerprint' }}</mat-icon>
                          <strong>{{ clock(p.punchedAt, d.date) }}</strong>
                          <span class="muted small">
                            @if (p.source === 'Manual') {
                              by {{ p.recordedBy }}: {{ p.reason }}
                            } @else {
                              device no. {{ p.deviceUserId }}
                            }
                            @if (p.isIgnored) {
                              · ignored by {{ p.ignoredBy }}: {{ p.ignoreReason }}
                            }
                          </span>
                          @if (canCorrect()) {
                            @if (p.isIgnored) {
                              <button mat-button (click)="restore(p)">Count it</button>
                            } @else {
                              <button mat-icon-button (click)="ignore(p, d)" matTooltip="Ignore it" [attr.aria-label]="'Ignore the punch at ' + clock(p.punchedAt)">
                                <mat-icon>block</mat-icon>
                              </button>
                            }
                          }
                        </span>
                      } @empty {
                        <span class="muted">No punches.</span>
                      }
                    </div>
                    @if (canCorrect() && d.status !== 'NotEmployed') {
                      <div class="actions">
                        <button mat-stroked-button (click)="addPunch(r)"><mat-icon>more_time</mat-icon> Add a punch <span class="inline-ar">إضافة بصمة</span></button>
                        <button mat-stroked-button (click)="markDay(r)"><mat-icon>event_available</mat-icon> Leave or day off <span class="inline-ar">إجازة أو راحة</span></button>
                      </div>
                    }
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="11" class="muted">{{ mode() === 'day' ? 'No one is employed on this day.' : 'Nothing to show.' }}</td></tr>
            }
          </tbody>
        </table>
      </div>
    }

    @if (mode() === 'day' && unassigned().length) {
      <section class="unassigned">
        <h3>Punches under a number nobody has <span class="inline-ar">بصمات بأرقام غير مسجلة</span></h3>
        <p class="muted small">Give the number to an employee on the Employees tab, and their punches count from then.</p>
        @for (p of unassigned(); track p.id) {
          <span class="punch"><mat-icon>fingerprint</mat-icon> no. <strong>{{ p.deviceUserId }}</strong> at {{ clock(p.punchedAt) }}</span>
        }
      </section>
    }

    @if (policy(); as p) {
      <p class="note">
        Each shift is nine hours. Late counts from the shift's start{{ p.graceMinutes ? ', once past ' + p.graceMinutes + ' minutes’ grace' : ': no grace is set' }};
        overtime is time worked after the shift ends;
        scans within {{ p.doubleScanMinutes }} minutes of each other are one. A day ends six hours before the next shift starts, so a shift past
        midnight is one day.
      </p>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .day {
      width: 170px;
    }
    .who {
      min-width: 220px;
    }
    .month {
      min-width: 130px;
      text-align: center;
      font: var(--mat-sys-title-medium);
    }
    .totals {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 8px 0 12px;
    }
    .chip {
      padding: 4px 10px;
      border-radius: 999px;
      background: var(--mat-sys-surface-container);

      &.bad {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
      }
      &.warn {
        background: color-mix(in srgb, var(--till-warn) 18%, transparent);
      }
      &.good {
        background: color-mix(in srgb, #2e7d32 14%, transparent);
      }
    }
    .status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);

      &.Worked {
        background: color-mix(in srgb, #2e7d32 16%, transparent);
      }
      &.WorkedOnDayOff {
        background: color-mix(in srgb, var(--mat-sys-primary) 16%, transparent);
      }
      &.Suspended {
        background: color-mix(in srgb, var(--till-warn) 24%, transparent);
      }
      &.Absent {
        background: color-mix(in srgb, var(--mat-sys-error) 16%, transparent);
        color: var(--mat-sys-error);
      }
    }
    tr.status-NotEmployed td,
    tr.status-DayOff td,
    tr.status-Leave td {
      color: var(--mat-sys-on-surface-variant);
    }
    td.bad,
    .bad-text {
      color: var(--mat-sys-error);
    }
    td.good {
      color: #2e7d32;
    }
    tr.detail td {
      background: var(--mat-sys-surface-container-lowest);
    }
    .punches {
      display: flex;
      flex-wrap: wrap;
      gap: 6px 16px;
      align-items: center;
    }
    .punch {
      display: inline-flex;
      align-items: center;
      gap: 6px;

      mat-icon {
        font-size: 18px;
        width: 18px;
        height: 18px;
        color: var(--mat-sys-on-surface-variant);
      }
      &.ignored strong {
        text-decoration: line-through;
      }
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 8px;
    }
    .unassigned {
      margin-top: 16px;

      h3 {
        margin: 0;
        font: var(--mat-sys-title-small);
      }
      .punch {
        margin-right: 16px;
      }
    }
    .chevron {
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
export class AttendancePage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);
  private readonly businessDay = inject(BusinessDay);

  // ?date=, or ?employee=&month= (its first day), to open on them.
  readonly date = input<string>();
  readonly employee = input<string>();
  readonly startMonth = input<string>(undefined, { alias: 'month' });

  protected readonly labels = dayStatusLabels;
  protected readonly markLabels: Record<DayMarkKind, string> = {
    Leave: 'Marked leave',
    DayOff: 'Marked day off',
    WorkingDay: 'Marked a working day: a swap',
  };
  protected readonly clock = clock;
  protected readonly duration = duration;
  protected readonly canCorrect = this.approvals.canApprove;

  protected readonly mode = signal<Mode>('day');
  protected readonly day = signal(isoDay(new Date()));
  protected readonly employeeId = signal<string | null>(null);
  protected readonly month = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly employees = signal<Employee[]>([]);
  protected readonly report = signal<AttendanceDayReport | null>(null);
  protected readonly sheet = signal<Timesheet | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly open = signal(new Set<string>());

  protected readonly policy = computed<AttendancePolicy | null>(() => (this.mode() === 'day' ? this.report()?.policy : this.sheet()?.policy) ?? null);
  protected readonly unassigned = computed<Punch[]>(() => this.report()?.unassigned ?? []);

  protected readonly rows = computed<Row[]>(() => {
    if (this.mode() === 'day') {
      return (this.report()?.employees ?? []).map((e) => ({ key: e.employee.id, title: e.employee.name, sub: e.employee.jobTitle, employee: e.employee, day: e.day }));
    }
    const sheet = this.sheet();
    if (!sheet) {
      return [];
    }
    return sheet.days.map((d) => ({
      key: d.date,
      title: new Date(`${d.date}T12:00:00`).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' }),
      sub: d.isWeeklyDayOff ? 'Weekly day off · راحة أسبوعية' : '',
      employee: sheet.employee,
      day: d,
    }));
  });

  protected readonly totals = computed<{ label: string; value: string; tone: string }[]>(() => {
    if (this.mode() === 'day') {
      const t = this.report()?.totals;
      return t
        ? [
            { label: 'came in · حضور', value: String(t.present), tone: 'good' },
            { label: 'late · تأخير', value: String(t.late), tone: t.late ? 'warn' : '' },
            { label: 'absent · غياب', value: String(t.absent), tone: t.absent ? 'bad' : '' },
            { label: 'suspended · موقوف للمراجعة', value: String(t.missingPunch), tone: t.missingPunch ? 'warn' : '' },
            { label: 'off or on leave · راحة وإجازات', value: String(t.offOrOnLeave), tone: '' },
            { label: 'overtime · إضافي', value: duration(t.overtimeMinutes), tone: '' },
          ]
        : [];
    }
    const t = this.sheet()?.totals;
    return t
      ? [
          { label: 'days worked · أيام العمل', value: String(t.daysWorked), tone: 'good' },
          { label: 'worked · ساعات العمل', value: duration(t.workedMinutes), tone: '' },
          { label: 'late, early and away · تأخير', value: duration(t.lateMinutes + t.earlyLeaveMinutes + t.awayMinutes), tone: t.lateMinutes + t.earlyLeaveMinutes + t.awayMinutes ? 'warn' : '' },
          { label: 'overtime · إضافي', value: duration(t.overtimeMinutes), tone: '' },
          { label: 'absent · غياب', value: String(t.absentDays), tone: t.absentDays ? 'bad' : '' },
          { label: 'suspended · موقوف للمراجعة', value: String(t.missingPunchDays), tone: t.missingPunchDays ? 'warn' : '' },
        ]
      : [];
  });

  constructor() {
    // What the address asks for, once; then the business day.
    effect(() => {
      const [date, employee, month] = [this.date(), this.employee(), this.startMonth()];
      untracked(() => {
        if (employee) {
          this.mode.set('employee');
          this.employeeId.set(employee);
          if (month && /^\d{4}-\d{2}-\d{2}$/.test(month)) {
            this.month.set(`${month.slice(0, 8)}01`);
          }
        } else if (date && /^\d{4}-\d{2}-\d{2}$/.test(date)) {
          this.day.set(date);
        }
        void this.start(!!date || !!employee);
      });
    });
  }

  protected setMode(mode: Mode): void {
    this.mode.set(mode);
    this.open.set(new Set());
    void this.load();
  }

  protected pickDay(day: string): void {
    if (/^\d{4}-\d{2}-\d{2}$/.test(day ?? '')) {
      this.day.set(day);
      void this.load();
    }
  }

  protected moveDay(by: number): void {
    const next = new Date(`${this.day()}T12:00:00`);
    next.setDate(next.getDate() + by);
    this.pickDay(isoDay(next));
  }

  protected pickEmployee(id: string): void {
    this.employeeId.set(id);
    void this.load();
  }

  protected moveMonth(by: number): void {
    const start = new Date(`${this.month()}T12:00:00`);
    this.month.set(isoDay(new Date(start.getFullYear(), start.getMonth() + by, 1)));
    void this.load();
  }

  protected toggle(key: string): void {
    this.open.update((open) => {
      const next = new Set(open);
      if (!next.delete(key)) {
        next.add(key);
      }
      return next;
    });
  }

  protected async addPunch(row: Row): Promise<void> {
    const data: AddPunchData = {
      employee: row.employee.name,
      day: row.day.date,
      shift: `${clock(row.day.shiftStartsAt)}–${clock(row.day.shiftEndsAt, row.day.date)}`,
    };
    const answer = await firstValueFrom(
      this.dialog.open<AddPunchDialog, AddPunchData, { punchedAt: string; reason: string }>(AddPunchDialog, { data, width: '440px' }).afterClosed(),
    );
    if (answer) {
      await this.act(() => this.api.addPunch(row.employee.id, answer.punchedAt, answer.reason), 'Punch added.');
    }
  }

  protected async ignore(punch: Punch, day: AttendanceDay): Promise<void> {
    const reason = await firstValueFrom(
      this.dialog.open<IgnorePunchDialog, { time: string }, string>(IgnorePunchDialog, { data: { time: clock(punch.punchedAt, day.date) }, width: '420px' }).afterClosed(),
    );
    if (reason) {
      await this.act(() => this.api.ignorePunch(punch.id, reason), 'The punch no longer counts.');
    }
  }

  protected async restore(punch: Punch): Promise<void> {
    await this.act(() => this.api.restorePunch(punch.id), 'The punch counts again.');
  }

  protected async markDay(row: Row): Promise<void> {
    const data: MarkDayData = {
      employee: row.employee.name,
      day: new Date(`${row.day.date}T12:00:00`).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' }),
      mark: row.day.mark,
      notes: row.day.markNotes,
      weeklyDayOff: row.day.isWeeklyDayOff,
    };
    const answer = await firstValueFrom(
      this.dialog
        .open<MarkDayDialog, MarkDayData, { kind: DayMarkKind | 'None'; notes: string | null }>(MarkDayDialog, { data, width: '440px' })
        .afterClosed(),
    );
    if (answer) {
      const kind = answer.kind === 'None' ? null : answer.kind;
      await this.act(() => this.api.markDay(row.employee.id, row.day.date, kind, kind ? answer.notes : null), 'Saved.');
    }
  }

  // Does it, says so, and reads the day or month again; a refusal is said as the till says it.
  private async act(call: () => Observable<unknown>, done: string): Promise<void> {
    try {
      await firstValueFrom(call());
      this.notify.info(done);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    }
  }

  private async start(fromAddress: boolean): Promise<void> {
    if (!fromAddress) {
      this.day.set(await this.businessDay.today());
    }
    try {
      this.employees.set((await firstValueFrom(this.api.employees())).sort((a, b) => Number(b.isActive) - Number(a.isActive)));
    } catch {
      this.employees.set([]);
    }
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      if (this.mode() === 'day') {
        this.report.set(await firstValueFrom(this.api.attendance(this.day())));
      } else if (this.employeeId()) {
        this.sheet.set(await firstValueFrom(this.api.timesheet(this.employeeId()!, this.month())));
      } else {
        this.sheet.set(null);
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The attendance could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
