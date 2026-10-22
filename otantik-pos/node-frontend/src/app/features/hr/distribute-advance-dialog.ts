import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { EmployeePicker } from '../../core/hr/employee-picker';
import { Employee, HrApi, PendingAdvance } from '../../core/hr/hr.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

export interface DistributeAdvanceData {
  advance: PendingAdvance;
  // Those employed on the day it was handed out.
  employees: Employee[];
}

interface ShareRow {
  id: number;
  employeeId: string | null;
  amount: number | null;
  notes: string;
}

// Advances handed out with no one named, given out (توزيع السلفة): an employee a row, each with a
// share, never more than is left; some now and the rest later is fine. Split equally shares what
// is left among the employees chosen, in whole pounds, the odd ones on the first. Each share comes
// off its employee's pay. Sent from here, so a share the till refuses (one not employed then) is
// said here and nothing is lost.
@Component({
  selector: 'app-distribute-advance-dialog',
  imports: [
    DatePipe,
    EmployeePicker,
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MoneyPipe,
  ],
  template: `
    <h2 mat-dialog-title>Give out {{ advance.remaining | money }} <span class="inline-ar">توزيع السلفة</span></h2>
    <mat-dialog-content>
      <p class="muted">
        Handed out {{ advance.date | date: 'd MMM y' }}
        @if (advance.fromTakingsOf) {
          from the takings of {{ advance.fromTakingsOf | date: 'd MMM' }}
        } @else {
          from the safe
        }
        @if (advance.notes) {
          · {{ advance.notes }}
        }
        @if (advance.allocated) {
          · {{ advance.allocated | money }} of {{ advance.amount | money }} given out already
        }
      </p>
      <p class="muted small">Each share comes off its employee's pay, from the month it was handed out in.</p>
      @for (row of rows(); track row.id; let i = $index) {
        <div class="row" [attr.aria-label]="'Share ' + (i + 1)">
          <app-employee-picker [employees]="choosable(row)" [value]="row.employeeId" (valueChange)="set(i, { employeeId: $event })"
            [ariaLabel]="'Share ' + (i + 1) + ', employee'" />
          <mat-form-field subscriptSizing="dynamic" class="amount">
            <mat-label>Amount · المبلغ</mat-label>
            <input matInput type="number" min="0" step="any" [ngModel]="row.amount" (ngModelChange)="set(i, { amount: $event })"
              [attr.aria-label]="'Share ' + (i + 1) + ', amount'" />
            <span matTextSuffix>&nbsp;L.E</span>
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="notes">
            <mat-label>Notes · ملاحظات</mat-label>
            <input matInput [ngModel]="row.notes" (ngModelChange)="set(i, { notes: $event })" maxlength="300" [attr.aria-label]="'Share ' + (i + 1) + ', notes'" />
          </mat-form-field>
          <button mat-icon-button (click)="remove(i)" [disabled]="rows().length === 1" [attr.aria-label]="'Take share ' + (i + 1) + ' off'">
            <mat-icon>close</mat-icon>
          </button>
        </div>
      }
      <div class="more">
        <button mat-stroked-button (click)="add()" [disabled]="rows().length >= data.employees.length"><mat-icon>person_add</mat-icon> Add an employee</button>
        <button mat-button (click)="split()" [disabled]="!chosen()"><mat-icon>call_split</mat-icon> Split equally <span class="inline-ar">بالتساوي</span></button>
      </div>
      <p class="total" [class.bad]="over()">
        Shares {{ total() | money }} of {{ advance.remaining | money }} left
        @if (!over() && total() < advance.remaining) {
          · {{ advance.remaining - total() | money }} stays to give out later
        }
      </p>
      @if (saving()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="!!holdUp() || saving()">Give out {{ total() | money }}</button>
    </mat-dialog-actions>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 8px;
      margin: 8px 0;
    }
    .amount {
      width: 170px;
    }
    .notes {
      flex: 1;
      min-width: 160px;
    }
    .more {
      display: flex;
      gap: 8px;
      margin: 4px 0 8px;
    }
    .total {
      font: var(--mat-sys-title-small);

      &.bad {
        color: var(--mat-sys-error);
      }
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class DistributeAdvanceDialog {
  protected readonly data = inject<DistributeAdvanceData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<DistributeAdvanceDialog, PendingAdvance>>(MatDialogRef);
  private readonly api = inject(HrApi);
  protected readonly advance = this.data.advance;

  private next = 1;
  protected readonly rows = signal<ShareRow[]>([this.row()]);
  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly total = computed(() => round(this.rows().reduce((sum, r) => sum + (Number(r.amount) || 0), 0)));
  protected readonly over = computed(() => this.total() > this.advance.remaining);
  protected readonly chosen = computed(() => this.rows().filter((r) => r.employeeId).length);

  // What keeps it from being given out, said on the button's state.
  protected readonly holdUp = computed(() => {
    const rows = this.rows();
    if (rows.some((r) => !r.employeeId)) {
      return 'Choose an employee for each share.';
    }
    if (rows.some((r) => !((Number(r.amount) || 0) > 0))) {
      return 'Give each share an amount.';
    }
    return this.over() ? 'The shares come to more than is left.' : null;
  });

  // Who a row may go to: anyone not on another row.
  protected choosable(row: ShareRow): Employee[] {
    const taken = new Set(this.rows().filter((r) => r !== row && r.employeeId).map((r) => r.employeeId));
    return this.data.employees.filter((e) => !taken.has(e.id));
  }

  protected set(index: number, change: Partial<ShareRow>): void {
    this.problem.set(null);
    this.rows.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  protected add(): void {
    this.rows.update((rows) => [...rows, this.row()]);
  }

  protected remove(index: number): void {
    this.rows.update((rows) => rows.filter((_, i) => i !== index));
  }

  // What is left, shared among the employees chosen: whole pounds each, the rest on the first.
  protected split(): void {
    const chosen = this.rows().filter((r) => r.employeeId);
    if (!chosen.length) {
      return;
    }
    const each = Math.floor(this.advance.remaining / chosen.length);
    const first = round(this.advance.remaining - each * (chosen.length - 1));
    this.problem.set(null);
    this.rows.set(chosen.map((r, i) => ({ ...r, amount: i === 0 ? first : each })));
  }

  protected async save(): Promise<void> {
    const held = this.holdUp();
    if (held) {
      this.problem.set(held);
      return;
    }
    this.saving.set(true);
    this.problem.set(null);
    try {
      const shares = this.rows().map((r) => ({ employeeId: r.employeeId!, amount: round(Number(r.amount)), notes: r.notes.trim() || null }));
      this.ref.close(await firstValueFrom(this.api.distributeAdvance(this.advance.id, shares)));
    } catch (error) {
      this.problem.set(errorMessage(error, 'The advances could not be given out.'));
    } finally {
      this.saving.set(false);
    }
  }

  private row(): ShareRow {
    return { id: this.next++, employeeId: null, amount: null, notes: '' };
  }
}

function round(amount: number): number {
  return Math.round(amount * 100) / 100;
}
