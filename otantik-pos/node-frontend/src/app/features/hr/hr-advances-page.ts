import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { isRequested, SafeVoucherInput } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { AdvanceShare, AdvancesReport, AllocatedAdvance, Employee, HrApi, PendingAdvance } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from '../costing/approvals';
import { BusinessDay, isoDay, SafeVoucherDialog, SafeVoucherDialogData } from '../costing/financials';
import { DistributeAdvanceData, DistributeAdvanceDialog } from './distribute-advance-dialog';

// HR's advances (السلفيات), a payroll month at a time.
//
// Pending (وضع معلق): advances handed out with no one named, from the daily batch or a Financials
// voucher, each with how much is given out and what is left. A manager gives one out a share each
// (Distribute), some now and the rest later, and takes a share back while that pay is not
// finalized. Every one not all given out is listed, whatever its month; those all given out, in
// their month.
//
// Allocated: the month's advances, each off whose pay: paid to them, or their share of a lump sum.
//
// New advance records one paid outside the till (from the owner's own pocket, say), in the same
// dialog Financials pays one with, less where it is paid from: no cash leaves the safe or a day's
// takings, and Financials never sees it. It comes off the employee's pay, or, naming no one, is a
// lump sum to give out here. A manager's is recorded at once, anyone else's asked of a manager; a
// manager voids one while its pay is not finalized (a lump sum, once no share of it stands).
@Component({
  selector: 'app-hr-advances-page',
  imports: [DatePipe, MatButtonModule, MatButtonToggleModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="move(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="month">{{ month() | date: 'MMMM y' }}</strong>
      <button mat-icon-button (click)="move(1)" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
      <mat-button-toggle-group [value]="view()" (change)="view.set($event.value)" aria-label="View" hideSingleSelectionIndicator>
        <mat-button-toggle value="pending">
          Pending <span class="inline-ar">وضع معلق</span>
          @if (waiting().length) {
            <span class="count">{{ waiting().length }}</span>
          }
        </mat-button-toggle>
        <mat-button-toggle value="allocated">Allocated <span class="inline-ar">السلف الموزعة</span></mat-button-toggle>
      </mat-button-toggle-group>
      <span class="spacer"></span>
      @if (report(); as r) {
        <span class="muted">{{ allocatedTotal() | money }} off this month's pay · {{ leftTotal() | money }} to give out</span>
      }
      @if (canRecord()) {
        <button mat-flat-button (click)="newAdvance()"><mat-icon>add</mat-icon> New advance <span class="inline-ar">سلفة جديدة</span></button>
      }
    </div>
    @if (asked().length) {
      <ul class="waiting">
        @for (w of asked(); track w.id) {
          <li>
            <span class="pending-badge">{{ pendingLabel(w).en }}<span class="ar">{{ pendingLabel(w).ar }}</span></span>
            {{ w.summary }} <span class="muted small">· asked by {{ w.requestedBy }}</span>
          </li>
        }
      </ul>
    }
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      @if (view() === 'pending') {
        <p class="muted">
          Advances handed out with no one named, in the daily batch or a Financials voucher, wait here to be given out, a share each. Each share comes
          off its employee's pay.
        </p>
        @for (p of r.pending; track p.id) {
          <section class="lump" [class.done]="p.fullyAllocated" [attr.aria-label]="'Advances of ' + (p.date | date: 'd MMM')">
            <header>
              <div>
                <strong>{{ p.amount | money }}</strong> handed out {{ p.date | date: 'd MMM' }}
                <span class="muted">
                  · @if (p.outsideTill) {
                    outside the till
                  } @else if (p.fromTakingsOf) {
                    from the takings of {{ p.fromTakingsOf | date: 'd MMM' }}
                  } @else {
                    from the safe
                  }
                </span>
                <div class="muted small">{{ p.notes ? p.notes + ' · ' : '' }}entered by {{ p.recordedBy }}</div>
              </div>
              <span class="spacer"></span>
              @if (p.fullyAllocated) {
                <span class="status ok">Fully allocated<span class="ar">تم التوزيع بالكامل</span></span>
              } @else {
                <span class="status left">{{ p.remaining | money }} left<span class="ar">متبقي</span></span>
              }
              @if (canDistribute() && !p.fullyAllocated) {
                <button mat-flat-button (click)="distribute(p)"><mat-icon>call_split</mat-icon> Distribute <span class="inline-ar">توزيع</span></button>
              }
              @if (canDistribute() && p.outsideTill && !p.shares.length) {
                <button mat-icon-button class="danger" (click)="voidOutside(p.id, p.amount)" matTooltip="Void" aria-label="Void these advances">
                  <mat-icon>delete</mat-icon>
                </button>
              }
            </header>
            <div class="bar" role="progressbar" [attr.aria-valuenow]="p.allocated" aria-valuemin="0" [attr.aria-valuemax]="p.amount">
              <span [style.width.%]="(p.allocated / p.amount) * 100"></span>
            </div>
            @if (p.shares.length) {
              <div class="table-wrap">
                <table class="grid">
                  <thead>
                    <tr>
                      <th>Employee<span class="ar">الموظف</span></th>
                      <th>Off the pay of<span class="ar">يُخصم من راتب</span></th>
                      <th class="num">Share<span class="ar">الحصة</span></th>
                      <th>Given out by<span class="ar">بواسطة</span></th>
                      <th class="num"><span class="visually-hidden">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (s of p.shares; track s.id) {
                      <tr>
                        <td>{{ s.employee }}@if (s.notes) {<div class="muted small">{{ s.notes }}</div>}</td>
                        <td class="nowrap">{{ s.payrollMonth | date: 'MMMM y' }}</td>
                        <td class="num">{{ s.amount | money }}</td>
                        <td>{{ s.allocatedBy }}<div class="muted small">{{ s.allocatedAtUtc | date: 'd MMM, HH:mm' }}</div></td>
                        <td class="num">
                          @if (canDistribute()) {
                            <button mat-icon-button (click)="takeBack(s)" matTooltip="Take it back, to give out again" [attr.aria-label]="'Take back ' + s.employee + '\\'s share'">
                              <mat-icon>undo</mat-icon>
                            </button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="muted small">Not given out to anyone yet.</p>
            }
          </section>
        } @empty {
          <p class="muted">No advances wait to be given out.</p>
        }
      } @else {
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Date<span class="ar">التاريخ</span></th>
                <th>Employee<span class="ar">الموظف</span></th>
                <th>How<span class="ar">النوع</span></th>
                <th class="num">Amount<span class="ar">المبلغ</span></th>
                <th>By<span class="ar">بواسطة</span></th>
                <th class="num"><span class="visually-hidden">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              @for (a of r.allocated; track a.id) {
                <tr>
                  <td class="nowrap">{{ a.date | date: 'd MMM' }}</td>
                  <td>{{ a.employee }}@if (a.notes) {<div class="muted small">{{ a.notes }}</div>}</td>
                  <td>
                    @if (a.origin === 'Share') {
                      Share of the advances of {{ a.date | date: 'd MMM' }}<span class="ar">حصة من سلفة</span>
                    } @else {
                      Paid to them<span class="ar">سلفة مباشرة</span>
                    }
                    <div class="muted small">
                      @if (a.outsideTill) {
                        Outside the till · من خارج الخزنة
                      } @else if (a.fromTakingsOf) {
                        From the takings of {{ a.fromTakingsOf | date: 'd MMM' }}
                      } @else {
                        From the safe
                      }
                    </div>
                  </td>
                  <td class="num">{{ a.amount | money }}</td>
                  <td>{{ a.recordedBy }}</td>
                  <td class="num">
                    @if (canDistribute() && a.origin === 'Outside') {
                      <button mat-icon-button class="danger" (click)="voidOutside(a.id, a.amount, a)" matTooltip="Void" [attr.aria-label]="'Void the advance to ' + a.employee">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="muted">No advances come off this month's pay.</td></tr>
              }
            </tbody>
            @if (r.allocated.length) {
              <tfoot>
                <tr>
                  <td colspan="3">Off {{ month() | date: 'MMMM' }}'s pay · <a routerLink="/hr/payroll" [queryParams]="{ month: month() }">Payroll</a></td>
                  <td class="num">{{ allocatedTotal() | money }}</td>
                  <td colspan="2"></td>
                </tr>
              </tfoot>
            }
          </table>
        </div>
      }
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .month {
      min-width: 120px;
      text-align: center;
    }
    mat-button-toggle-group {
      margin-left: 12px;
    }
    .count {
      margin-left: 6px;
      padding: 0 6px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--till-warn);
      color: #000;
    }
    .lump {
      margin: 12px 0;
      padding: 12px 16px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      &.done {
        opacity: 0.85;
      }
      header {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px 12px;
      }
    }
    .bar {
      height: 6px;
      margin: 10px 0;
      border-radius: 999px;
      background: var(--mat-sys-surface-container-high);
      overflow: hidden;

      span {
        display: block;
        height: 100%;
        background: var(--mat-sys-primary);
      }
    }
    .status {
      display: inline-block;
      padding: 2px 10px;
      border-radius: 999px;
      font: var(--mat-sys-label-medium);

      &.ok {
        background: color-mix(in srgb, #2e7d32 16%, transparent);
      }
      &.left {
        background: color-mix(in srgb, var(--till-warn) 30%, transparent);
      }
    }
    .small {
      font-size: 0.85em;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .waiting {
      margin: 8px 0;
      padding-left: 18px;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class HrAdvancesPage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);
  private readonly auth = inject(AuthService);
  private readonly businessDay = inject(BusinessDay);

  protected readonly canDistribute = this.approvals.canApprove;
  // Whoever manages HR records one; anyone but a manager, for a manager to approve.
  protected readonly canRecord = computed(() => this.auth.can(Permissions.HrManage));
  protected readonly asked = computed(() => this.approvals.waitingToBeMade('Advance'));
  protected readonly pendingLabel = pendingLabel;
  protected readonly month = signal(isoDay(new Date(new Date().getFullYear(), new Date().getMonth(), 1)));
  protected readonly view = signal<'pending' | 'allocated'>('pending');
  protected readonly report = signal<AdvancesReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly employees = signal<Employee[]>([]);

  protected readonly waiting = computed(() => (this.report()?.pending ?? []).filter((p) => !p.fullyAllocated));
  protected readonly allocatedTotal = computed(() => round((this.report()?.allocated ?? []).reduce((sum, a) => sum + a.amount, 0)));
  protected readonly leftTotal = computed(() => round(this.waiting().reduce((sum, p) => sum + p.remaining, 0)));

  constructor() {
    void this.load();
    void this.approvals.refresh();
    void firstValueFrom(this.api.employees()).then(
      (e) => this.employees.set(e),
      () => undefined,
    );
  }

  protected move(by: number): void {
    const start = new Date(`${this.month()}T12:00:00`);
    this.month.set(isoDay(new Date(start.getFullYear(), start.getMonth() + by, 1)));
    void this.load();
  }

  // Given out to those employed on the day it was handed out.
  protected async distribute(advance: PendingAdvance): Promise<void> {
    const employees = this.employees().filter((e) => e.hiredOn <= advance.date && (!e.leftOn || e.leftOn >= advance.date));
    const given = await firstValueFrom(
      this.dialog
        .open<DistributeAdvanceDialog, DistributeAdvanceData, PendingAdvance>(DistributeAdvanceDialog, {
          data: { advance, employees },
          width: '720px',
          maxWidth: '95vw',
        })
        .afterClosed(),
    );
    if (given) {
      this.notify.info(
        given.fullyAllocated
          ? `All ${money(given.amount)} given out: each share comes off its employee's pay.`
          : `Given out: ${money(given.remaining)} of it is left to give out.`,
      );
      await this.load();
    }
  }

  // Paid outside the till: asked for in the dialog Financials pays one with, recorded in HR.
  protected async newAdvance(): Promise<void> {
    const voucher = await firstValueFrom(
      this.dialog
        .open<SafeVoucherDialog, SafeVoucherDialogData, SafeVoucherInput>(SafeVoucherDialog, {
          data: { kind: 'EmployeeAdvance', outsideTill: true, canApprove: this.approvals.canApprove(), today: await this.businessDay.today() },
          width: '520px',
          maxWidth: '95vw',
        })
        .afterClosed(),
    );
    if (!voucher) {
      return;
    }
    try {
      const result = await firstValueFrom(
        this.api.recordHrAdvance({
          advanceId: voucher.transactionId, requestId: voucher.requestId, amount: voucher.amount, date: voucher.date ?? (await this.businessDay.today()),
          employeeId: voucher.employeeId ?? null, notes: voucher.notes,
        }),
      );
      if (isRequested(result)) {
        await this.approvals.requested(result);
      } else {
        this.notify.info(
          result.employee
            ? `${money(result.amount)} recorded outside the till: it comes off ${result.employee}'s pay.`
            : `${money(result.amount)} recorded outside the till: give it out below.`,
        );
      }
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The advance could not be recorded.');
    }
  }

  protected async voidOutside(id: string, amount: number, advance?: AllocatedAdvance): Promise<void> {
    try {
      await firstValueFrom(this.api.voidHrAdvance(id));
      this.notify.info(advance ? `The advance to ${advance.employee} is voided: it no longer comes off their pay.` : `${money(amount)} of advances voided.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The advance could not be voided.');
    }
  }

  protected async takeBack(share: AdvanceShare): Promise<void> {
    try {
      await firstValueFrom(this.api.takeBackAdvanceShare(share.id));
      this.notify.info(`${share.employee}'s ${money(share.amount)} is taken back, to give out again.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The share could not be taken back.');
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.report.set(await firstValueFrom(this.api.advances(this.month())));
      this.error.set(null);
    } catch (error) {
      this.error.set(errorMessage(error, 'The advances could not be read.'));
    } finally {
      this.loading.set(false);
    }
  }
}

function round(amount: number): number {
  return Math.round(amount * 100) / 100;
}

function money(amount: number): string {
  return `L.E ${amount.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}
