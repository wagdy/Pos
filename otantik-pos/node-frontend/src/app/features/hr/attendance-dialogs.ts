import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { DayMarkKind } from '../../core/hr/hr.api';

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
}

// A day marked as leave or a day off (not an absence), or the mark taken off. Closes with the
// kind ('None' to take it off) and the notes.
@Component({
  selector: 'app-mark-day-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>{{ data.employee }}, {{ data.day }} <span class="ar">تمييز اليوم</span></h2>
    <mat-dialog-content>
      <mat-radio-group [(ngModel)]="kind" aria-label="The day is">
        <mat-radio-button value="Leave">Leave, paid <span class="ar">إجازة</span></mat-radio-button>
        <mat-radio-button value="DayOff">A day off: swapped, or extra <span class="ar">راحة</span></mat-radio-button>
        <mat-radio-button value="None">No mark: absent if there are no punches <span class="ar">بدون</span></mat-radio-button>
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
