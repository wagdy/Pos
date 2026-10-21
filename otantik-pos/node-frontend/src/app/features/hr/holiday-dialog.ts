import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { dayName, minutesBetween } from '../../core/hr/hr.api';

// A public holiday as entered: given when it is changed, or suggested; `existing` when there is
// one to take off.
export interface HolidayData {
  name?: string;
  firstDay?: string;
  lastDay?: string;
  existing?: boolean;
}

// Closes with the holiday, or with `remove` to take it off.
export type HolidayAnswer = { name: string; firstDay: string; lastDay: string } | { remove: true };

const maxDays = 10;

// A public holiday (عطلة رسمية): its name and the days it runs, one or several (the Eids), as
// the government announces them.
@Component({
  selector: 'app-holiday-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.existing ? 'Change the holiday' : 'Add a holiday' }} <span class="ar">عطلة رسمية</span></h2>
    <mat-dialog-content>
      <mat-form-field class="wide">
        <mat-label>Name · الاسم</mat-label>
        <input matInput [(ngModel)]="name" maxlength="100" placeholder="Eid al-Fitr" aria-label="Name" />
      </mat-form-field>
      <div class="row">
        <mat-form-field>
          <mat-label>First day · من</mat-label>
          <input matInput type="date" [ngModel]="firstDay" (ngModelChange)="pickFirst($event)" aria-label="First day" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Last day · إلى</mat-label>
          <input matInput type="date" [(ngModel)]="lastDay" [min]="firstDay" aria-label="Last day" />
        </mat-form-field>
      </div>
      @if (problem(); as why) {
        <p class="problem">{{ why }}</p>
      } @else if (days()) {
        <p>{{ days() }} {{ days() === 1 ? 'day' : 'days' }}: {{ dates() }}</p>
      }
      <p class="muted">Everyone's paid day off. Whoever works it earns a day's pay on top, for half a shift or more.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (data.existing) {
        <button mat-button class="danger" [mat-dialog-close]="{ remove: true }">Take it off</button>
      }
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!ready()" [mat-dialog-close]="{ name: name.trim(), firstDay, lastDay }">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
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
    .problem {
      color: var(--mat-sys-error);
    }
    .danger {
      color: var(--mat-sys-error);
      margin-right: auto;
    }
  `,
})
export class HolidayDialog {
  protected readonly data = inject<HolidayData>(MAT_DIALOG_DATA);
  protected name = this.data.name ?? '';
  protected firstDay = this.data.firstDay ?? '';
  protected lastDay = this.data.lastDay ?? this.data.firstDay ?? '';

  // A one-day holiday unless told otherwise: the last day follows the first until it is after it.
  protected pickFirst(day: string): void {
    const followed = !this.lastDay || this.lastDay === this.firstDay || this.lastDay < day;
    this.firstDay = day;
    if (followed) {
      this.lastDay = day;
    }
  }

  // The days it runs, once both are given; 0 until then.
  protected days(): number {
    return valid(this.firstDay) && valid(this.lastDay) ? minutesBetween(`${this.firstDay}T00:00:00`, `${this.lastDay}T00:00:00`) / 1440 + 1 : 0;
  }

  protected dates(): string {
    return this.firstDay === this.lastDay ? dayName(this.firstDay) : `${dayName(this.firstDay)} – ${dayName(this.lastDay)}`;
  }

  protected problem(): string | null {
    if (!valid(this.firstDay) || !valid(this.lastDay)) {
      return null;
    }
    if (this.lastDay < this.firstDay) {
      return 'The last day comes on or after the first.';
    }
    return this.days() > maxDays ? `A holiday runs ${maxDays} days at most: enter a longer one as two.` : null;
  }

  protected ready(): boolean {
    return !!this.name.trim() && this.days() > 0 && !this.problem();
  }
}

function valid(day: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(day ?? '');
}
