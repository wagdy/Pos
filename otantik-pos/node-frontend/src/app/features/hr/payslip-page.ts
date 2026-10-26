import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { AdjustmentKind, HrApi, PayrollAdjustment, PayrollSlip } from '../../core/hr/hr.api';
import { HrAccess } from '../../core/hr/hr-access';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { Treasury, isoDay } from '../costing/financials';
import { PayslipSheet } from './payslip-sheet';

export interface AdjustmentInput {
  kind: AdjustmentKind;
  amount: number;
  reason: string | null;
}

// An incentive or a leave allowance for the month: how much, and why.
@Component({
  selector: 'app-adjustment-dialog',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>Add to {{ data.employee }}’s pay <span class="ar">إضافة للراتب</span></h2>
    <mat-dialog-content>
      <mat-radio-group [(ngModel)]="kind" aria-label="What it is">
        <mat-radio-button value="Incentive">Incentive or bonus <span class="ar">حوافز</span></mat-radio-button>
        <mat-radio-button value="LeaveAllowance">Leave allowance: a holiday worked, or other <span class="ar">بدل إجازات</span></mat-radio-button>
      </mat-radio-group>
      <mat-form-field class="wide">
        <mat-label>Amount · المبلغ</mat-label>
        <input matInput type="number" min="0" step="any" [(ngModel)]="amount" aria-label="Amount" />
        <span matTextSuffix>&nbsp;L.E</span>
        @if (kind === 'LeaveAllowance') {
          <mat-hint>A day’s pay is {{ data.dailyRate | number: '1.2-2' }}. Days off worked are counted by themselves.</mat-hint>
        }
      </mat-form-field>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <input matInput [(ngModel)]="reason" maxlength="300" aria-label="Why" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!(amount && amount > 0)" [mat-dialog-close]="{ kind, amount, reason: reason.trim() || null }">Add</button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide {
      width: 100%;
    }
    mat-radio-group {
      display: grid;
      gap: 4px;
      margin-bottom: 12px;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
  `,
})
export class AdjustmentDialog {
  protected readonly data = inject<{ employee: string; dailyRate: number }>(MAT_DIALOG_DATA);
  protected kind: AdjustmentKind = 'Incentive';
  protected amount: number | null = null;
  protected reason = '';
}

// Before finalizing: what it fixes.
@Component({
  selector: 'app-finalize-dialog',
  imports: [MatButtonModule, MatDialogModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Finalize {{ data.employee }}’s pay? <span class="ar">اعتماد الراتب</span></h2>
    <mat-dialog-content>
      <p>Net payable <strong>{{ data.net | money }}</strong>. Once finalized, the slip stays as it is: its days, incentives and advances are not changed after it, and what is paid or advanced later counts next month.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [mat-dialog-close]="true">Finalize</button>
    </mat-dialog-actions>
  `,
  styles: `
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
  `,
})
export class FinalizeDialog {
  protected readonly data = inject<{ employee: string; net: number }>(MAT_DIALOG_DATA);
}

// An employee's payslip for a month (قسيمة الراتب): the formula, line by line, from the base salary
// to the net payable; the days it was worked out from; the incentives, allowances and advances in
// it; and what has been paid of it. A manager adds incentives and allowances and finalizes it once
// the month is over and every day is settled; then it is paid from the safe or a day's takings.
// It prints as a payslip. Embedded in an employee's profile, it has no way back to Payroll but a
// link to open it there.
@Component({
  selector: 'app-payslip-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, PayslipSheet, RouterLink],
  template: `
    <div class="toolbar no-print">
      @if (!embedded()) {
        <a mat-button routerLink="../" [queryParams]="{ month: month() }"><mat-icon>arrow_back</mat-icon> Payroll · مسير الرواتب</a>
      }
      <span class="spacer"></span>
      @if (slip(); as s) {
        @if (embedded()) {
          <a mat-button [routerLink]="['/hr/payroll', s.employee.id]" [queryParams]="{ month: month() }"><mat-icon>open_in_new</mat-icon> On Payroll</a>
        }
        @if (access.has('Attendance')) {
          <a mat-button routerLink="/hr/attendance" [queryParams]="{ employee: s.employee.id, month: month() }"><mat-icon>schedule</mat-icon> Timesheet</a>
        }
        <button mat-button (click)="print()"><mat-icon>print</mat-icon> Print</button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (slip(); as s) {
      @let f = s.figures;
      <app-payslip-sheet [slip]="s" [month]="month()" [canVoid]="canDecide()" (voidAdjustment)="voidAdjustment($event)">
        <div class="actions no-print">
          @if (canDecide() && s.status !== 'Finalized') {
            <button mat-stroked-button (click)="addAdjustment(s)"><mat-icon>add</mat-icon> Incentive or allowance <span class="inline-ar">حوافز أو بدل</span></button>
            <span [matTooltip]="finalizeBlocked(s) ?? ''" [matTooltipDisabled]="!finalizeBlocked(s)">
              <button mat-flat-button (click)="finalize(s)" [disabled]="!!finalizeBlocked(s)"><mat-icon>verified</mat-icon> Finalize <span class="inline-ar">اعتماد</span></button>
            </span>
          }
          @if (s.status === 'Finalized' && s.salaryOwed > 0 && canPay()) {
            <button mat-flat-button (click)="pay(s)"><mat-icon>payments</mat-icon> Pay salary <span class="inline-ar">صرف الراتب</span></button>
          }
        </div>
        @if (s.status !== 'Finalized' && finalizeBlocked(s); as why) {
          <p class="note no-print">
            {{ why }}
            @if (!f.openDays && f.missingPunchDays > 0 && access.has('Corrections')) {
              <a routerLink="/hr/corrections">Open Corrections</a>
            }
          </p>
        }
      </app-payslip-sheet>
    } @else if (!loading()) {
      <p class="muted">They were not employed in {{ month() | date: 'MMMM y' }}.</p>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin: 16px 0 8px;
    }
  `,
})
export class PayslipPage {
  private readonly api = inject(HrApi);
  protected readonly access = inject(HrAccess);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly treasury = inject(Treasury);
  private readonly approvals = inject(Approvals);

  readonly employeeId = input.required<string>();
  readonly startMonth = input<string>(undefined, { alias: 'month' });
  // In an employee's profile, not a page of its own.
  readonly embedded = input(false);
  // Changed by whoever holds it to have the slip read again: the profile, when a disciplinary
  // action's penalty is issued or withdrawn beside it.
  readonly reloadKey = input(0);

  // A manager's, or an employee's holding the Payroll tab in the portal.
  protected readonly canDecide = computed(() => this.access.canDecide('Payroll'));
  // Paying is a safe entry, Financials': whoever keeps the books.
  protected readonly canPay = computed(() => this.auth.can(Permissions.InventoryManage));
  protected readonly month = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly slip = signal<PayrollSlip | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const [, month] = [this.employeeId(), this.startMonth(), this.reloadKey()];
      untracked(() => {
        if (month && /^\d{4}-\d{2}-\d{2}$/.test(month)) {
          this.month.set(`${month.slice(0, 8)}01`);
        }
        void this.load();
      });
    });
  }

  // What keeps it from being finalized yet, said plainly; null when nothing does.
  protected finalizeBlocked(s: PayrollSlip): string | null {
    if (s.figures.openDays > 0) {
      return 'The month is not over yet: a payslip is finalized once all its days are.';
    }
    if (s.figures.missingPunchDays > 0) {
      const n = s.figures.missingPunchDays;
      return `${n} ${n === 1 ? 'day is' : 'days are'} suspended, a punch-in with no punch-out: enter the punch-out, or ignore the stray punch, on Corrections first.`;
    }
    return null;
  }

  protected async addAdjustment(s: PayrollSlip): Promise<void> {
    const answer = await firstValueFrom(
      this.dialog
        .open<AdjustmentDialog, { employee: string; dailyRate: number }, AdjustmentInput>(AdjustmentDialog, {
          data: { employee: s.employee.name, dailyRate: s.employee.dailyRate },
          width: '460px',
        })
        .afterClosed(),
    );
    if (answer) {
      await this.act(() => firstValueFrom(this.api.addAdjustment(s.employee.id, this.month(), answer.kind, answer.amount, answer.reason)), 'Added to the pay.');
    }
  }

  protected async voidAdjustment(a: PayrollAdjustment): Promise<void> {
    await this.act(() => firstValueFrom(this.api.voidAdjustment(a.id)), 'Voided.');
  }

  protected async finalize(s: PayrollSlip): Promise<void> {
    const sure = await firstValueFrom(
      this.dialog
        .open<FinalizeDialog, { employee: string; net: number }, boolean>(FinalizeDialog, { data: { employee: s.employee.name, net: s.figures.netPayable }, width: '460px' })
        .afterClosed(),
    );
    if (sure) {
      await this.act(() => firstValueFrom(this.api.finalizeSlip(s.employee.id, this.month())), `${s.employee.name}’s pay is finalized.`);
    }
  }

  protected async pay(s: PayrollSlip): Promise<void> {
    if (await this.treasury.enter('SalaryPayment', { employeeId: s.employee.id, payrollMonth: this.month(), amount: s.salaryOwed })) {
      await this.load();
    }
  }

  protected print(): void {
    window.print();
  }

  private async act(call: () => Promise<unknown>, done: string): Promise<void> {
    try {
      await call();
      this.notify.info(done);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const payroll = await firstValueFrom(this.api.payroll(this.month()));
      this.slip.set(payroll.slips.find((s) => s.employee.id === this.employeeId()) ?? null);
    } catch (error) {
      this.error.set(errorMessage(error, 'The payslip could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
