import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { EmployeePicker } from '../../core/hr/employee-picker';
import { Employee } from '../../core/hr/hr.api';
import { FormChecks, FormProblem } from './form-checks';

// Material's own: a field in error once touched and invalid.
const defaultMatcher = new ErrorStateMatcher();

// An employee's advance (سلفة) paid in the daily batch: out of the safe or the day's takings, as the
// batch is paid, and off the employee's pay for the month on the payroll. With no employee, it is a
// lump sum handed out as advances (سلف معلقة), given out to employees on HR → Advances. Its id is
// the advance's, made here: a retry pays once.
export interface AdvanceEntry {
  id: string;
  employeeId: string | null;
  amount: number | null;
  notes: string;
}

export function newAdvance(): AdvanceEntry {
  return { id: crypto.randomUUID(), employeeId: null, amount: null, notes: '' };
}

// Who is advanced (found by typing their name; or no one yet), how much, and a note. Only those
// employed are offered. Its fields sit in the form around it.
@Component({
  selector: 'app-advance-fields',
  imports: [EmployeePicker, FormsModule, MatFormFieldModule, MatInputModule],
  template: `
    <app-employee-picker [employees]="employees()" [value]="advance().employeeId" (valueChange)="advance().employeeId = $event; changed.emit()"
      [none]="true" noneLabel="No one yet: give it out on HR → Advances" [ariaLabel]="label() + ', employee'" />
    <mat-form-field subscriptSizing="dynamic" class="amount">
      <mat-label>Amount · المبلغ</mat-label>
      <input matInput type="number" min="0" step="any" [(ngModel)]="advance().amount" (ngModelChange)="changed.emit()" [attr.aria-label]="label() + ', amount'"
        [errorStateMatcher]="marked(', amount')" />
      <span matTextSuffix>&nbsp;L.E</span>
      <mat-error>{{ said(', amount') }}</mat-error>
    </mat-form-field>
    <mat-form-field subscriptSizing="dynamic" class="notes">
      <mat-label>Notes · ملاحظات</mat-label>
      <input matInput [(ngModel)]="advance().notes" (ngModelChange)="changed.emit()" maxlength="300" [attr.aria-label]="label() + ', notes'" />
    </mat-form-field>
  `,
  styles: `
    :host {
      display: contents;
    }
    .amount {
      width: 170px;
    }
    .notes {
      min-width: 200px;
    }
  `,
})
export class AdvanceFields {
  readonly advance = input.required<AdvanceEntry>();
  readonly employees = input.required<Employee[]>();
  // How its fields are named to a screen reader: "Advance 2, amount".
  readonly label = input('Advance');
  readonly checks = input<FormChecks | null>(null);
  readonly changed = output<void>();

  protected marked(field: string): ErrorStateMatcher {
    return this.checks()?.field(this.label() + field) ?? defaultMatcher;
  }

  protected said(field: string): string {
    return this.checks()?.message(this.label() + field) ?? '';
  }
}

// What holds an advance up: its amount. No employee is a lump sum, given out later.
export function advanceProblems(advance: AdvanceEntry, label: string): FormProblem[] {
  const found: FormProblem[] = [];
  if (!((advance.amount ?? 0) > 0)) {
    found.push({ where: label, what: 'enter the amount', field: `${label}, amount` });
  }
  return found;
}
