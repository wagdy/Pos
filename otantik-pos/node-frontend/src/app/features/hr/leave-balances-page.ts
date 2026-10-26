import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { searchKey } from '../../core/hr/employee-picker';
import { HrAccess } from '../../core/hr/hr-access';
import { LeaveBalance, LeavesApi } from '../../core/hr/leaves.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { BusinessDay } from '../costing/financials';
import { LeaveBalanceDialog } from './leave-dialogs';

// Everyone's leave balances in a year (أرصدة الإجازات): a row each employee employed in it, a
// column each paid type, what is left of what they are given, taken and asked for. A year opens at
// each type's days (a new hire's share of a shared one) and carries nothing over; a manager
// changes a balance's days, in whole or half days, never below what is taken.
@Component({
  selector: 'app-leave-balances-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="moveYear(-1)" aria-label="The year before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="year">{{ year() }}</strong>
      <button mat-icon-button (click)="moveYear(1)" aria-label="The year after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      <mat-form-field subscriptSizing="dynamic" class="search">
        <mat-label>Find · بحث</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" placeholder="A name" aria-label="Find an employee" />
      </mat-form-field>
    </div>
    <p class="note">
      Each year opens afresh on 1 January at each type’s days, nothing carried over; a new hire’s first year at their share of a type shared so.
      @if (manager()) {
        Change a balance’s days with its pencil, in whole or half days.
      }
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as e) {
      <p class="error">{{ e }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Employee<span class="ar">الموظف</span></th>
              @for (t of types(); track t.id) {
                <th class="num">{{ t.name }}<span class="ar">المتبقي من الرصيد</span></th>
              }
            </tr>
          </thead>
          <tbody>
            @for (row of shown(); track row.employeeId) {
              <tr>
                <td><strong>{{ row.name }}</strong></td>
                @for (b of row.balances; track b.leaveTypeId) {
                  <td class="num cell">
                    <span class="left" [class.none]="b.remainingDays <= 0"><strong>{{ b.remainingDays }}</strong> of {{ b.allocatedDays }}</span>
                    @if (manager()) {
                      <button mat-icon-button class="edit" (click)="change(b)" matTooltip="Change the days"
                        [attr.aria-label]="'Change ' + b.employeeName + '’s ' + b.leaveTypeName + ' days'">
                        <mat-icon>edit</mat-icon>
                      </button>
                    }
                    <div class="muted small">
                      {{ b.usedDays }} taken@if (b.pendingDays) { · {{ b.pendingDays }} asked}@if (!b.opened) { · <span matTooltip="Not kept yet: as the year opens">as it opens</span>}
                    </div>
                  </td>
                }
              </tr>
            } @empty {
              @if (!loading()) {
                <tr>
                  <td [attr.colspan]="types().length + 1" class="muted">
                    {{ rows().length ? 'No one by that name.' : 'No balances in ' + year() + ': no one employed, or no paid leave type.' }}
                  </td>
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .year {
      min-width: 60px;
      text-align: center;
      font: var(--mat-sys-title-medium);
    }
    .search {
      width: 220px;
    }
    .cell {
      white-space: nowrap;
    }
    .left {
      font-variant-numeric: tabular-nums;

      &.none strong {
        color: var(--mat-sys-error);
      }
    }
    .edit {
      vertical-align: middle;
      margin-right: -8px;
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class LeaveBalancesPage {
  private readonly api = inject(LeavesApi);
  private readonly access = inject(HrAccess);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);

  protected readonly manager = this.access.manager;
  protected readonly year = signal(new Date().getFullYear());
  protected readonly search = signal('');
  protected readonly balances = signal<LeaveBalance[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  // The paid types, in the order the balances list them.
  protected readonly types = computed(() => {
    const seen = new Map<string, { id: string; name: string }>();
    for (const b of this.balances()) {
      if (!seen.has(b.leaveTypeId)) {
        seen.set(b.leaveTypeId, { id: b.leaveTypeId, name: b.leaveTypeName });
      }
    }
    return [...seen.values()];
  });

  // A row each employee, their balances in the types' order.
  protected readonly rows = computed(() => {
    const rows = new Map<string, { employeeId: string; name: string; balances: LeaveBalance[] }>();
    for (const b of this.balances()) {
      const row = rows.get(b.employeeId) ?? { employeeId: b.employeeId, name: b.employeeName, balances: [] };
      row.balances.push(b);
      rows.set(b.employeeId, row);
    }
    return [...rows.values()];
  });

  protected readonly shown = computed(() => {
    const key = searchKey(this.search());
    return key ? this.rows().filter((r) => searchKey(r.name).includes(key)) : this.rows();
  });

  constructor() {
    void this.start();
  }

  protected moveYear(by: number): void {
    this.year.update((y) => y + by);
    void this.load();
  }

  protected async change(b: LeaveBalance): Promise<void> {
    const days = await firstValueFrom(this.dialog.open<LeaveBalanceDialog, LeaveBalance, number>(LeaveBalanceDialog, { data: b, width: '440px' }).afterClosed());
    if (days === undefined) {
      return;
    }
    try {
      const saved = await firstValueFrom(this.api.setBalance(b.employeeId, b.leaveTypeId, b.year, days));
      this.balances.update((list) => list.map((x) => (x.employeeId === saved.employeeId && x.leaveTypeId === saved.leaveTypeId ? saved : x)));
      this.notify.info(`${saved.employeeName}’s ${saved.leaveTypeName}: ${saved.allocatedDays} days in ${saved.year}.`);
    } catch (error) {
      this.notify.error(error, 'The balance could not be saved.');
    }
  }

  private async start(): Promise<void> {
    this.year.set(Number((await this.businessDay.today()).slice(0, 4)));
    await this.load();
  }

  private async load(): Promise<void> {
    const year = this.year();
    this.loading.set(true);
    this.error.set(null);
    try {
      const list = await firstValueFrom(this.api.balances(year));
      if (year === this.year()) {
        this.balances.set(list);
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The balances could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
