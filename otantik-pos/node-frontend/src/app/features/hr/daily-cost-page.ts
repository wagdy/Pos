import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { DailyLabourCost, HrApi, duration } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { BusinessDay, isoDay } from '../costing/financials';

// A day's labour cost (تكلفة يوم), a quick reference: everyone who punched in on the day, each at
// their day's rate (a monthly salary over 30, or their daily wage), totalled and by department.
// That day's overtime pay is said beside it, not in the total; the payslips deduct lateness and
// absence and add allowances at the month's end. Opens on the business day, so far; no day after it.
@Component({
  selector: 'app-daily-cost-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe],
  template: `
    <div class="toolbar no-print">
      <button mat-icon-button (click)="move(-1)" aria-label="The day before"><mat-icon>chevron_left</mat-icon></button>
      <mat-form-field subscriptSizing="dynamic" class="day">
        <mat-label>Day · اليوم</mat-label>
        <input matInput type="date" [ngModel]="date()" (ngModelChange)="choose($event)" [max]="today()" aria-label="Day" />
      </mat-form-field>
      <button mat-icon-button (click)="move(1)" [disabled]="date() >= today()" aria-label="The day after"><mat-icon>chevron_right</mat-icon></button>
      @if (date() === today()) {
        <span class="muted">Today, so far</span>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as problem) {
      <p class="error">{{ problem }}</p>
    } @else if (cost(); as c) {
      <header class="report-head">
        <h2>Daily cost · {{ c.date | date: 'EEEE d MMMM y' }} <span class="inline-ar">تكلفة يوم</span></h2>
      </header>
      <div class="tiles">
        <div class="tile main">
          <span class="label">Labour cost · تكلفة العمالة</span>
          <strong>{{ c.cost | money }}</strong>
          <small class="muted">each who came in, at their day's rate</small>
        </div>
        <div class="tile">
          <span class="label">Came in · حضروا</span>
          <strong>{{ c.worked }} <span class="of">of {{ c.employed }}</span></strong>
          <small class="muted">employed on the day</small>
        </div>
        <div class="tile">
          <span class="label">Overtime · الإضافي</span>
          <strong>{{ c.overtimePay | money }}</strong>
          <small class="muted">paid on top, not in the cost</small>
        </div>
      </div>

      @if (c.worked) {
        <h3>By department <span class="inline-ar">حسب القسم</span></h3>
        <div class="table-wrap narrow">
          <table class="grid departments" aria-label="By department">
            <thead>
              <tr>
                <th>Department<span class="ar">القسم</span></th>
                <th class="num">Came in<span class="ar">حضروا</span></th>
                <th class="num">Cost<span class="ar">التكلفة</span></th>
                <th class="num">Share<span class="ar">النسبة</span></th>
                <th class="num">Overtime<span class="ar">الإضافي</span></th>
              </tr>
            </thead>
            <tbody>
              @for (d of c.departments; track d.departmentId) {
                <tr>
                  <td><strong>{{ d.name }}</strong></td>
                  <td class="num">{{ d.worked }}</td>
                  <td class="num">{{ d.cost | money }}</td>
                  <td class="num">{{ share(d.cost) }}</td>
                  <td class="num">{{ d.overtimePay | money }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <h3>Who came in <span class="inline-ar">الحضور</span></h3>
        <div class="table-wrap">
          <table class="grid employees" aria-label="Who came in">
            <thead>
              <tr>
                <th>Employee<span class="ar">الموظف</span></th>
                <th>In<span class="ar">حضور</span></th>
                <th>Out<span class="ar">انصراف</span></th>
                <th class="num">Worked<span class="ar">ساعات العمل</span></th>
                <th class="num">Day's rate<span class="ar">أجر اليوم</span></th>
                <th class="num">Overtime<span class="ar">الإضافي</span></th>
              </tr>
            </thead>
            <tbody>
              @for (e of c.employees; track e.employeeId) {
                <tr [class.forgot]="e.forgotToCheckOut">
                  <td>
                    <strong>{{ e.name }}</strong>
                    <div class="muted small">{{ e.jobTitle }} · {{ departmentName(e.departmentId) }}</div>
                  </td>
                  <td>{{ e.firstIn | date: 'HH:mm' }}</td>
                  <td>
                    @if (e.lastOut) {
                      {{ e.lastOut | date: 'HH:mm' }}
                    } @else if (e.forgotToCheckOut) {
                      <span class="flag">Forgot to check out<span class="ar">نسي تسجيل الانصراف</span></span>
                    } @else {
                      <span class="muted">Still in</span>
                    }
                  </td>
                  <td class="num">{{ duration(e.workedMinutes) }}</td>
                  <td class="num">
                    {{ e.dayRate | money }}
                    <div class="muted small">{{ e.employmentType === 'Daily' ? 'daily wage' : 'salary ÷ 30' }}</div>
                  </td>
                  <td class="num">
                    {{ e.overtimePay | money }}
                    @if (e.overtimeMinutes) {
                      <div class="muted small">{{ duration(e.overtimeMinutes) }}</div>
                    }
                  </td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <td colspan="4">Total · الإجمالي</td>
                <td class="num">{{ c.cost | money }}</td>
                <td class="num">{{ c.overtimePay | money }}</td>
              </tr>
            </tfoot>
          </table>
        </div>
      } @else {
        <p class="muted">No one came in on this day.</p>
      }
      <p class="note">
        A quick reference, not the payroll: each employee who came in, at their day's rate, a monthly salary over 30 or their daily wage.
        Overtime is paid on top; lateness, absence and penalties are deducted, and allowances added, on the month's payslips.
      </p>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .day {
      width: 170px;
    }
    .report-head h2 {
      margin: 0 0 12px;
      font: var(--mat-sys-title-large);
    }
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
      gap: 8px;
      max-width: 900px;
      margin-bottom: 16px;
    }
    .tile {
      display: grid;
      gap: 2px;
      padding: 12px 14px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      .label {
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      strong {
        font: var(--mat-sys-headline-small);
      }
      .of {
        font: var(--mat-sys-title-small);
        color: var(--mat-sys-on-surface-variant);
      }
      &.main {
        background: color-mix(in srgb, var(--mat-sys-primary) 8%, var(--mat-sys-surface));
      }
    }
    h3 {
      margin: 16px 0 8px;
      font: var(--mat-sys-title-small);
    }
    .narrow {
      max-width: 900px;
    }
    tr.forgot td {
      background: color-mix(in srgb, var(--till-warn) 12%, transparent);
    }
    .flag {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: color-mix(in srgb, var(--till-warn) 30%, transparent);
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class DailyCostPage {
  private readonly api = inject(HrApi);
  private readonly businessDay = inject(BusinessDay);

  protected readonly duration = duration;
  protected readonly today = signal('');
  protected readonly date = signal('');
  protected readonly cost = signal<DailyLabourCost | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly names = computed(() => new Map((this.cost()?.departments ?? []).map((d) => [d.departmentId, d.name])));

  // Each department's share of the day's cost: "62%".
  protected readonly share = (amount: number): string => {
    const total = this.cost()?.cost ?? 0;
    return total ? `${Math.round((amount / total) * 100)}%` : '—';
  };

  protected readonly departmentName = (id: string): string => this.names().get(id) ?? '—';

  constructor() {
    void (async () => {
      const today = await this.businessDay.today();
      this.today.set(today);
      this.date.set(today);
      await this.load();
    })();
  }

  protected choose(day: string): void {
    if (/^\d{4}-\d{2}-\d{2}$/.test(day ?? '') && day <= this.today()) {
      this.date.set(day);
      void this.load();
    }
  }

  protected move(by: number): void {
    const day = new Date(`${this.date()}T12:00:00`);
    day.setDate(day.getDate() + by);
    this.choose(isoDay(day));
  }

  private async load(): Promise<void> {
    const date = this.date();
    this.loading.set(true);
    this.error.set(null);
    try {
      const cost = await firstValueFrom(this.api.dailyCost(date));
      // A day chosen while it was read is read in its turn.
      if (date === this.date()) {
        this.cost.set(cost);
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The day’s cost could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
