import { DecimalPipe } from '@angular/common';
import { Component, ElementRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { ApprovalResult } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { Employee, EmployeeInput, HrApi, WeekDay, weekDays } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { FormChecks, FormProblem } from '../costing/form-checks';

export interface EmployeeDialogData {
  // The one changed; none for a new one.
  employee?: Employee;
  // A new one's name and number as the attendance device has them.
  fromDevice?: { name: string; deviceUserId: string };
  canApprove: boolean;
  today: string;
}

// An employee added or changed: who they are, their base monthly salary, when their nine-hour
// shift starts (or flexible hours, with none), their weekly days off, the day they started, and
// their number on the attendance device. Saved here: a manager's at once, anyone else's sent for
// approval. Closes with what was saved or asked, or nothing when cancelled.
@Component({
  selector: 'app-employee-dialog',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.employee) {
        Edit {{ data.employee.name }} <span class="ar">تعديل بيانات الموظف</span>
      } @else {
        Add employee <span class="ar">إضافة موظف</span>
      }
    </h2>
    <mat-dialog-content>
      <div class="form">
        <mat-form-field class="wide">
          <mat-label>Name · الاسم</mat-label>
          <input matInput [(ngModel)]="name" maxlength="200" aria-label="Name" [errorStateMatcher]="checks.field('Name')" />
          <mat-error>{{ checks.message('Name') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Role · الوظيفة</mat-label>
          <input matInput [(ngModel)]="jobTitle" maxlength="100" aria-label="Role" placeholder="Chef, waiter, cashier" [errorStateMatcher]="checks.field('Role')" />
          <mat-error>{{ checks.message('Role') }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Base salary · الراتب الأساسي</mat-label>
          <input matInput type="number" min="0" step="any" [(ngModel)]="salary" aria-label="Base salary" [errorStateMatcher]="checks.field('Base salary')" />
          <span matTextSuffix>&nbsp;L.E</span>
          @if (salary !== null && salary > 0) {
            <mat-hint>A month: {{ salary / 30 | number: '1.2-2' }} a day · {{ salary / 270 | number: '1.2-2' }} an hour</mat-hint>
          } @else {
            <mat-hint>A month</mat-hint>
          }
          <mat-error>{{ checks.message('Base salary') }}</mat-error>
        </mat-form-field>
        <div class="shift">
          @if (flexible) {
            <p class="muted small flexible">No start time: nothing is late for them, and hours past nine in a day are overtime.</p>
          } @else {
            <mat-form-field>
              <mat-label>Shift starts · بداية الوردية</mat-label>
              <input matInput type="time" [(ngModel)]="shiftStart" aria-label="Shift starts" [errorStateMatcher]="checks.field('Shift starts')" />
              @if (shiftEnd(); as end) {
                <mat-hint>Nine hours: ends {{ end }}</mat-hint>
              }
              <mat-error>{{ checks.message('Shift starts') }}</mat-error>
            </mat-form-field>
          }
          <mat-checkbox [(ngModel)]="flexible">Flexible hours <span class="ar">ساعات مرنة</span></mat-checkbox>
        </div>
        <mat-form-field>
          <mat-label>Started on · تاريخ التعيين</mat-label>
          <input matInput type="date" [(ngModel)]="hiredOn" aria-label="Started on" [errorStateMatcher]="checks.field('Started on')" />
          <mat-error>{{ checks.message('Started on') }}</mat-error>
        </mat-form-field>
        <div class="wide days">
          <span class="label">Weekly days off · أيام الراحة</span>
          <mat-button-toggle-group multiple [(ngModel)]="daysOff" aria-label="Weekly days off" hideMultipleSelectionIndicator>
            @for (d of week; track d.day) {
              <mat-button-toggle [value]="d.day" [attr.aria-label]="d.day">{{ d.en }}<span class="ar">{{ d.ar }}</span></mat-button-toggle>
            }
          </mat-button-toggle-group>
          @if (checks.shown() && checks.message('Weekly days off')) {
            <span class="error small">{{ checks.message('Weekly days off') }}</span>
          } @else {
            <span class="muted small">A working day with no punches and no leave is an absence.</span>
          }
        </div>
        <mat-form-field>
          <mat-label>Device number · رقم البصمة</mat-label>
          <input matInput [(ngModel)]="deviceUserId" maxlength="24" aria-label="Device number" [errorStateMatcher]="checks.field('Device number')" />
          <mat-hint>Their number on the K40</mat-hint>
          <mat-error>{{ checks.message('Device number') }}</mat-error>
        </mat-form-field>
        @if (!data.canApprove) {
          <mat-form-field>
            <mat-label>Reason · السبب</mat-label>
            <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Reason" />
          </mat-form-field>
        }
      </div>
      @if (!data.canApprove) {
        <p class="ask">A manager approves employees and their changes: nothing changes until then. <span class="ar">يُرسل للمدير للموافقة.</span></p>
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="saving()">{{ data.canApprove ? 'Save' : 'Send for approval' }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 8px 12px;
    }
    .wide {
      grid-column: span 2;
    }
    .shift {
      display: grid;
      align-content: start;

      mat-form-field {
        width: 100%;
      }
      .flexible {
        margin: 8px 0 4px;
      }
    }
    .days {
      display: grid;
      gap: 6px;
      margin: 4px 0 16px;

      .label {
        font: var(--mat-sys-body-small);
        color: var(--mat-sys-on-surface-variant);
      }
      mat-button-toggle-group {
        flex-wrap: wrap;
      }
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    mat-button-toggle .ar {
      display: block;
      margin: 0;
      line-height: 1.2;
    }
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    .error {
      color: var(--mat-sys-error);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    @media (max-width: 600px) {
      .form {
        grid-template-columns: minmax(0, 1fr);
      }
      .wide {
        grid-column: auto;
      }
    }
  `,
})
export class EmployeeDialog {
  private readonly api = inject(HrApi);
  private readonly notify = inject(NotifyService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly ref = inject<MatDialogRef<EmployeeDialog, Employee | ApprovalResult>>(MatDialogRef);
  protected readonly data = inject<EmployeeDialogData>(MAT_DIALOG_DATA);

  protected readonly week = weekDays;
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  private readonly id = this.data.employee?.id ?? crypto.randomUUID();
  // Made once: a retry after a lost answer finds what was made.
  private readonly requestId = crypto.randomUUID();

  protected name = this.data.employee?.name ?? this.data.fromDevice?.name ?? '';
  protected jobTitle = this.data.employee?.jobTitle ?? '';
  protected salary: number | null = this.data.employee?.monthlySalary ?? null;
  protected flexible = !!this.data.employee && this.data.employee.shiftStartsAt === null;
  protected shiftStart = (this.data.employee?.shiftStartsAt ?? '10:00:00').slice(0, 5);
  protected daysOff: WeekDay[] = [...(this.data.employee?.daysOff ?? [])];
  protected hiredOn = this.data.employee?.hiredOn ?? this.data.today;
  protected deviceUserId = this.data.employee?.deviceUserId ?? this.data.fromDevice?.deviceUserId ?? '';
  protected reason = '';

  protected readonly checks = new FormChecks(() => this.problems());

  // When the nine hours end: "19:00", or "01:00 the next day".
  protected shiftEnd(): string | null {
    const match = /^(\d{2}):(\d{2})$/.exec(this.shiftStart ?? '');
    if (!match) {
      return null;
    }
    const minutes = Number(match[1]) * 60 + Number(match[2]) + 9 * 60;
    const end = `${String(Math.floor(minutes / 60) % 24).padStart(2, '0')}:${match[2]}`;
    return minutes >= 24 * 60 ? `${end} the next day` : end;
  }

  protected problems(): FormProblem[] {
    const problems: FormProblem[] = [];
    const add = (field: string, what: string) => problems.push({ where: field, what, field });
    if (!this.name.trim()) {
      add('Name', 'enter their name');
    }
    if (!this.jobTitle.trim()) {
      add('Role', 'enter what they do');
    }
    if (this.salary === null || !(this.salary >= 0)) {
      add('Base salary', 'enter the salary');
    }
    if (!this.flexible && !/^\d{2}:\d{2}$/.test(this.shiftStart ?? '')) {
      add('Shift starts', 'enter when the shift starts');
    }
    if (!this.hiredOn) {
      add('Started on', 'enter the day they started');
    }
    if (this.daysOff.length >= 7) {
      add('Weekly days off', 'leave at least one working day');
    }
    if (this.deviceUserId.trim() && !/^[A-Za-z0-9]+$/.test(this.deviceUserId.trim())) {
      add('Device number', 'use the number as the device shows it: letters and digits');
    }
    return problems;
  }

  protected async save(): Promise<void> {
    if (this.saving() || !this.checks.passed(this.notify, this.host.nativeElement)) {
      return;
    }
    const input: EmployeeInput = {
      requestId: this.requestId,
      name: this.name.trim(),
      jobTitle: this.jobTitle.trim(),
      monthlySalary: this.salary!,
      shiftStartsAt: this.flexible ? null : `${this.shiftStart}:00`,
      daysOff: this.daysOff,
      hiredOn: this.hiredOn,
      deviceUserId: this.deviceUserId.trim() || null,
      reason: this.reason.trim() || null,
    };
    this.saving.set(true);
    this.error.set(null);
    try {
      this.ref.close(await firstValueFrom(this.api.saveEmployee(this.id, input)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The employee could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
