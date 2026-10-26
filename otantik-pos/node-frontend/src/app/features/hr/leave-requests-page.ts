import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { errorMessage } from '../../core/http/error-message';
import { HrAccess } from '../../core/hr/hr-access';
import { Employee, HrApi, dayName } from '../../core/hr/hr.api';
import { LeaveBalance, LeaveRequest, LeavesApi, leaveDays, leaveStatus } from '../../core/hr/leaves.api';
import { NotifyService } from '../../core/ui/notify.service';
import { BusinessDay } from '../costing/financials';
import { CancelLeaveData, CancelLeaveDialog, LeaveRequestData, LeaveRequestDialog, RejectLeaveDialog } from './leave-dialogs';
import { PendingLeaves } from './pending-leaves';

// The leave requests (طلبات الإجازات): those waiting for a manager, the oldest first, each with
// what is left of its balance, approved or rejected at a click; and a year's decided ones, an
// approved one cancelled from a day on. Approving takes its days from the balance and marks them
// on the attendance. Whoever holds the tab sees them, and asks for leave for someone; deciding is a
// manager's alone.
@Component({
  selector: 'app-leave-requests-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <section aria-labelledby="pending-title">
      <div class="toolbar">
        <h2 id="pending-title">Waiting for a manager <span class="inline-ar">بانتظار الموافقة</span></h2>
        <span class="spacer"></span>
        @if (canAsk()) {
          <button mat-flat-button (click)="ask()"><mat-icon>add</mat-icon> Request leave for someone <span class="inline-ar">طلب إجازة</span></button>
        }
      </div>
      @if (!manager()) {
        <p class="note">A manager approves or rejects them.</p>
      }
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error(); as e) {
        <p class="error">{{ e }}</p>
      } @else {
        <div class="table-wrap">
          <table class="grid pending">
            <thead>
              <tr>
                <th>Employee<span class="ar">الموظف</span></th>
                <th>Type<span class="ar">النوع</span></th>
                <th>Dates<span class="ar">التاريخ</span></th>
                <th class="num">Days<span class="ar">الأيام</span></th>
                <th>Reason<span class="ar">السبب</span></th>
                <th>Asked<span class="ar">الطلب</span></th>
                @if (manager()) {
                  <th class="num"><span class="visually-hidden">Decide</span></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (r of pending(); track r.id) {
                <tr>
                  <td><strong>{{ r.employeeName }}</strong></td>
                  <td>{{ r.leaveTypeName }}@if (!r.isPaid) { <span class="badge">Unpaid</span> }</td>
                  <td class="nowrap">{{ dates(r) }}</td>
                  <td class="num">
                    {{ r.days }}
                    @if (balanceOf(r); as b) {
                      <div class="muted small" [class.short]="r.days > b.remainingDays">{{ b.remainingDays }} left</div>
                    }
                  </td>
                  <td [class.muted]="!r.reason">{{ r.reason ?? '—' }}</td>
                  <td class="muted small nowrap">{{ r.requestedBy }}<br />{{ r.requestedAtUtc | date: 'd MMM, HH:mm' }}</td>
                  @if (manager()) {
                    <td class="num actions">
                      <button mat-flat-button (click)="approve(r)" [disabled]="busy() === r.id" [attr.aria-label]="'Approve ' + r.employeeName + '’s leave'">
                        <mat-icon>check</mat-icon> Approve
                      </button>
                      <button mat-stroked-button class="danger" (click)="reject(r)" [disabled]="busy() === r.id" [attr.aria-label]="'Reject ' + r.employeeName + '’s leave'">
                        <mat-icon>close</mat-icon> Reject
                      </button>
                    </td>
                  }
                </tr>
              } @empty {
                @if (!loading()) {
                  <tr>
                    <td [attr.colspan]="manager() ? 7 : 6" class="muted"><mat-icon class="done">task_alt</mat-icon> No leave is waiting for a manager.</td>
                  </tr>
                }
              }
            </tbody>
          </table>
        </div>
      }
    </section>

    <section class="year-part" aria-labelledby="year-title">
      <div class="toolbar">
        <h2 id="year-title">The year’s leaves <span class="inline-ar">إجازات السنة</span></h2>
        <span class="spacer"></span>
        <button mat-icon-button (click)="moveYear(-1)" aria-label="The year before"><mat-icon>chevron_left</mat-icon></button>
        <strong class="year">{{ year() }}</strong>
        <button mat-icon-button (click)="moveYear(1)" aria-label="The year after"><mat-icon>chevron_right</mat-icon></button>
      </div>
      @if (manager()) {
        <p class="note">An approved leave is cancelled from a day on: its days from then are given back and unmarked, those before stay taken.</p>
      }
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Employee<span class="ar">الموظف</span></th>
              <th>Type<span class="ar">النوع</span></th>
              <th>Dates<span class="ar">التاريخ</span></th>
              <th class="num">Days<span class="ar">الأيام</span></th>
              <th>Status<span class="ar">الحالة</span></th>
              <th>Decided<span class="ar">القرار</span></th>
              @if (manager()) {
                <th class="num"><span class="visually-hidden">Cancel</span></th>
              }
            </tr>
          </thead>
          <tbody>
            @for (r of decided(); track r.id) {
              @let s = leaveStatus(r);
              <tr>
                <td><strong>{{ r.employeeName }}</strong></td>
                <td>{{ r.leaveTypeName }}@if (!r.isPaid) { <span class="badge">Unpaid</span> }</td>
                <td class="nowrap">{{ dates(r) }}</td>
                <td class="num">
                  {{ r.status === 'Approved' ? r.daysTaken : r.days }}
                  @if (r.cancelledFrom && r.status === 'Approved') {
                    <div class="muted small">of {{ r.days }}</div>
                  }
                </td>
                <td>
                  <span class="leave-status" [class]="s.key">{{ s.en }}<span class="ar">{{ s.ar }}</span></span>
                  @if (r.cancelledFrom) {
                    <div class="muted small">from {{ dayName(r.cancelledFrom) }} by {{ r.cancelledBy }}@if (r.cancellationNotes) { · {{ r.cancellationNotes }} }</div>
                  } @else if (r.managerNotes) {
                    <div class="muted small">{{ r.managerNotes }}</div>
                  }
                </td>
                <td class="muted small nowrap">{{ r.decidedBy }}@if (r.decidedAtUtc) { <br />{{ r.decidedAtUtc | date: 'd MMM, HH:mm' }} }</td>
                @if (manager()) {
                  <td class="num">
                    @if (r.status === 'Approved' && !r.cancelledFrom) {
                      <button mat-stroked-button (click)="cancel(r)" [disabled]="busy() === r.id" [attr.aria-label]="'Cancel ' + r.employeeName + '’s leave'">
                        <mat-icon>event_busy</mat-icon> Cancel from a day…
                      </button>
                    }
                  </td>
                }
              </tr>
            } @empty {
              @if (!loading()) {
                <tr>
                  <td [attr.colspan]="manager() ? 7 : 6" class="muted">No leave decided in {{ year() }}.</td>
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    </section>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    h2 {
      margin: 0;
      font: var(--mat-sys-title-medium);
    }
    .year-part {
      margin-top: 28px;
    }
    .year {
      min-width: 60px;
      text-align: center;
      font: var(--mat-sys-title-medium);
    }
    td.actions {
      white-space: nowrap;

      button {
        margin-left: 6px;
      }
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .short {
      color: var(--mat-sys-error);
    }
    .done {
      vertical-align: middle;
      color: #2e7d32;
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
    .leave-status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      white-space: nowrap;
      background: var(--mat-sys-surface-container-high);

      .ar {
        display: inline;
        margin-left: 4px;
      }
      &.Approved {
        background: color-mix(in srgb, var(--till-ok, #2e7d32) 18%, transparent);
      }
      &.Rejected {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
        color: var(--mat-sys-error);
      }
      &.CutShort {
        background: color-mix(in srgb, var(--till-warn) 22%, transparent);
      }
    }
  `,
})
export class LeaveRequestsPage {
  private readonly api = inject(LeavesApi);
  private readonly hr = inject(HrApi);
  private readonly auth = inject(AuthService);
  private readonly access = inject(HrAccess);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  private readonly pendingLeaves = inject(PendingLeaves);

  protected readonly dayName = dayName;
  protected readonly leaveStatus = leaveStatus;
  // Deciding is a manager's alone; asking for someone, whoever changes things on the tab.
  protected readonly manager = this.access.manager;
  protected readonly canAsk = computed(() => this.auth.can(Permissions.HrManage));

  protected readonly year = signal(new Date().getFullYear());
  protected readonly pending = signal<LeaveRequest[]>([]);
  protected readonly decided = signal<LeaveRequest[]>([]);
  private readonly balances = signal<LeaveBalance[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  // The request being decided, its buttons held until the till answers.
  protected readonly busy = signal<string | null>(null);
  private today = '';

  constructor() {
    void this.start();
  }

  protected dates(r: LeaveRequest): string {
    return r.startDate === r.endDate ? dayName(r.startDate) : `${dayName(r.startDate)} – ${dayName(r.endDate)}`;
  }

  // What is left of a paid request's balance, its year's.
  protected balanceOf(r: LeaveRequest): LeaveBalance | undefined {
    return r.isPaid ? this.balances().find((b) => b.employeeId === r.employeeId && b.leaveTypeId === r.leaveTypeId && b.year === Number(r.startDate.slice(0, 4))) : undefined;
  }

  protected moveYear(by: number): void {
    this.year.update((y) => y + by);
    void this.loadYear();
  }

  protected async ask(): Promise<void> {
    let employees: Employee[] = [];
    let types: { id: string; name: string; isPaid: boolean }[] = [];
    try {
      [employees, types] = await Promise.all([firstValueFrom(this.hr.employees()), firstValueFrom(this.api.types())]);
    } catch (error) {
      this.notify.error(error, 'The employees could not be loaded.');
      return;
    }
    const data: LeaveRequestData = {
      employees: employees.filter((e) => e.isActive),
      types,
      countDays: (id, start, end) => this.api.days(id!, start, end),
      balances: (id, year) => this.api.balances(year, id!),
      submit: (input) => this.api.submit({ ...input, employeeId: input.employeeId! }),
    };
    const made = await firstValueFrom(this.dialog.open<LeaveRequestDialog, LeaveRequestData, LeaveRequest>(LeaveRequestDialog, { data, width: '520px', maxWidth: '95vw' }).afterClosed());
    if (made) {
      this.notify.info(`Leave asked for ${made.employeeName}: ${leaveDays(made.days)} of ${made.leaveTypeName}, waiting for a manager.`);
      await this.reload();
    }
  }

  protected async approve(r: LeaveRequest): Promise<void> {
    await this.act(r, () => this.api.approve(r.id, null), (done) => `Approved: ${done.employeeName}’s ${leaveDays(done.days)} of ${done.leaveTypeName}, marked on the attendance.`);
  }

  protected async reject(r: LeaveRequest): Promise<void> {
    const notes = await firstValueFrom(this.dialog.open<RejectLeaveDialog, LeaveRequest, string>(RejectLeaveDialog, { data: r, width: '460px' }).afterClosed());
    if (notes !== undefined) {
      await this.act(r, () => this.api.reject(r.id, notes || null), (done) => `${done.employeeName}’s leave rejected.`);
    }
  }

  protected async cancel(r: LeaveRequest): Promise<void> {
    const data: CancelLeaveData = { request: r, today: this.today, countDays: (from) => this.api.days(r.employeeId, from, r.endDate) };
    const answer = await firstValueFrom(
      this.dialog.open<CancelLeaveDialog, CancelLeaveData, { from: string; notes: string | null }>(CancelLeaveDialog, { data, width: '480px' }).afterClosed(),
    );
    if (answer) {
      await this.act(
        r,
        () => this.api.cancel(r.id, answer.from, answer.notes),
        (done) =>
          done.status === 'Cancelled'
            ? `${done.employeeName}’s leave cancelled: all of it given back.`
            : `${done.employeeName}’s leave cut short from ${dayName(done.cancelledFrom!)}: ${leaveDays(done.cancelledDays)} given back.`,
      );
    }
  }

  // Does it, says so, and reads the lists again; a refusal is said as the till says it.
  private async act(r: LeaveRequest, call: () => Observable<LeaveRequest>, done: (made: LeaveRequest) => string): Promise<void> {
    this.busy.set(r.id);
    try {
      const made = await firstValueFrom(call());
      this.notify.info(done(made));
      await this.reload();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    } finally {
      this.busy.set(null);
    }
  }

  private async start(): Promise<void> {
    this.today = await this.businessDay.today();
    this.year.set(Number(this.today.slice(0, 4)));
    await this.reload();
  }

  private async reload(): Promise<void> {
    await Promise.all([this.loadPending(), this.loadYear()]);
  }

  private async loadPending(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const pending = await firstValueFrom(this.api.requests({ status: 'Pending' }));
      this.pending.set(pending);
      // The count on the tabs, as it stands now.
      this.pendingLeaves.count.set(pending.length);
      // The balances of the years asked for.
      const years = [...new Set(pending.filter((r) => r.isPaid).map((r) => Number(r.startDate.slice(0, 4))))];
      const lists = await Promise.all(years.map((y) => firstValueFrom(this.api.balances(y))));
      this.balances.set(lists.flat());
    } catch (error) {
      this.error.set(errorMessage(error, 'The leave requests could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private async loadYear(): Promise<void> {
    try {
      const year = this.year();
      const list = await firstValueFrom(this.api.requests({ year }));
      if (year === this.year()) {
        this.decided.set(list.filter((r) => r.status !== 'Pending'));
      }
    } catch {
      this.decided.set([]);
    }
  }
}
