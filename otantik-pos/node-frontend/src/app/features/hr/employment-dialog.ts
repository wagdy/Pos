import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { ApprovalResult } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { Employee, HrApi } from '../../core/hr/hr.api';

export interface EmploymentDialogData {
  employee: Employee;
  // Leaving: their last day. Coming back: the day they start again.
  mode: 'leave' | 'return';
  canApprove: boolean;
  today: string;
}

// An employee leaving (paid to their last day, and off the lists of who is at work), or coming
// back. A manager's at once; anyone else's sent for approval.
@Component({
  selector: 'app-employment-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>
      @if (leaving) {
        {{ data.employee.name }} leaves <span class="ar">إنهاء الخدمة</span>
      } @else {
        {{ data.employee.name }} comes back <span class="ar">إعادة التعيين</span>
      }
    </h2>
    <mat-dialog-content>
      <p class="muted">
        @if (leaving) {
          They are paid to their last day, and are not counted absent after it. Their records stay.
        } @else {
          They are employed again from this day; the days they were away are not counted.
        }
      </p>
      <mat-form-field>
        <mat-label>{{ leaving ? 'Last day · آخر يوم' : 'Back from · يعود من' }}</mat-label>
        <input matInput type="date" [(ngModel)]="day" [min]="leaving ? data.employee.hiredOn : (data.employee.leftOn ?? '')" [attr.aria-label]="leaving ? 'Last day' : 'Back from'" />
      </mat-form-field>
      @if (!data.canApprove) {
        <mat-form-field class="wide">
          <mat-label>Reason · السبب</mat-label>
          <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Reason" />
        </mat-form-field>
        <p class="ask">A manager approves this: nothing changes until then.</p>
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [class.danger-fill]="leaving" (click)="save()" [disabled]="saving() || !day">
        {{ data.canApprove ? (leaving ? 'Mark as left' : 'Bring back') : 'Send for approval' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide {
      width: 100%;
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
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    .error {
      color: var(--mat-sys-error);
    }
    .danger-fill {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
  `,
})
export class EmploymentDialog {
  private readonly api = inject(HrApi);
  private readonly ref = inject<MatDialogRef<EmploymentDialog, Employee | ApprovalResult>>(MatDialogRef);
  protected readonly data = inject<EmploymentDialogData>(MAT_DIALOG_DATA);

  protected readonly leaving = this.data.mode === 'leave';
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected day = this.data.today;
  protected reason = '';

  protected async save(): Promise<void> {
    if (this.saving() || !this.day) {
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    const reason = this.reason.trim() || null;
    try {
      this.ref.close(
        await firstValueFrom(
          this.leaving ? this.api.leave(this.data.employee.id, this.day, reason) : this.api.comeBack(this.data.employee.id, this.day, reason),
        ),
      );
    } catch (error) {
      this.error.set(errorMessage(error, 'It could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
