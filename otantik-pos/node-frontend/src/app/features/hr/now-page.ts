import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { aheadBy } from '../../core/hr/device-link';
import { errorMessage } from '../../core/http/error-message';
import { AttendanceSignal } from '../../core/hr/attendance-signal';
import { AttendanceDevice, Department, EmployeeStatus, HrApi, Presence, PresenceState, clock } from '../../core/hr/hr.api';
import { HrAccess } from '../../core/hr/hr-access';
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
// grace have started, or off today. Grouped by department. Each card changes in place the moment
// the till pushes it (EmployeeStatusChanged), lit up for a moment; the whole page is read again
// every minute for the clock, and after the connection was lost.
//
// Punches come as they are made only while the device calls the till over ADMS: when it has not
// for a few minutes, the page says so, and where to point it. A device whose punches come stamped
// ahead of the till's clock is said too: they count at the wrong times.
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
    @if (quiet().length) {
      <div class="quiet" role="status">
        <mat-icon>wifi_off</mat-icon>
        <span>
          <strong>{{ quietNames() }} {{ quiet().length === 1 ? 'is' : 'are' }} not calling in over ADMS</strong>@if (quiet()[0].lastCalledInAtUtc; as last) {
            since {{ last | date: 'd MMM, HH:mm' }}}: punches reach the till only when someone presses Sync.
          @if (pointAt(); as at) {
            On the device, set Cloud Server Setting to <strong>{{ at }}</strong>.
          }
          @if (access.has('Devices')) {
            <a routerLink="/hr/devices">Attendance devices</a>
          }
        </span>
      </div>
    }
    @for (d of fast(); track d.id) {
      <div class="quiet" role="status">
        <mat-icon>schedule</mat-icon>
        <span>
          <strong>{{ d.name ?? d.serialNumber }}'s clock is {{ ahead(d.clockAheadMinutes!) }} ahead</strong>: its punches are stamped later than
          they happen, and count at the wrong times. The till sends it the restaurant's time zone each time it starts: restart the device to set
          its clock, and ignore those punches on the Attendance tab.
          @if (access.has('Devices')) {
            <a routerLink="/hr/devices">Attendance devices</a>
          }
        </span>
      </div>
    }
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
            <a class="card" [class]="tone(p)" [class.fresh]="fresh().has(p.employee.id)" [routerLink]="access.has('Attendance') ? '/hr/attendance' : null"
              [queryParams]="{ employee: p.employee.id, month: p.day }">
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
      &.fresh {
        animation: fresh 4s ease-out;
      }
    }
    @keyframes fresh {
      from {
        box-shadow: 0 0 0 3px var(--mat-sys-primary);
        background: color-mix(in srgb, var(--mat-sys-primary) 12%, var(--mat-sys-surface));
      }
      to {
        box-shadow: 0 0 0 0 transparent;
      }
    }
    .quiet {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      margin: 8px 0;
      padding: 10px 12px;
      border-radius: 12px;
      background: color-mix(in srgb, var(--mat-sys-error) 10%, var(--mat-sys-surface));
      color: var(--mat-sys-on-surface);

      mat-icon {
        flex: none;
        color: var(--mat-sys-error);
      }
      a {
        color: var(--mat-sys-primary);
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
  protected readonly access = inject(HrAccess);
  private readonly punches = inject(AttendanceSignal);
  protected readonly waiting = inject(CorrectionsCount);

  protected readonly report = signal<{ now: string; employees: Presence[] } | null>(null);
  private readonly departments = signal<Department[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  // The cards just changed by a push, lit up for a moment.
  protected readonly fresh = signal<ReadonlySet<string>>(new Set());

  // Approved devices not calling the till over ADMS, and where they should call.
  protected readonly quiet = signal<AttendanceDevice[]>([]);
  protected readonly fast = signal<AttendanceDevice[]>([]);
  protected readonly ahead = aheadBy;
  protected readonly quietNames = computed(() => this.quiet().map((d) => d.name ?? d.serialNumber).join(', '));
  private readonly connection = signal<{ devicePort: number | null; addresses: string[] } | null>(null);
  protected readonly pointAt = computed(() => {
    const c = this.connection();
    const port = c?.devicePort ?? (location.port || null);
    return c?.addresses.length && port ? `${c.addresses.join(' or ')}, port ${port}` : null;
  });

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
    // Read whole at first, after the connection was lost, and every minute, for whoever has
    // become late; each card as the till pushes it.
    effect(() => {
      this.punches.changes();
      untracked(() => void this.load());
    });
    this.punches.statuses.pipe(takeUntilDestroyed()).subscribe((statuses) => this.apply(statuses));
    const minute = setInterval(() => void this.load(), 60_000);
    const timers = new Set<ReturnType<typeof setTimeout>>();
    this.dim = (ids) => {
      const timer = setTimeout(() => {
        timers.delete(timer);
        this.fresh.update((lit) => new Set([...lit].filter((id) => !ids.includes(id))));
      }, 4_000);
      timers.add(timer);
    };
    destroyRef.onDestroy(() => {
      clearInterval(minute);
      timers.forEach(clearTimeout);
    });
    void firstValueFrom(this.api.deviceConnection()).then(
      (c) => this.connection.set(c),
      () => undefined,
    );
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
        : p.dayStatus === 'Leave' || p.dayStatus === 'UnpaidLeave'
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

  private dim: (ids: string[]) => void = () => undefined;

  // Each pushed card changed in place and lit up. One not on the page (just employed) has the
  // whole page read again.
  private apply(statuses: EmployeeStatus[]): void {
    const report = this.report();
    if (!report || !statuses.length) {
      return;
    }
    const pushed = new Map(statuses.map((s) => [s.employeeId, s]));
    if ([...pushed.keys()].some((id) => !report.employees.some((p) => p.employee.id === id))) {
      void this.load();
      return;
    }
    this.report.set({
      now: statuses.reduce((latest, s) => (s.at > latest ? s.at : latest), report.now),
      employees: report.employees.map((p) => {
        const s = pushed.get(p.employee.id);
        return s ? { ...p, day: s.day, state: s.state, since: s.since, dueAt: s.dueAt, dayStatus: s.dayStatus, holidayName: s.holidayName } : p;
      }),
    });
    const ids = [...pushed.keys()];
    this.fresh.update((lit) => new Set([...lit, ...ids]));
    this.dim(ids);
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    void firstValueFrom(this.api.devices()).then(
      (devices) => {
        this.quiet.set(devices.filter((d) => d.isApproved && !d.callingIn));
        this.fast.set(devices.filter((d) => d.isApproved && d.clockAheadMinutes !== null));
      },
      () => undefined,
    );
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
