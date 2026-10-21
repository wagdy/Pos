import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { HrApi, PublicHoliday, dayName, egyptFixedHolidays } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { HolidayAnswer, HolidayData, HolidayDialog } from './holiday-dialog';

// Public holidays (العطلات الرسمية), a year at a time: everyone's paid day off, entered by a
// manager as the government announces them. Egypt's fixed-date holidays not entered yet are
// offered, each to be checked and added; the ones that follow the moon are entered once announced.
// A month whose pay is finalized keeps its holidays as they were.
@Component({
  selector: 'app-holidays-page',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="toolbar">
      <button mat-icon-button (click)="moveYear(-1)" aria-label="The year before"><mat-icon>chevron_left</mat-icon></button>
      <strong class="year">{{ year() }}</strong>
      <button mat-icon-button (click)="moveYear(1)" aria-label="The year after"><mat-icon>chevron_right</mat-icon></button>
      <span class="spacer"></span>
      @if (canChange()) {
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add a holiday <span class="inline-ar">إضافة عطلة</span></button>
      }
    </div>
    <p class="note">
      Everyone's paid day off: not working one is never an absence. Whoever works it is measured against their shift as on any day, and earns a
      day's pay on top for half a shift or more, its hours for less. One on someone's weekly day off is paid once.
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Holiday<span class="ar">العطلة</span></th>
              <th>Dates<span class="ar">التاريخ</span></th>
              <th class="num">Days<span class="ar">الأيام</span></th>
              <th>Set by<span class="ar">بواسطة</span></th>
              @if (canChange()) {
                <th class="num"><span class="visually-hidden">Change</span></th>
              }
            </tr>
          </thead>
          <tbody>
            @for (h of holidays(); track h.id) {
              <tr>
                <td><strong>{{ h.name }}</strong></td>
                <td class="nowrap">{{ dates(h.firstDay, h.lastDay) }}</td>
                <td class="num">{{ h.days }}</td>
                <td class="muted">{{ h.setBy }}</td>
                @if (canChange()) {
                  <td class="num">
                    <button mat-icon-button (click)="change(h)" matTooltip="Change or take off" [attr.aria-label]="'Change ' + h.name">
                      <mat-icon>edit</mat-icon>
                    </button>
                  </td>
                }
              </tr>
            } @empty {
              @if (!loading()) {
                <tr>
                  <td [attr.colspan]="canChange() ? 5 : 4" class="muted">No holidays entered for {{ year() }}.</td>
                </tr>
              }
            }
          </tbody>
        </table>
      </div>

      @if (suggested().length) {
        <section class="suggested" aria-labelledby="suggested-title">
          <h3 id="suggested-title">Egypt's fixed-date holidays not entered yet <span class="inline-ar">العطلات الثابتة</span></h3>
          <p class="note">
            Check each against the government's announcement, as some are moved to a Thursday. The Eids, Arafat, the Hijri New Year and the Prophet's
            Birthday follow the moon: add them once announced.
          </p>
          <ul>
            @for (s of suggested(); track s.name) {
              <li>
                <span><strong>{{ s.name }}</strong><span class="inline-ar">{{ s.ar }}</span></span>
                <span class="muted nowrap">{{ dayName(s.day) }}</span>
                @if (canChange()) {
                  <button mat-stroked-button (click)="add(s.name, s.day)"><mat-icon>add</mat-icon> Add</button>
                }
              </li>
            }
          </ul>
        </section>
      }
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .year {
      min-width: 60px;
      text-align: center;
      font: var(--mat-sys-title-medium);
    }
    .suggested {
      margin-top: 24px;

      h3 {
        margin: 0 0 4px;
        font: var(--mat-sys-title-small);
      }
      ul {
        list-style: none;
        margin: 0;
        padding: 0;
        display: grid;
        gap: 6px;
      }
      li {
        display: grid;
        grid-template-columns: 1fr auto auto;
        align-items: center;
        gap: 12px;
        padding: 6px 12px;
        border-radius: 12px;
        border: 1px solid var(--mat-sys-outline-variant);
      }
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class HolidaysPage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);
  private readonly businessDay = inject(BusinessDay);

  protected readonly dayName = dayName;
  protected readonly canChange = this.approvals.canApprove;
  protected readonly year = signal(new Date().getFullYear());
  protected readonly holidays = signal<PublicHoliday[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  // The fixed-date holidays of the year that no holiday entered covers or is named as.
  protected readonly suggested = computed(() => {
    const entered = this.holidays();
    return egyptFixedHolidays(this.year()).filter(
      (s) => !entered.some((h) => (h.firstDay <= s.day && h.lastDay >= s.day) || h.name.trim().toLowerCase() === s.name.toLowerCase()),
    );
  });

  constructor() {
    void this.start();
  }

  protected dates(first: string, last: string): string {
    return first === last ? dayName(first) : `${dayName(first)} – ${dayName(last)}`;
  }

  protected moveYear(by: number): void {
    this.year.update((y) => y + by);
    void this.load();
  }

  protected async add(name?: string, day?: string): Promise<void> {
    await this.edit(crypto.randomUUID(), { name, firstDay: day, lastDay: day }, 'Holiday added.');
  }

  protected async change(h: PublicHoliday): Promise<void> {
    await this.edit(h.id, { name: h.name, firstDay: h.firstDay, lastDay: h.lastDay, existing: true }, 'Holiday changed.');
  }

  private async edit(id: string, data: HolidayData, done: string): Promise<void> {
    const answer = await firstValueFrom(this.dialog.open<HolidayDialog, HolidayData, HolidayAnswer>(HolidayDialog, { data, width: '460px' }).afterClosed());
    if (!answer) {
      return;
    }
    if ('remove' in answer) {
      await this.act(() => this.api.deleteHoliday(id), 'Holiday taken off.');
    } else {
      await this.act(() => this.api.saveHoliday(id, answer.name, answer.firstDay, answer.lastDay), done);
    }
  }

  // Does it, says so, and reads the year again; a refusal is said as the till says it.
  private async act(call: () => Observable<unknown>, done: string): Promise<void> {
    try {
      await firstValueFrom(call());
      this.notify.info(done);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    }
  }

  private async start(): Promise<void> {
    this.year.set(Number((await this.businessDay.today()).slice(0, 4)));
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.holidays.set(await firstValueFrom(this.api.holidays(this.year())));
    } catch (error) {
      this.error.set(errorMessage(error, 'The holidays could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
