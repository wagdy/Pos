import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CustodyItem } from '../../core/custody/custody.api';
import { PayrollSlip, weekDays } from '../../core/hr/hr.api';
import { errorMessage } from '../../core/http/error-message';
import { PortalAuth } from '../../core/portal/portal-auth';
import { PortalApi, PortalProfile } from '../../core/portal/portal.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { PaperPreview } from '../costing/invoice-paper';
import { PayslipSheet } from '../hr/payslip-sheet';

// An employee's own page in the portal (ملفي), read-only, and nothing of the till around it: who
// they are (their details and ID card), their payslip (this month's as it stands, or one before
// from the list), and what is in their custody.
@Component({
  selector: 'app-my-profile-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, MoneyPipe, PaperPreview, PayslipSheet],
  template: `
    <header class="bar">
      <mat-icon>badge</mat-icon>
      <span class="title">My profile <span class="ar">ملفي</span></span>
      <span class="spacer"></span>
      <span class="who">{{ auth.session()?.name }}</span>
      <button mat-button (click)="auth.signOut()"><mat-icon>logout</mat-icon> Sign out</button>
    </header>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    <main class="page">
      @if (error(); as e) {
        <p class="error">{{ e }}</p>
      } @else if (profile(); as p) {
        <section class="card details" aria-label="My details">
          <h2>My details <span class="ar">بياناتي</span></h2>
          <dl>
            <div><dt>Full name<span class="ar">الاسم بالكامل</span></dt><dd>{{ p.name }}</dd></div>
            <div><dt>National ID<span class="ar">رقم البطاقة</span></dt><dd class="digits" [class.missing]="!p.nationalId">{{ p.nationalId ?? 'Not entered' }}</dd></div>
            <div><dt>Job<span class="ar">الوظيفة</span></dt><dd>{{ p.jobTitle }} · {{ p.department }}</dd></div>
            <div><dt>Mobile<span class="ar">الموبايل</span></dt><dd class="digits">{{ p.mobileNumber ?? '—' }}</dd></div>
            <div><dt>Started on<span class="ar">تاريخ التعيين</span></dt><dd>{{ p.hiredOn | date: 'd MMM y' }}</dd></div>
            <div>
              <dt>Shift<span class="ar">الوردية</span></dt>
              <dd>
                {{ p.shiftStartsAt && p.shiftEndsAt ? p.shiftStartsAt.slice(0, 5) + '–' + p.shiftEndsAt.slice(0, 5) : 'Flexible hours' }}
                <span class="muted"> · {{ daysOff() }}</span>
              </dd>
            </div>
          </dl>
          @if (p.idPhoto; as photo) {
            <h3>ID card <span class="ar">صورة البطاقة</span></h3>
            <div class="photo">
              <app-paper-preview [fetch]="photo$" [fileName]="photo.fileName" />
            </div>
          }
        </section>

        <section class="card payslip" aria-label="My payslip">
          <div class="head">
            <h2>My payslip <span class="ar">قسيمة راتبي</span></h2>
            <span class="spacer"></span>
            @if (months().length) {
              <mat-form-field subscriptSizing="dynamic" class="month">
                <mat-label>Month · الشهر</mat-label>
                <mat-select [ngModel]="month()" (ngModelChange)="pick($event)" aria-label="Month">
                  @for (m of months(); track m) {
                    <mat-option [value]="m">{{ m | date: 'MMMM y' }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            }
          </div>
          @if (slipLoading()) {
            <mat-progress-bar mode="indeterminate" />
          }
          @if (slip(); as s) {
            @if (s.status !== 'Finalized') {
              <p class="muted small draft-note">A draft, worked out from your attendance as it stands now: it changes until your manager finalizes it.</p>
            }
            <app-payslip-sheet [slip]="s" [month]="month()" />
          } @else if (!slipLoading()) {
            <p class="muted no-slip">No payslip for this month.</p>
          }
        </section>

        <section class="card custody" aria-label="My custody">
          <h2>My custody <span class="ar">عهدتي</span></h2>
          @for (c of custody(); track c.asset.id) {
            <div class="held">
              <span>
                <strong>{{ c.asset.name }}</strong> × {{ c.asset.quantity }}
                <div class="muted small">{{ c.asset.categoryName ?? '' }}@if (c.since) { · since {{ c.since | date: 'd MMM y' }} }</div>
              </span>
              @if (c.asset.totalCost !== null) {
                <span class="amount">{{ c.asset.totalCost | money }}</span>
              }
            </div>
          } @empty {
            <p class="muted">Nothing is in your custody.</p>
          }
          @if (custody().length) {
            <p class="muted small">Keep them safe: what breaks or is lost is checked out by your manager.</p>
          }
        </section>
      }
    </main>
  `,
  styles: `
    :host {
      display: block;
      min-height: 100dvh;
      background: var(--mat-sys-surface-container);
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 16px;
      background: var(--mat-sys-primary);
      color: var(--mat-sys-on-primary);

      button {
        color: inherit;
      }
      .title {
        font: var(--mat-sys-title-medium);
      }
      .who {
        font: var(--mat-sys-label-large);
      }
    }
    .spacer {
      flex: 1;
    }
    .page {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr);
      /* The payslip spans both rows; what is left over goes to the second, so custody sits right under the details. */
      grid-template-rows: auto 1fr;
      gap: 16px;
      max-width: 1200px;
      margin: 0 auto;
      padding: 16px;
      box-sizing: border-box;
      align-items: start;
    }
    .card {
      padding: 14px 16px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      h2 {
        margin: 0 0 10px;
        font: var(--mat-sys-title-medium);
      }
      h3 {
        margin: 12px 0 8px;
        font: var(--mat-sys-title-small);
        color: var(--mat-sys-on-surface-variant);
      }
    }
    .payslip {
      grid-row: span 2;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;

      h2 {
        margin: 0;
      }
    }
    .month {
      min-width: 170px;
    }
    dl {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
      gap: 10px 16px;
      margin: 0;

      dt {
        font: var(--mat-sys-label-small);
        color: var(--mat-sys-on-surface-variant);
      }
      dd {
        margin: 2px 0 0;
        font: var(--mat-sys-body-large);
        overflow-wrap: anywhere;
      }
      dd.missing {
        color: var(--mat-sys-on-surface-variant);
        font-style: italic;
      }
    }
    .digits {
      font-variant-numeric: tabular-nums;
    }
    .photo {
      --paper-height: 220px;
      --paper-min-width: 0;
    }
    .held {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      padding: 6px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);

      .amount {
        margin-left: auto;
        white-space: nowrap;
        font-variant-numeric: tabular-nums;
      }
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .error {
      color: var(--mat-sys-error);
    }
    /* A phone: one under the other, the long payslip last. */
    @media (max-width: 800px) {
      .page {
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: none;
      }
      .payslip {
        grid-row: auto;
        order: 1;
      }
    }
  `,
})
export class MyProfilePage {
  private readonly api = inject(PortalApi);
  protected readonly auth = inject(PortalAuth);

  protected readonly profile = signal<PortalProfile | null>(null);
  protected readonly custody = signal<CustodyItem[]>([]);
  protected readonly months = signal<string[]>([]);
  protected readonly month = signal('');
  protected readonly slip = signal<PayrollSlip | null>(null);
  protected readonly loading = signal(true);
  protected readonly slipLoading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly photo$ = () => this.api.idPhoto();

  protected readonly daysOff = computed(() => {
    const off = weekDays.filter((d) => this.profile()?.daysOff.includes(d.day)).map((d) => d.en);
    return off.length ? 'off ' + off.join(', ') : 'no day off';
  });

  constructor() {
    void this.load();
  }

  protected pick(month: string): void {
    this.month.set(month);
    void this.loadSlip();
  }

  private async load(): Promise<void> {
    try {
      const [profile, custody, months] = await Promise.all([
        firstValueFrom(this.api.me()),
        firstValueFrom(this.api.custody()),
        firstValueFrom(this.api.months()),
      ]);
      this.profile.set(profile);
      this.custody.set(custody);
      this.months.set(months);
      if (months.length) {
        this.month.set(months[0]);
        await this.loadSlip();
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'Your profile could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private async loadSlip(): Promise<void> {
    const month = this.month();
    this.slipLoading.set(true);
    try {
      const slip = await firstValueFrom(this.api.payslip(month));
      if (month === this.month()) {
        this.slip.set(slip);
      }
    } catch {
      this.slip.set(null);
    } finally {
      this.slipLoading.set(false);
    }
  }
}
