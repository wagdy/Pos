import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalResult, isRequested } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { Employee, HrApi, weekDays } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { EmployeeDialog, EmployeeDialogData } from './employee-dialog';
import { EmploymentDialog, EmploymentDialogData } from './employment-dialog';

// The employees (الموظفين): who is on the payroll, their base salary, their nine-hour shift and
// days off, and their number on the attendance device. Added, changed, leaving and coming back by
// whoever manages HR: a manager's at once, anyone else's waiting for a manager, listed above the
// table until then (a new one) or locking its row (a change).
@Component({
  selector: 'app-employees-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatIconModule, MatProgressBarModule, MatSlideToggleModule, MatTooltipModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <mat-slide-toggle [ngModel]="showLeft()" (ngModelChange)="showLeft.set($event)">Show who has left · عرض من ترك العمل</mat-slide-toggle>
      <span class="spacer"></span>
      <span class="muted">{{ active().length }} employed · {{ payroll() | money }} a month</span>
      @if (canManage()) {
        <button mat-flat-button (click)="add()"><mat-icon>person_add</mat-icon> Add employee <span class="inline-ar">إضافة موظف</span></button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      @if (waiting().length) {
        <ul class="waiting">
          @for (w of waiting(); track w.id) {
            <li>
              <span class="pending-badge">{{ pendingLabel(w).en }}<span class="ar">{{ pendingLabel(w).ar }}</span></span>
              {{ w.summary }} <span class="muted small">· asked by {{ w.requestedBy }}</span>
            </li>
          }
        </ul>
      }
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Employee<span class="ar">الموظف</span></th>
              <th class="num">Base salary<span class="ar">الراتب الأساسي</span></th>
              <th>Shift<span class="ar">الوردية</span></th>
              <th>Days off<span class="ar">أيام الراحة</span></th>
              <th>Device no.<span class="ar">رقم البصمة</span></th>
              <th>Status<span class="ar">الحالة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (e of shown(); track e.id) {
              @let asked = approvals.pendingFor(e.id);
              <tr [class.left]="!e.isActive" [class.pending-deletion]="!!asked">
                <td>
                  <strong>{{ e.name }}</strong>
                  <div class="muted small">{{ e.jobTitle }}</div>
                  @if (asked) {
                    <div><span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span></div>
                  }
                </td>
                <td class="num">
                  {{ e.monthlySalary | money }}
                  <div class="muted small">{{ e.dailyRate | money }} a day · {{ e.hourlyRate | money }} an hour</div>
                </td>
                <td class="nowrap">{{ e.shiftStartsAt.slice(0, 5) }}–{{ e.shiftEndsAt.slice(0, 5) }}</td>
                <td>{{ daysOff(e) }}</td>
                <td>{{ e.deviceUserId ?? '—' }}</td>
                <td>
                  @if (e.isActive) {
                    <span class="status active">Employed<span class="ar">على رأس العمل</span></span>
                    <div class="muted small">since {{ e.hiredOn | date: 'd MMM y' }}</div>
                  } @else {
                    <span class="status">Left<span class="ar">ترك العمل</span></span>
                    <div class="muted small">last day {{ e.leftOn | date: 'd MMM y' }}</div>
                  }
                </td>
                <td class="num nowrap">
                  @if (canManage()) {
                    @if (asked) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval">lock_clock</mat-icon>
                    } @else {
                      <button mat-icon-button (click)="edit(e)" matTooltip="Edit" [attr.aria-label]="'Edit ' + e.name"><mat-icon>edit</mat-icon></button>
                      @if (e.isActive) {
                        <button mat-icon-button class="danger" (click)="leave(e)" matTooltip="Leaves" [attr.aria-label]="e.name + ' leaves'">
                          <mat-icon>person_off</mat-icon>
                        </button>
                      } @else {
                        <button mat-icon-button (click)="comeBack(e)" matTooltip="Comes back" [attr.aria-label]="e.name + ' comes back'">
                          <mat-icon>person_add_alt</mat-icon>
                        </button>
                      }
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="muted">
                  @if (employees().length) {
                    Everyone on the list has left. Turn on “Show who has left” to see them.
                  } @else {
                    No employees yet. Add them to count their attendance and pay.
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .waiting {
      margin: 0 0 12px;
      padding: 0;
      list-style: none;
      display: grid;
      gap: 4px;
    }
    tr.left td {
      color: var(--mat-sys-on-surface-variant);
    }
    .status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);

      &.active {
        background: color-mix(in srgb, var(--till-ok, #2e7d32) 16%, transparent);
      }
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
  `,
})
export class EmployeesPage {
  private readonly api = inject(HrApi);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  protected readonly employees = signal<Employee[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly canManage = computed(() => this.auth.can(Permissions.HrManage));
  protected readonly showLeft = signal(false);

  protected readonly active = computed(() => this.employees().filter((e) => e.isActive));
  protected readonly shown = computed(() => (this.showLeft() ? this.employees() : this.active()));
  protected readonly payroll = computed(() => this.active().reduce((sum, e) => sum + e.monthlySalary, 0));
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('Employee'));

  constructor() {
    void this.load();
    void this.approvals.refresh();
  }

  // "Fri" or "Fri, Sat", in the restaurant's week order; "None".
  protected daysOff(e: Employee): string {
    const off = weekDays.filter((d) => e.daysOff.includes(d.day)).map((d) => d.en);
    return off.length ? off.join(', ') : 'None';
  }

  protected async add(): Promise<void> {
    await this.open({ canApprove: this.approvals.canApprove(), today: await this.businessDay.today() });
  }

  protected async edit(employee: Employee): Promise<void> {
    await this.open({ employee, canApprove: this.approvals.canApprove(), today: await this.businessDay.today() });
  }

  protected async leave(employee: Employee): Promise<void> {
    await this.employment({ employee, mode: 'leave', canApprove: this.approvals.canApprove(), today: await this.businessDay.today() });
  }

  protected async comeBack(employee: Employee): Promise<void> {
    await this.employment({ employee, mode: 'return', canApprove: this.approvals.canApprove(), today: await this.businessDay.today() });
  }

  private async open(data: EmployeeDialogData): Promise<void> {
    const result = await firstValueFrom(
      this.dialog.open<EmployeeDialog, EmployeeDialogData, Employee | ApprovalResult>(EmployeeDialog, { data, width: '620px', maxWidth: '95vw' }).afterClosed(),
    );
    await this.saved(result, data.employee ? `${result && !isRequested(result) ? result.name : 'The employee'} saved.` : 'Employee added.');
  }

  private async employment(data: EmploymentDialogData): Promise<void> {
    const result = await firstValueFrom(
      this.dialog.open<EmploymentDialog, EmploymentDialogData, Employee | ApprovalResult>(EmploymentDialog, { data, width: '440px' }).afterClosed(),
    );
    await this.saved(result, data.mode === 'leave' ? `${data.employee.name} has left.` : `${data.employee.name} is back.`);
  }

  private async saved(result: Employee | ApprovalResult | undefined, done: string): Promise<void> {
    if (!result) {
      return;
    }
    if (isRequested(result)) {
      await this.approvals.requested(result);
    } else {
      this.notify.info(done);
    }
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.employees.set(await firstValueFrom(this.api.employees()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The employees could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
