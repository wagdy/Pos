import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { HrAccess } from '../../core/hr/hr-access';
import { LeaveType, LeavesApi } from '../../core/hr/leaves.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { LeaveTypeAnswer, LeaveTypeDialog } from './leave-dialogs';

// The kinds of leave (أنواع الإجازات), a manager's: paid ones with their days a year, unpaid
// ones with none. One asked for or kept a balance of stays paid or unpaid as it is, and is not
// taken off.
@Component({
  selector: 'app-leave-types-page',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="toolbar">
      <span class="spacer"></span>
      @if (manager()) {
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add a leave type <span class="inline-ar">إضافة نوع</span></button>
      }
    </div>
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
              <th>Type<span class="ar">النوع</span></th>
              <th>Pay<span class="ar">الأجر</span></th>
              <th class="num">Days a year<span class="ar">أيام في السنة</span></th>
              <th>New hires<span class="ar">المعيّن الجديد</span></th>
              @if (manager()) {
                <th class="num"><span class="visually-hidden">Change</span></th>
              }
            </tr>
          </thead>
          <tbody>
            @for (t of types(); track t.id) {
              <tr>
                <td><strong>{{ t.name }}</strong>@if (t.inUse) { <span class="muted small"> · in use</span> }</td>
                <td>
                  @if (t.isPaid) {
                    Paid<span class="inline-ar">مدفوعة</span>
                  } @else {
                    <span class="badge">Unpaid<span class="inline-ar">بدون راتب</span></span>
                  }
                </td>
                <td class="num">{{ t.isPaid ? t.defaultDaysPerYear : '—' }}</td>
                <td [class.muted]="!t.proratedForNewHires">{{ t.isPaid ? (t.proratedForNewHires ? 'Their share of the year' : 'All of it') : '—' }}</td>
                @if (manager()) {
                  <td class="num">
                    <button mat-icon-button (click)="change(t)" matTooltip="Change{{ t.inUse ? '' : ' or take off' }}" [attr.aria-label]="'Change ' + t.name">
                      <mat-icon>edit</mat-icon>
                    </button>
                  </td>
                }
              </tr>
            } @empty {
              @if (!loading()) {
                <tr><td [attr.colspan]="manager() ? 5 : 4" class="muted">No leave types yet.</td></tr>
              }
            }
          </tbody>
        </table>
      </div>
      @if (!manager()) {
        <p class="note">A manager adds and changes them.</p>
      }
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .small {
      font-size: 0.85em;
    }
    .note {
      margin-top: 12px;
    }
  `,
})
export class LeaveTypesPage {
  private readonly api = inject(LeavesApi);
  private readonly access = inject(HrAccess);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly manager = this.access.manager;
  protected readonly types = signal<LeaveType[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected async add(): Promise<void> {
    await this.edit(crypto.randomUUID(), null, 'Leave type added.');
  }

  protected async change(t: LeaveType): Promise<void> {
    await this.edit(t.id, t, `${t.name} saved.`);
  }

  private async edit(id: string, type: LeaveType | null, done: string): Promise<void> {
    const answer = await firstValueFrom(
      this.dialog.open<LeaveTypeDialog, LeaveType | null, LeaveTypeAnswer>(LeaveTypeDialog, { data: type, width: '460px' }).afterClosed(),
    );
    if (!answer) {
      return;
    }
    if ('remove' in answer) {
      await this.act(() => this.api.deleteType(id), `${type?.name ?? 'The type'} taken off.`);
    } else {
      await this.act(() => this.api.saveType(id, answer), done);
    }
  }

  // Does it, says so, and reads the list again; a refusal is said as the till says it.
  private async act(call: () => Observable<unknown>, done: string): Promise<void> {
    try {
      await firstValueFrom(call());
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
      this.types.set(await firstValueFrom(this.api.types()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The leave types could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
