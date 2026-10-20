import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { DayMarkKind, Employee, clock, dayName, duration, minutesBetween } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

const styles = `
  .wide {
    width: 100%;
  }
  .row {
    display: flex;
    gap: 12px;
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
  mat-radio-group {
    display: grid;
    gap: 4px;
    margin-bottom: 12px;
  }
  .grow {
    flex: 1;
  }
  .problem {
    color: var(--mat-sys-error);
  }
  .shortcut {
    margin-bottom: 12px;
  }
  .cost {
    margin: 16px 0 0;
    white-space: nowrap;
  }
  .danger {
    color: var(--mat-sys-error);
    margin-right: auto;
  }
`;

export interface AddPunchData {
  employee: string;
  // The day it is for, and when its shift runs: a punch after midnight is on the next date.
  day: string;
  shift: string;
}

// A punch the device missed, entered by a manager: when, on the restaurant's clock, and why.
// Closes with the time ('2026-10-19T20:00:00') and the reason.
@Component({
  selector: 'app-add-punch-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Add a punch for {{ data.employee }} <span class="ar">إضافة بصمة</span></h2>
    <mat-dialog-content>
      <p class="muted">The device missed it: a forgotten scan, the device down. Their shift runs {{ data.shift }}; a punch after midnight is on the next date.</p>
      <div class="row">
        <mat-form-field>
          <mat-label>Date · التاريخ</mat-label>
          <input matInput type="date" [(ngModel)]="date" aria-label="Date" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Time · الوقت</mat-label>
          <input matInput type="time" [(ngModel)]="time" aria-label="Time" />
        </mat-form-field>
      </div>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <input matInput [(ngModel)]="reason" maxlength="300" placeholder="Forgot to scan out" aria-label="Why" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!ready()" [mat-dialog-close]="{ punchedAt: date + 'T' + time + ':00', reason: reason.trim() }">Add punch</button>
    </mat-dialog-actions>
  `,
  styles,
})
export class AddPunchDialog {
  protected readonly data = inject<AddPunchData>(MAT_DIALOG_DATA);
  protected date = this.data.day;
  protected time = '';
  protected reason = '';

  protected ready(): boolean {
    return !!this.date && /^\d{2}:\d{2}$/.test(this.time) && !!this.reason.trim();
  }
}

// Why a punch should not count: scanned twice, a test. Closes with the reason.
@Component({
  selector: 'app-ignore-punch-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Ignore the punch at {{ data.time }} <span class="ar">تجاهل البصمة</span></h2>
    <mat-dialog-content>
      <p class="muted">It is kept, marked, and no longer counted. It can be counted again.</p>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <input matInput [(ngModel)]="reason" maxlength="300" placeholder="Scanned twice" aria-label="Why" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!reason.trim()" [mat-dialog-close]="reason.trim()">Ignore it</button>
    </mat-dialog-actions>
  `,
  styles,
})
export class IgnorePunchDialog {
  protected readonly data = inject<{ time: string }>(MAT_DIALOG_DATA);
  protected reason = '';
}

export interface MarkDayData {
  employee: string;
  day: string;
  mark: DayMarkKind | null;
  notes: string | null;
  // A weekly day off: one that can be marked a working day, worked in exchange for another.
  weeklyDayOff?: boolean;
}

// A day marked as leave or a day off (not an absence), a weekly day off marked as a working day (a
// swap: a normal shift, and no leave allowance), or the mark taken off. Closes with the kind
// ('None' to take it off) and the notes.
@Component({
  selector: 'app-mark-day-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>{{ data.employee }}, {{ data.day }} <span class="ar">تمييز اليوم</span></h2>
    <mat-dialog-content>
      <mat-radio-group [(ngModel)]="kind" aria-label="The day is">
        <mat-radio-button value="Leave">Leave, paid <span class="ar">إجازة</span></mat-radio-button>
        <mat-radio-button value="DayOff">A day off: swapped, or extra <span class="ar">راحة</span></mat-radio-button>
        @if (data.weeklyDayOff) {
          <mat-radio-button value="WorkingDay">A working day: their day off, worked in exchange for another <span class="ar">يوم عمل بدل راحة</span></mat-radio-button>
        }
        <mat-radio-button value="None">No mark: {{ data.weeklyDayOff ? 'their day off, with a leave allowance if they work it' : 'absent if there are no punches' }} <span class="ar">بدون</span></mat-radio-button>
      </mat-radio-group>
      @if (kind !== 'None') {
        <mat-form-field class="wide">
          <mat-label>Notes · ملاحظات</mat-label>
          <input matInput [(ngModel)]="notes" maxlength="300" aria-label="Notes" />
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [mat-dialog-close]="{ kind, notes: notes.trim() || null }">Save</button>
    </mat-dialog-actions>
  `,
  styles,
})
export class MarkDayDialog {
  protected readonly data = inject<MarkDayData>(MAT_DIALOG_DATA);
  protected kind: DayMarkKind | 'None' = this.data.mark ?? 'Leave';
  protected notes = this.data.notes ?? '';
}

// A punch-in left this long without its punch-out suspends the day.
const abandonedAfterMinutes = 18 * 60;

// A punch-in left 18 hours without its punch-out (a suspended day), and the shift it was for.
export interface ResolveDayData {
  employee: string;
  date: string;
  shift: string;
  shiftEndsAt: string;
  punchedIn: string;
}

// The punch-out a manager enters for a suspended day: when they left, on the restaurant's clock,
// after the punch-in, within 18 hours of it and before the next day starts (nine hours after the
// shift ends), and why it is entered. Closes with the time
// ('2026-10-20T01:00:00') and the reason.
@Component({
  selector: 'app-resolve-day-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.employee }}’s punch-out, {{ day }} <span class="ar">بصمة الانصراف</span></h2>
    <mat-dialog-content>
      <p class="muted">In at <strong>{{ inAt }}</strong> and never out; their shift runs {{ data.shift }}. Enter when they left, and the day is worked out from it.</p>
      @if (shiftEndFits) {
        <button mat-stroked-button type="button" class="shortcut" (click)="atShiftEnd()">At the shift’s end, {{ endsAt }}</button>
      }
      <div class="row">
        <mat-form-field>
          <mat-label>Date · التاريخ</mat-label>
          <input matInput type="date" [(ngModel)]="date" aria-label="Date" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Time · الوقت</mat-label>
          <input matInput type="time" [(ngModel)]="time" aria-label="Time" />
        </mat-form-field>
      </div>
      @if (problem(); as why) {
        <p class="problem">{{ why }}</p>
      } @else if (after() !== null) {
        <p>{{ duration(after()!) }} after the punch-in.</p>
      }
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <input matInput [(ngModel)]="reason" maxlength="300" placeholder="Forgot to scan out" aria-label="Why" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!ready()" [mat-dialog-close]="{ punchedOutAt: punchedOutAt(), reason: reason.trim() }">Enter the punch-out</button>
    </mat-dialog-actions>
  `,
  styles,
})
export class ResolveDayDialog {
  protected readonly data = inject<ResolveDayData>(MAT_DIALOG_DATA);
  protected readonly duration = duration;
  protected readonly day = dayName(this.data.date);
  protected readonly inAt = clock(this.data.punchedIn, this.data.date);
  protected readonly endsAt = clock(this.data.shiftEndsAt, this.data.date);
  private readonly toShiftEnd = minutesBetween(this.data.punchedIn, this.data.shiftEndsAt);
  // When the next day starts, six hours before its shift: nine after this one ends.
  private readonly dayEndsAt = new Date(Date.parse(`${this.data.shiftEndsAt}Z`) + 9 * 3_600_000).toISOString().slice(0, 19);
  // The shift's end, when it can be the punch-out: after the punch-in and within 18 hours of it.
  protected readonly shiftEndFits = this.toShiftEnd > 0 && this.toShiftEnd <= abandonedAfterMinutes;
  // On the date the shift ends, as most punch-outs are; the time is the manager's to enter.
  protected date = (this.shiftEndFits ? this.data.shiftEndsAt : this.data.punchedIn).slice(0, 10);
  protected time = '';
  protected reason = '';

  protected atShiftEnd(): void {
    this.date = this.data.shiftEndsAt.slice(0, 10);
    this.time = this.data.shiftEndsAt.slice(11, 16);
  }

  protected punchedOutAt(): string {
    return `${this.date}T${this.time}:00`;
  }

  // Minutes from the punch-in to the punch-out entered; null until it is.
  protected after(): number | null {
    return this.date && /^\d{2}:\d{2}$/.test(this.time) ? minutesBetween(this.data.punchedIn, this.punchedOutAt()) : null;
  }

  protected problem(): string | null {
    const after = this.after();
    if (after === null) {
      return null;
    }
    if (after <= 0) {
      return `The punch-out comes after the punch-in at ${this.inAt}.`;
    }
    if (after > abandonedAfterMinutes) {
      return 'The punch-out is within 18 hours of the punch-in.';
    }
    return minutesBetween(this.dayEndsAt, this.punchedOutAt()) >= 0
      ? `The punch-out is before ${clock(this.dayEndsAt, this.data.date)}, when the next day starts.`
      : null;
  }

  protected ready(): boolean {
    return this.after() !== null && !this.problem() && !!this.reason.trim();
  }
}

// Who a penalty can be for: their hourly rate prices it.
export type PenaltyEmployee = Pick<Employee, 'id' | 'name' | 'hourlyRate'>;

// A day's penalty: given, fixed, when opened on a day or a penalty; chosen in the dialog, from
// `employees` and within `month`, when added from the month's list. `taken` are the penalties the
// month already has, as there is one a day: one given again replaces it.
export interface PenaltyData {
  employee?: PenaltyEmployee;
  date?: string;
  employees?: PenaltyEmployee[];
  month?: { first: string; last: string };
  taken?: { employeeId: string; date: string; hours: number }[];
  hours?: number | null;
  reason?: string | null;
}

export interface PenaltyAnswer {
  employeeId: string;
  date: string;
  // Null: taken off.
  hours: number | null;
  reason: string | null;
}

// An administrative penalty (جزاء إداري): hours taken off the month's pay at the employee's hourly
// rate, and why. One a day, changed or taken off until the month's pay is finalized.
@Component({
  selector: 'app-penalty-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Administrative penalty <span class="ar">جزاء إداري</span></h2>
    <mat-dialog-content>
      @if (data.employee && data.date) {
        <p><strong>{{ data.employee.name }}</strong>, {{ dayName(data.date) }}</p>
      } @else {
        <div class="row">
          <mat-form-field class="grow">
            <mat-label>Employee · الموظف</mat-label>
            <mat-select [(ngModel)]="employeeId" aria-label="Employee">
              @for (e of data.employees ?? []; track e.id) {
                <mat-option [value]="e.id">{{ e.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Day · اليوم</mat-label>
            <input matInput type="date" [(ngModel)]="date" [min]="data.month?.first ?? ''" [max]="data.month?.last ?? ''" aria-label="Day" />
          </mat-form-field>
        </div>
      }
      <div class="row">
        <mat-form-field>
          <mat-label>Hours · عدد الساعات</mat-label>
          <input matInput type="number" inputmode="decimal" min="0.25" max="72" step="0.25" [(ngModel)]="hours" aria-label="Hours" />
        </mat-form-field>
        @if (employee(); as e) {
          @if (validHours()) {
            <p class="cost">− {{ amount() | money }} <span class="muted">at {{ e.hourlyRate | money }} an hour</span></p>
          } @else {
            <p class="cost muted">{{ e.hourlyRate | money }} an hour</p>
          }
        }
      </div>
      @if (hours !== null && !validHours()) {
        <p class="problem">More than no hours and at most 72, to two decimal places: 1.5, 0.25.</p>
      }
      @if (replaces(); as taken) {
        <p class="problem">There is a penalty of {{ taken }}h on this day already: this replaces it, as there is one a day.</p>
      }
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <input matInput [(ngModel)]="reason" maxlength="300" placeholder="Left the walk-in open" aria-label="Why" />
      </mat-form-field>
      <p class="muted">Taken off the month’s pay at their hourly rate, a day’s pay over nine; changed or taken off until the month’s pay is finalized.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (existing) {
        <button mat-button class="danger" [mat-dialog-close]="answer(null)">Take it off</button>
      }
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!ready()" [mat-dialog-close]="answer(hours)">Save</button>
    </mat-dialog-actions>
  `,
  styles,
})
export class PenaltyDialog {
  protected readonly data = inject<PenaltyData>(MAT_DIALOG_DATA);
  protected readonly dayName = dayName;
  protected readonly existing = !!this.data.hours;
  protected employeeId = this.data.employee?.id ?? '';
  protected date = this.data.date ?? '';
  protected hours: number | null = this.data.hours ?? null;
  protected reason = this.data.reason ?? '';

  protected employee(): PenaltyEmployee | undefined {
    return this.data.employee ?? this.data.employees?.find((e) => e.id === this.employeeId);
  }

  protected validHours(): boolean {
    const h = this.hours;
    return typeof h === 'number' && h > 0 && h <= 72 && Math.abs(h * 100 - Math.round(h * 100)) < 1e-9;
  }

  protected amount(): number {
    return Math.round((this.hours ?? 0) * this.employee()!.hourlyRate * 100) / 100;
  }

  // The hours of the penalty already on the day chosen, when it is not the one being changed.
  protected replaces(): number | null {
    if (this.data.employee) {
      return null;
    }
    return this.data.taken?.find((t) => t.employeeId === this.employeeId && t.date === this.date)?.hours ?? null;
  }

  protected ready(): boolean {
    return !!this.employeeId && /^\d{4}-\d{2}-\d{2}$/.test(this.date) && this.validHours() && !!this.reason.trim();
  }

  protected answer(hours: number | null): PenaltyAnswer {
    return { employeeId: this.employeeId, date: this.date, hours, reason: hours === null ? null : this.reason.trim() };
  }
}
