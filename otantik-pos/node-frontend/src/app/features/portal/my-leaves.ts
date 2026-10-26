import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom, of } from 'rxjs';
import { dayName } from '../../core/hr/hr.api';
import { LeaveRequest, leaveDays, leaveStatus } from '../../core/hr/leaves.api';
import { errorMessage } from '../../core/http/error-message';
import { EmployeeLeaves, PortalApi } from '../../core/portal/portal.api';
import { NotifyService } from '../../core/ui/notify.service';
import { LeaveRequestData, LeaveRequestDialog } from '../hr/leave-dialogs';

// The portal's My leaves (إجازاتي): what is left of each paid type this year, asking for leave
// (the working days it takes shown first), and their requests, the latest first, with where each
// stands and what the manager said.
@Component({
  selector: 'app-my-leaves',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="head">
      <h2>My leaves <span class="ar">إجازاتي</span></h2>
      <span class="spacer"></span>
      @if (leaves()) {
        <button mat-flat-button (click)="ask()"><mat-icon>add</mat-icon> Request leave <span class="ar">طلب إجازة</span></button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as e) {
      <p class="error">{{ e }}</p>
    } @else if (leaves(); as l) {
      <div class="balances" [attr.aria-label]="'Balances ' + l.year">
        @for (b of l.balances; track b.leaveTypeId) {
          <div class="balance">
            <span class="label">{{ b.leaveTypeName }} {{ l.year }}</span>
            <span><strong>{{ b.remainingDays }}</strong> of {{ b.allocatedDays }} days left</span>
            @if (b.pendingDays) {
              <span class="muted small">{{ b.pendingDays }} asked for, waiting</span>
            }
          </div>
        }
      </div>
      @if (l.requests.length) {
        <div class="history">
          <table>
            <thead>
              <tr>
                <th>Dates<span class="ar">التاريخ</span></th>
                <th>Type<span class="ar">النوع</span></th>
                <th class="num">Days<span class="ar">الأيام</span></th>
                <th>Status<span class="ar">الحالة</span></th>
              </tr>
            </thead>
            <tbody>
              @for (r of l.requests; track r.id) {
                @let s = leaveStatus(r);
                <tr>
                  <td>
                    <span class="nowrap">{{ dayName(r.startDate) }}</span>
                    @if (r.endDate !== r.startDate) {
                      – <span class="nowrap">{{ dayName(r.endDate) }}</span>
                    }
                  </td>
                  <td>{{ r.leaveTypeName }}</td>
                  <td class="num">{{ r.status === 'Approved' ? r.daysTaken : r.days }}</td>
                  <td>
                    <span class="leave-status" [class]="s.key">{{ s.en }}<span class="ar">{{ s.ar }}</span></span>
                    @if (r.cancelledFrom) {
                      <div class="muted small">from {{ dayName(r.cancelledFrom) }}@if (r.cancellationNotes) { · {{ r.cancellationNotes }} }</div>
                    } @else if (r.managerNotes) {
                      <div class="muted small">{{ r.managerNotes }}</div>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <p class="muted">You have not asked for leave yet.</p>
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 10px;

      h2 {
        margin: 0;
        font: var(--mat-sys-title-medium);
      }
    }
    .spacer {
      flex: 1;
    }
    .balances {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
      gap: 8px;
      margin-bottom: 12px;
    }
    .balance {
      display: grid;
      gap: 2px;
      padding: 10px 12px;
      border-radius: 10px;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);

      .label {
        font: var(--mat-sys-label-medium);
      }
      strong {
        font: var(--mat-sys-headline-small);
      }
    }
    /* Its own scroll on a narrow phone, never the page's. */
    .history {
      overflow-x: auto;
    }
    table {
      width: 100%;
      border-collapse: collapse;

      th {
        text-align: left;
        font: var(--mat-sys-label-small);
        color: var(--mat-sys-on-surface-variant);
        padding: 0 8px 6px 0;
      }
      td {
        padding: 8px 8px 8px 0;
        border-top: 1px solid var(--mat-sys-outline-variant);
        vertical-align: top;
      }
      .num {
        text-align: right;
        font-variant-numeric: tabular-nums;
      }
    }
    .leave-status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      white-space: nowrap;
      background: var(--mat-sys-surface-container-high);

      &.Pending {
        background: color-mix(in srgb, var(--till-warn) 22%, transparent);
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
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    th .ar {
      display: block;
      margin-left: 0;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class MyLeaves {
  private readonly api = inject(PortalApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly dayName = dayName;
  protected readonly leaveStatus = leaveStatus;
  protected readonly leaves = signal<EmployeeLeaves | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected async ask(): Promise<void> {
    const l = this.leaves()!;
    const data: LeaveRequestData = {
      types: l.types,
      countDays: (_, start, end) => this.api.leaveDays(start, end),
      // This year's they have; another year's is checked when it is asked for.
      balances: (_, year) => of(year === l.year ? l.balances : []),
      submit: ({ leaveTypeId, startDate, endDate, reason }) => this.api.requestLeave({ leaveTypeId, startDate, endDate, reason }),
    };
    const made = await firstValueFrom(this.dialog.open<LeaveRequestDialog, LeaveRequestData, LeaveRequest>(LeaveRequestDialog, { data, width: '520px', maxWidth: '95vw' }).afterClosed());
    if (made) {
      this.notify.info(`Asked for ${leaveDays(made.days)} of ${made.leaveTypeName}: your manager decides.`);
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.leaves.set(await firstValueFrom(this.api.leaves()));
      this.error.set(null);
    } catch (error) {
      this.error.set(errorMessage(error, 'Your leaves could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
