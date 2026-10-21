import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { AttendanceSignal } from '../../core/hr/attendance-signal';
import { Department, HrApi, Presence, PresenceState, clock } from '../../core/hr/hr.api';
import { CorrectionsCount } from './corrections-count';

// What a state is called, and its colour.
const states: Record<PresenceState, { en: string; ar: string; tone: string }> = {
  In: { en: 'In', ar: 'موجود', tone: 'in' },
  Left: { en: 'Left', ar: 'غادر', tone: 'left' },
  NotArrived: { en: 'Not arrived yet', ar: 'لم يحضر بعد', tone: 'waiting' },
  Late: { en: 'Late', ar: 'متأخر', tone: 'late' },
  Off: { en: 'Off today', ar: 'إجازة اليوم', tone: 'off' },
};

// Now (الآن): who is in the restaurant right now, from the attendance device's punches as they
// come in. Each employee on their own day, so a shift past midnight is still on: in (green) since
// their last scan, left (grey), not arrived yet (yellow) or late (red) once their shift and its
// grace have started, or off today. Grouped by department, read again the moment the till says
// punches came, and every minute for the clock.
@Component({
  selector: 'app-now-page',
  imports: [DatePipe, MatIconModule, MatProgressBarModule, RouterLink],
  template: `
    <div class="toolbar">
      <div class="counts" aria-label="Now">
        @for (c of counts(); track c.state) {
          <span class="count" [class]="c.tone"><span class="dot"></span><strong>{{ c.value }}</strong> {{ c.en }}<span class="inline-ar">{{ c.ar }}</span></span>
        }
      </div>
      <span class="spacer"></span>
      @if (waiting.count()) {
        <a class="corrections" routerLink="/hr/corrections"><mat-icon>rule</mat-icon> {{ waiting.count() }} waiting on Corrections</a>
      }
      @if (report(); as r) {
        <span class="muted small">As of {{ r.now | date: 'HH:mm' }} · live</span>
      }
    </div>
    @if (loading() && !report()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    }

    @for (g of groups(); track g.name) {
      <section class="department" [attr.aria-label]="g.name">
        <h3>{{ g.name }} <span class="muted small">· {{ g.in }} in of {{ g.people.length }}</span></h3>
        <div class="cards">
          @for (p of g.people; track p.employee.id) {
            <a class="card" [class]="tone(p)" routerLink="/hr/attendance" [queryParams]="{ employee: p.employee.id, month: p.day }">
              <span class="dot" aria-hidden="true"></span>
              <span class="who">
                <strong>{{ p.employee.name }}</strong>
                <span class="muted small">{{ p.employee.jobTitle }}</span>
              </span>
              <span class="state">
                <span class="label">{{ label(p).en }}<span class="ar">{{ label(p).ar }}</span></span>
                <span class="small">{{ detail(p) }}</span>
              </span>
            </a>
          }
        </div>
      </section>
    } @empty {
      @if (report()) {
        <p class="muted">No one is employed today. Add employees on the Employees tab.</p>
      }
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    :host {
      --in: #2e7d32;
      --left: #9e9e9e;
      --waiting: #f9a825;
      --late: var(--mat-sys-error);
      --off: var(--mat-sys-outline);
    }
    .counts {
      display: flex;
      flex-wrap: wrap;
      gap: 6px 10px;
    }
    .count {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 12px;
      border-radius: 999px;
      background: var(--mat-sys-surface-container);
    }
    .dot {
      flex: none;
      width: 10px;
      height: 10px;
      border-radius: 50%;
      background: var(--off);
    }
    .in .dot {
      background: var(--in);
      box-shadow: 0 0 0 3px color-mix(in srgb, var(--in) 25%, transparent);
    }
    .left .dot {
      background: var(--left);
    }
    .waiting .dot {
      background: var(--waiting);
    }
    .late .dot {
      background: var(--late);
    }
    .corrections {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--mat-sys-primary);

      mat-icon {
        font-size: 18px;
        width: 18px;
        height: 18px;
      }
    }
    .department {
      margin-top: 16px;

      h3 {
        margin: 0 0 8px;
        font: var(--mat-sys-title-small);
      }
    }
    .cards {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
      gap: 8px;
    }
    .card {
      display: grid;
      grid-template-columns: auto 1fr;
      grid-template-areas: 'dot who' 'dot state';
      align-items: center;
      gap: 2px 10px;
      padding: 10px 12px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-left: 4px solid var(--off);
      background: var(--mat-sys-surface);
      color: inherit;
      text-decoration: none;

      &:hover {
        background: var(--mat-sys-surface-container-low);
      }
      &.in {
        border-left-color: var(--in);
      }
      &.left {
        border-left-color: var(--left);
      }
      &.waiting {
        border-left-color: var(--waiting);
      }
      &.late {
        border-left-color: var(--late);
      }
      &.off {
        opacity: 0.75;
      }
      .dot {
        grid-area: dot;
      }
      .who {
        grid-area: who;
        display: flex;
        flex-wrap: wrap;
        gap: 0 8px;
        align-items: baseline;
      }
      .state {
        grid-area: state;
        display: flex;
        flex-wrap: wrap;
        gap: 0 8px;
        align-items: baseline;
      }
      .label {
        font-weight: 600;
      }
      &.late .label {
        color: var(--late);
      }
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.85em;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class NowPage {
  private readonly api = inject(HrApi);
  private readonly punches = inject(AttendanceSignal);
  protected readonly waiting = inject(CorrectionsCount);

  protected readonly report = signal<{ now: string; employees: Presence[] } | null>(null);
  private readonly departments = signal<Department[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly counts = computed(() => {
    const people = this.report()?.employees ?? [];
    const count = (...of: PresenceState[]) => people.filter((p) => of.includes(p.state)).length;
    const late = count('Late');
    return [
      { state: 'In', value: count('In'), en: 'in', ar: 'موجود', tone: 'in' },
      { state: 'Left', value: count('Left'), en: 'left', ar: 'غادر', tone: 'left' },
      {
        state: 'NotArrived', value: count('NotArrived', 'Late'), en: late ? `not arrived, ${late} late` : 'not arrived', ar: 'لم يحضر بعد',
        tone: late ? 'late' : 'waiting',
      },
      { state: 'Off', value: count('Off'), en: 'off today', ar: 'إجازة', tone: 'off' },
    ];
  });

  // By department, in the departments' order, and within each: in first, then late, not
  // arrived, left and off, each by name.
  protected readonly groups = computed(() => {
    const order: PresenceState[] = ['In', 'Late', 'NotArrived', 'Left', 'Off'];
    const names = new Map(this.departments().map((d) => [d.id, d.name]));
    const groups = new Map<string, Presence[]>();
    for (const p of this.report()?.employees ?? []) {
      const name = names.get(p.employee.departmentId) ?? 'Other';
      groups.set(name, [...(groups.get(name) ?? []), p]);
    }
    const rank = (name: string) => {
      const i = this.departments().findIndex((d) => d.name === name);
      return i < 0 ? Number.MAX_SAFE_INTEGER : i;
    };
    return [...groups.entries()]
      .sort((a, b) => rank(a[0]) - rank(b[0]))
      .map(([name, people]) => ({
        name,
        in: people.filter((p) => p.state === 'In').length,
        people: [...people].sort((a, b) => order.indexOf(a.state) - order.indexOf(b.state) || a.employee.name.localeCompare(b.employee.name)),
      }));
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    // The moment the till says punches came; and every minute, for whoever has become late.
    effect(() => {
      this.punches.changes();
      untracked(() => void this.load());
    });
    const minute = setInterval(() => void this.load(), 60_000);
    destroyRef.onDestroy(() => clearInterval(minute));
    void firstValueFrom(this.api.departments()).then(
      (d) => this.departments.set(d),
      () => undefined,
    );
    void this.waiting.refresh();
  }

  protected tone(p: Presence): string {
    return states[p.state].tone;
  }

  protected label(p: Presence): { en: string; ar: string } {
    if (p.state === 'Off') {
      return p.dayStatus === 'Holiday'
        ? { en: 'Public holiday', ar: 'عطلة رسمية' }
        : p.dayStatus === 'Leave'
          ? { en: 'On leave', ar: 'إجازة' }
          : { en: 'Day off', ar: 'راحة' };
    }
    return states[p.state];
  }

  // "since 16:03", "at 01:10 +1", "due 16:00", the holiday's name.
  protected detail(p: Presence): string {
    switch (p.state) {
      case 'In':
        return `since ${clock(p.since, p.day)}`;
      case 'Left':
        return `at ${clock(p.since, p.day)}`;
      case 'Late':
      case 'NotArrived':
        return p.dueAt ? `due ${clock(p.dueAt)}` : 'flexible hours';
      default:
        return p.holidayName ?? '';
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.report.set(await firstValueFrom(this.api.presence()));
      this.error.set(null);
    } catch (error) {
      this.error.set(errorMessage(error, 'Who is in could not be read.'));
    } finally {
      this.loading.set(false);
    }
  }
}
