import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Observable, firstValueFrom } from 'rxjs';
import { EmployeePicker } from '../../core/hr/employee-picker';
import { Employee, dayName } from '../../core/hr/hr.api';
import { LeaveBalance, LeaveDays, LeaveRequest, LeaveRequestInput, LeaveType, LeaveTypeInput, leaveDays } from '../../core/hr/leaves.api';
import { errorMessage } from '../../core/http/error-message';

const dialogStyles = `
  .wide {
    width: 100%;
  }
  .row {
    display: flex;
    gap: 12px;

    > * {
      flex: 1;
      min-width: 0;
    }
  }
  .ar {
    direction: rtl;
    unicode-bidi: isolate;
    margin-left: 6px;
    opacity: 0.75;
    font-size: 0.8em;
  }
  .muted {
    color: var(--mat-sys-on-surface-variant);
  }
  .small {
    font-size: 0.85em;
  }
  .problem {
    color: var(--mat-sys-error);
  }
  .danger {
    color: var(--mat-sys-error);
    margin-right: auto;
  }
  .facts {
    margin: 0 0 12px;
    padding: 8px 12px;
    border-radius: 8px;
    background: var(--mat-sys-surface-container);
  }
`;

function validDay(day: string | null | undefined): day is string {
  return /^\d{4}-\d{2}-\d{2}$/.test(day ?? '');
}

// ---- Asking for leave ----

export interface LeaveRequestData {
  // HR's: whom it is for, chosen; the portal's: their own, none to choose.
  employees?: Employee[];
  employeeName?: string;
  types: { id: string; name: string; isPaid: boolean }[];
  // The working days the dates would take for them, and their balances the dates' year.
  countDays: (employeeId: string | null, startDate: string, endDate: string) => Observable<LeaveDays>;
  balances: (employeeId: string | null, year: number) => Observable<LeaveBalance[]>;
  submit: (input: Omit<LeaveRequestInput, 'employeeId'> & { employeeId: string | null }) => Observable<LeaveRequest>;
}

// A leave asked for (طلب إجازة): its type, the days it runs and why, the working days it takes
// shown before it is sent, and for a paid type what is left of the balance. Waits for a manager;
// closes with the request made.
@Component({
  selector: 'app-leave-request-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, EmployeePicker],
  template: `
    <h2 mat-dialog-title>Request leave <span class="ar">طلب إجازة</span></h2>
    <mat-dialog-content>
      @if (data.employees) {
        <app-employee-picker [employees]="data.employees" [value]="employeeId()" (valueChange)="pickEmployee($event)" [fill]="true" />
      } @else if (data.employeeName) {
        <p class="muted">For {{ data.employeeName }}</p>
      }
      <mat-form-field class="wide">
        <mat-label>Type · نوع الإجازة</mat-label>
        <mat-select [ngModel]="typeId()" (ngModelChange)="typeId.set($event)" aria-label="Leave type">
          @for (t of data.types; track t.id) {
            <mat-option [value]="t.id">{{ t.name }}{{ t.isPaid ? '' : ' · unpaid' }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <div class="row">
        <mat-form-field>
          <mat-label>Start date · من</mat-label>
          <input matInput type="date" [ngModel]="startDate()" (ngModelChange)="pickStart($event)" aria-label="Start date" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>End date · إلى</mat-label>
          <input matInput type="date" [ngModel]="endDate()" (ngModelChange)="pickEnd($event)" [min]="startDate()" aria-label="End date" />
        </mat-form-field>
      </div>
      @if (counting()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (counted(); as c) {
        <div class="facts" aria-live="polite">
          @if (c.days) {
            <strong>{{ leaveDays(c.days) }}</strong> of work:
            <span class="muted">{{ c.dates.length > 6 ? dayName(c.dates[0]) + ' … ' + dayName(c.dates[c.dates.length - 1]) : names(c.dates) }}</span>
          } @else {
            Those dates hold no working day: their days off and public holidays are not taken.
          }
          @if (balance(); as b) {
            <div class="small" [class.problem]="short()">
              {{ b.remainingDays }} of {{ b.allocatedDays }} days of {{ b.leaveTypeName }} left in {{ b.year }}@if (b.pendingDays) {, {{ b.pendingDays }} asked for already}.
            </div>
          } @else if (type() && !type()!.isPaid) {
            <div class="small muted">Unpaid: a day’s pay off each day, never an absence.</div>
          }
        </div>
      }
      <mat-form-field class="wide">
        <mat-label>Reason · السبب</mat-label>
        <textarea matInput [ngModel]="reason()" (ngModelChange)="reason.set($event)" maxlength="500" rows="2" aria-label="Reason"></textarea>
      </mat-form-field>
      @if (problem(); as why) {
        <p class="problem" role="alert">{{ why }}</p>
      }
      <p class="muted small">A manager approves it. Once approved its days are marked on the attendance{{ type()?.isPaid === false ? '' : ' and taken from the balance' }}.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="send()" [disabled]="sending()">Send the request</button>
    </mat-dialog-actions>
  `,
  styles: dialogStyles,
})
export class LeaveRequestDialog {
  protected readonly data = inject<LeaveRequestData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<LeaveRequestDialog, LeaveRequest>);
  protected readonly dayName = dayName;
  protected readonly leaveDays = leaveDays;

  protected readonly employeeId = signal<string | null>(null);
  protected readonly typeId = signal<string | null>(this.data.types[0]?.id ?? null);
  protected readonly startDate = signal('');
  protected readonly endDate = signal('');
  protected readonly reason = signal('');
  protected readonly counted = signal<LeaveDays | null>(null);
  protected readonly balances = signal<LeaveBalance[]>([]);
  protected readonly counting = signal(false);
  protected readonly sending = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly type = computed(() => this.data.types.find((t) => t.id === this.typeId()) ?? null);
  // The balance of the type chosen, the dates' year: none for an unpaid type.
  protected readonly balance = computed(() => this.balances().find((b) => b.leaveTypeId === this.typeId()) ?? null);
  // More than is left, less what is asked for already.
  protected readonly short = computed(() => {
    const b = this.balance();
    const c = this.counted();
    return !!b && !!c && c.days > b.remainingDays - b.pendingDays;
  });

  private readonly who = computed(() => (this.data.employees ? this.employeeId() : null));
  private balancesYear = 0;

  protected pickEmployee(id: string | null): void {
    this.employeeId.set(id);
    this.balancesYear = 0;
    void this.recount();
  }

  // A one-day leave unless told otherwise: the end follows the start until it is after it.
  protected pickStart(day: string): void {
    const followed = !this.endDate() || this.endDate() === this.startDate() || this.endDate() < day;
    this.startDate.set(day);
    if (followed) {
      this.endDate.set(day);
    }
    void this.recount();
  }

  protected pickEnd(day: string): void {
    this.endDate.set(day);
    void this.recount();
  }

  protected names(dates: string[]): string {
    return dates.map((d) => dayName(d)).join(', ');
  }

  protected async send(): Promise<void> {
    this.problem.set(this.check());
    if (this.problem()) {
      return;
    }
    this.sending.set(true);
    try {
      const made = await firstValueFrom(
        this.data.submit({
          employeeId: this.who(),
          leaveTypeId: this.typeId()!,
          startDate: this.startDate(),
          endDate: this.endDate(),
          reason: this.reason().trim() || null,
        }),
      );
      this.ref.close(made);
    } catch (error) {
      this.problem.set(errorMessage(error, 'The request could not be sent.'));
    } finally {
      this.sending.set(false);
    }
  }

  private check(): string | null {
    if (this.data.employees && !this.employeeId()) {
      return 'Choose whom it is for.';
    }
    if (!this.typeId()) {
      return 'Choose the type of leave.';
    }
    if (!validDay(this.startDate()) || !validDay(this.endDate())) {
      return 'Give the day it starts and the day it ends.';
    }
    if (this.endDate() < this.startDate()) {
      return 'A leave ends on or after the day it starts.';
    }
    if (this.startDate().slice(0, 4) !== this.endDate().slice(0, 4)) {
      return 'A leave runs within one year: ask for each year’s days apart.';
    }
    if (this.counted()?.days === 0) {
      return 'Those dates hold no working day: there is nothing to take.';
    }
    return this.short() ? 'That is more than is left of the balance, less what is asked for already.' : null;
  }

  // The working days, and the balances of the dates' year, once both dates are given.
  private async recount(): Promise<void> {
    this.problem.set(null);
    const start = this.startDate();
    const end = this.endDate();
    if ((this.data.employees && !this.employeeId()) || !validDay(start) || !validDay(end) || end < start) {
      this.counted.set(null);
      return;
    }
    this.counting.set(true);
    try {
      const year = Number(start.slice(0, 4));
      if (year !== this.balancesYear) {
        this.balances.set(await firstValueFrom(this.data.balances(this.who(), year)));
        this.balancesYear = year;
      }
      const days = await firstValueFrom(this.data.countDays(this.who(), start, end));
      if (start === this.startDate() && end === this.endDate()) {
        this.counted.set(days);
      }
    } catch {
      this.counted.set(null);
    } finally {
      this.counting.set(false);
    }
  }
}

// ---- Rejecting ----

// Why a request is rejected, optional: told to the employee in the portal. Closes with the notes
// ('' for none), or undefined when not rejected after all.
@Component({
  selector: 'app-reject-leave-dialog',
  imports: [DatePipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Reject {{ data.employeeName }}’s leave? <span class="ar">رفض الإجازة</span></h2>
    <mat-dialog-content>
      <p class="facts">
        {{ data.leaveTypeName }} · {{ data.startDate | date: 'EEE d MMM' }}{{ data.endDate !== data.startDate ? ' – ' + (data.endDate | date: 'EEE d MMM') : '' }} ·
        {{ data.days }} {{ data.days === 1 ? 'day' : 'days' }}
      </p>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <textarea matInput [(ngModel)]="notes" maxlength="500" rows="2" placeholder="Optional: they see it in the portal" aria-label="Why"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Keep it waiting</button>
      <button mat-flat-button class="reject" [mat-dialog-close]="notes.trim()">Reject</button>
    </mat-dialog-actions>
  `,
  styles: `
    ${dialogStyles}
    .reject {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
  `,
})
export class RejectLeaveDialog {
  protected readonly data = inject<LeaveRequest>(MAT_DIALOG_DATA);
  protected notes = '';
}

// ---- Cancelling an approved leave ----

export interface CancelLeaveData {
  request: LeaveRequest;
  today: string;
  // The working days from a day to the leave's end: those given back.
  countDays: (from: string) => Observable<LeaveDays>;
}

// An approved leave cancelled from a day on (the business day unless changed): the days from then
// given back to the balance and unmarked, those before staying taken. From its first day or
// before, none of it stands. Closes with the day and why.
@Component({
  selector: 'app-cancel-leave-dialog',
  imports: [DatePipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    @let r = data.request;
    <h2 mat-dialog-title>Cancel {{ r.employeeName }}’s leave <span class="ar">إلغاء الإجازة</span></h2>
    <mat-dialog-content>
      <p class="facts">
        {{ r.leaveTypeName }} · {{ r.startDate | date: 'EEE d MMM' }}{{ r.endDate !== r.startDate ? ' – ' + (r.endDate | date: 'EEE d MMM') : '' }} ·
        {{ r.days }} {{ r.days === 1 ? 'day' : 'days' }}
      </p>
      <mat-form-field class="wide">
        <mat-label>Cancelled from · اعتبارًا من</mat-label>
        <input matInput type="date" [ngModel]="from()" (ngModelChange)="pick($event)" [max]="r.endDate" aria-label="Cancelled from" />
      </mat-form-field>
      <p aria-live="polite">
        @if (whole()) {
          <strong>All of it</strong> is cancelled: none of its {{ r.days }} {{ r.days === 1 ? 'day' : 'days' }} is taken.
        } @else if (after()) {
          <span class="problem">It ended on {{ r.endDate | date: 'd MMM' }}: choose a day of it.</span>
        } @else if (givenBack() !== null) {
          @let kept = r.days - givenBack()!;
          <strong>{{ givenBack() }} {{ givenBack() === 1 ? 'day' : 'days' }}</strong> given back{{ r.isPaid ? ' to the balance' : '' }};
          {{ kept === 1 ? 'the day' : 'the ' + kept + ' days' }} before {{ from() | date: 'd MMM' }} {{ kept === 1 ? 'stays' : 'stay' }} taken.
        }
      </p>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <textarea matInput [(ngModel)]="notes" maxlength="500" rows="2" placeholder="Optional: called back to work" aria-label="Why"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Keep it</button>
      <button mat-flat-button [disabled]="after() || !from()" [mat-dialog-close]="{ from: from(), notes: notes.trim() || null }">Cancel the leave</button>
    </mat-dialog-actions>
  `,
  styles: dialogStyles,
})
export class CancelLeaveDialog {
  protected readonly data = inject<CancelLeaveData>(MAT_DIALOG_DATA);
  private readonly r = this.data.request;
  // The business day, within the leave.
  protected readonly from = signal(this.data.today < this.r.startDate ? this.r.startDate : this.data.today > this.r.endDate ? this.r.endDate : this.data.today);
  protected readonly givenBack = signal<number | null>(null);
  protected notes = '';

  protected readonly whole = computed(() => validDay(this.from()) && this.from() <= this.r.startDate);
  protected readonly after = computed(() => validDay(this.from()) && this.from() > this.r.endDate);

  constructor() {
    void this.count();
  }

  protected pick(day: string): void {
    this.from.set(day);
    void this.count();
  }

  private async count(): Promise<void> {
    const from = this.from();
    this.givenBack.set(null);
    if (!validDay(from) || this.whole() || this.after()) {
      return;
    }
    try {
      const days = await firstValueFrom(this.data.countDays(from));
      if (from === this.from()) {
        this.givenBack.set(Math.min(days.days, this.r.days));
      }
    } catch {
      this.givenBack.set(null);
    }
  }
}

// ---- A balance's days ----

// A balance's days in a year, a manager's: whole or half days, never below what is taken.
// Closes with the days.
@Component({
  selector: 'app-leave-balance-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.employeeName }}’s {{ data.leaveTypeName }}, {{ data.year }} <span class="ar">رصيد الإجازة</span></h2>
    <mat-dialog-content>
      <p class="facts">
        {{ data.usedDays }} taken · {{ data.pendingDays }} asked for · {{ data.remainingDays }} left of {{ data.allocatedDays }}
        @if (!data.opened) {
          <span class="muted small"><br />The year’s days as it would open, not kept yet.</span>
        } @else if (data.allocatedBy) {
          <span class="muted small"><br />Last set by {{ data.allocatedBy }}.</span>
        }
      </p>
      <mat-form-field class="wide">
        <mat-label>Days in {{ data.year }} · أيام الرصيد</mat-label>
        <input matInput type="number" step="0.5" [min]="data.usedDays" max="365" [(ngModel)]="days" aria-label="Days" />
        <mat-hint>Whole or half days, {{ data.usedDays }} at least</mat-hint>
      </mat-form-field>
      @if (problem(); as why) {
        <p class="problem" role="alert">{{ why }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()">Save</button>
    </mat-dialog-actions>
  `,
  styles: dialogStyles,
})
export class LeaveBalanceDialog {
  protected readonly data = inject<LeaveBalance>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<LeaveBalanceDialog, number>);
  protected days: number | null = this.data.allocatedDays;
  protected readonly problem = signal<string | null>(null);

  protected save(): void {
    const days = Number(this.days);
    if (this.days === null || !Number.isFinite(days) || days < 0 || days > 365) {
      this.problem.set('Give the days, 0 to 365.');
    } else if (!Number.isInteger(days * 2)) {
      this.problem.set('Give a balance in whole or half days: 21, 10.5.');
    } else if (days < this.data.usedDays) {
      this.problem.set(`${this.data.usedDays} of its days are taken already: give at least that many.`);
    } else {
      this.ref.close(days);
    }
  }
}

// ---- A leave type ----

export type LeaveTypeAnswer = LeaveTypeInput | { remove: true };

// A leave type (نوع إجازة), a manager's: its name, paid or not, and a paid one's days a year,
// shared for new hires or not. One in use keeps whether it is paid, and is not taken off.
@Component({
  selector: 'app-leave-type-dialog',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Change ' + data.name : 'Add a leave type' }} <span class="ar">نوع الإجازة</span></h2>
    <mat-dialog-content>
      <mat-form-field class="wide">
        <mat-label>Name · الاسم</mat-label>
        <input matInput [(ngModel)]="name" maxlength="100" placeholder="Maternity (وضع)" aria-label="Name" />
      </mat-form-field>
      <mat-slide-toggle [(ngModel)]="isPaid" [disabled]="!!data?.inUse" aria-label="Paid">Paid · مدفوعة الأجر</mat-slide-toggle>
      @if (data?.inUse) {
        <p class="muted small">In use: whether it is paid stays as it is.</p>
      }
      @if (isPaid) {
        <mat-form-field class="wide days">
          <mat-label>Days a year · أيام في السنة</mat-label>
          <input matInput type="number" min="0" max="365" step="1" [(ngModel)]="days" aria-label="Days a year" />
        </mat-form-field>
        <mat-checkbox [(ngModel)]="prorated">A new hire’s share in their first year · نسبة للمعيّن الجديد</mat-checkbox>
        <p class="muted small">By the days of the year left from their first day, to the half day: joining on 2 July is 10.5 of 21.</p>
      } @else {
        <p class="muted small">No balance: each day is a day’s pay off, never an absence.</p>
      }
      @if (problem(); as why) {
        <p class="problem" role="alert">{{ why }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (data && !data.inUse) {
        <button mat-button class="danger" [mat-dialog-close]="{ remove: true }">Take it off</button>
      }
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    ${dialogStyles}
    .days {
      margin-top: 16px;
    }
  `,
})
export class LeaveTypeDialog {
  protected readonly data = inject<LeaveType | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<LeaveTypeDialog, LeaveTypeAnswer>);
  protected name = this.data?.name ?? '';
  protected isPaid = this.data?.isPaid ?? true;
  protected days: number | null = this.data?.defaultDaysPerYear ?? 0;
  protected prorated = this.data?.proratedForNewHires ?? false;
  protected readonly problem = signal<string | null>(null);

  protected save(): void {
    const days = this.isPaid ? Number(this.days) : 0;
    if (!this.name.trim()) {
      this.problem.set('Give the type its name.');
    } else if (!Number.isInteger(days) || days < 0 || days > 365) {
      this.problem.set('Give its days a year, 0 to 365.');
    } else {
      this.ref.close({ name: this.name.trim(), isPaid: this.isPaid, defaultDaysPerYear: days, proratedForNewHires: this.isPaid && this.prorated });
    }
  }
}
