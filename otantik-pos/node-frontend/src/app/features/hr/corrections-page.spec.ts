import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AdministrativePenalty, AttendanceDay, Employee, EmployeeDay, HrApi, LatePunch, Punch } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { IgnorePunchDialog, PenaltyDialog, ResolveDayDialog } from './attendance-dialogs';
import { CorrectionsPage } from './corrections-page';
import { CorrectionsCount } from './corrections-count';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Each element's text inside, as Angular drops the spaces between elements.
const parts = (el: Element) => [...el.children].map(text);

function employee(id: string, name: string, extra: Partial<Employee> = {}): Employee {
  return {
    id, name, jobTitle: 'Chef', monthlySalary: 5_400, shiftStartsAt: '16:00:00', shiftEndsAt: '01:00:00', daysOff: ['Friday'], deviceUserId: '7',
    hiredOn: '2026-09-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20, employmentType: 'Monthly', dailyWage: 0, departmentId: 'general', jobRoleId: null, ...extra,
  };
}

function punch(id: string, at: string, extra: Partial<Punch> = {}): Punch {
  return {
    id, employeeId: 'hassan', source: 'Device', deviceSerialNumber: 'CQZ', deviceUserId: '7', punchedAt: at, deviceStatus: 0, verifyMode: 1, reason: null,
    recordedBy: null, isIgnored: false, ignoreReason: null, ignoredBy: null, ...extra,
  };
}

function suspendedDay(date: string, next: string, punches: Punch[]): AttendanceDay {
  return {
    date, status: 'Suspended', shiftStartsAt: `${date}T16:00:00`, shiftEndsAt: `${next}T01:00:00`, firstIn: punches[0].punchedAt, lastOut: null,
    workedMinutes: 0, lateMinutes: 0, earlyLeaveMinutes: 0, awayMinutes: 0, overtimeMinutes: 0, shortMinutes: 0, isWeeklyDayOff: false, mark: null,
    markNotes: null, punches, penaltyHours: 0, penaltyReason: null, penaltySetBy: null, holidayName: null, dayEndsAt: `${next}T10:00:00`,
  };
}

const hassan = employee('hassan', 'Hassan');
const mona = employee('mona', 'Mona', { hourlyRate: 25 });
const gone = employee('gone', 'Gone', { hiredOn: '2026-01-01', leftOn: '2026-08-31', isActive: false });

const waiting: EmployeeDay[] = [
  {
    employee: hassan,
    day: suspendedDay('2026-10-12', '2026-10-13', [
      punch('p1', '2026-10-12T10:00:00'), punch('p2', '2026-10-12T14:00:00'), punch('p3', '2026-10-12T16:05:00'),
    ]),
  },
  { employee: mona, day: suspendedDay('2026-10-19', '2026-10-20', [punch('m1', '2026-10-19T16:02:00'), punch('m2', '2026-10-19T16:03:00', { isIgnored: true })]) },
];

const penalties: AdministrativePenalty[] = [
  {
    employeeId: 'hassan', employeeName: 'Hassan', date: '2026-10-05', hours: 2, hourlyRate: 20, amount: 40, reason: 'Left the walk-in open', setBy: 'Omar',
    setAtUtc: '2026-10-05T20:00:00Z',
  },
  {
    employeeId: 'mona', employeeName: 'Mona', date: '2026-10-09', hours: 1.5, hourlyRate: 25, amount: 37.5, reason: 'Late twice this week', setBy: 'Omar',
    setAtUtc: '2026-10-09T20:00:00Z',
  },
];

describe('the corrections', () => {
  const suspendedDays = vi.fn();
  const penaltiesOf = vi.fn();
  const resolveDay = vi.fn();
  const ignorePunch = vi.fn();
  const setPenalty = vi.fn();
  const latePunches = vi.fn();
  const settleLatePunches = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const count = { refresh: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HrApi,
          useValue: {
            suspendedDays, penalties: penaltiesOf, resolveDay, ignorePunch, setPenalty, latePunches, settleLatePunches, employees: () => of([hassan, mona, gone]),
          },
        },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-21') } },
        { provide: CorrectionsCount, useValue: count },
      ],
    });
    const fixture = TestBed.createComponent(CorrectionsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = (section: string) => [...el.querySelectorAll(`.${section} tbody tr`)];
    return { el, rows, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    suspendedDays.mockReset().mockReturnValue(of(waiting));
    penaltiesOf.mockReset().mockReturnValue(of(penalties));
    resolveDay.mockReset().mockReturnValue(of({ ...waiting[0].day, status: 'Worked', workedMinutes: 780 }));
    ignorePunch.mockReset().mockReturnValue(of({}));
    setPenalty.mockReset().mockReturnValue(of(undefined));
    latePunches.mockReset().mockReturnValue(of([]));
    settleLatePunches.mockReset().mockReturnValue(of({ settled: 2 }));
    open.mockReset();
    notify.info.mockReset();
    count.refresh.mockReset();
    canApprove.set(true);
  });

  it('lists the suspended days since the first of last month, each one’s stray punch-in marked', async () => {
    const { el, rows } = await render();

    expect(suspendedDays).toHaveBeenCalledWith('2026-09-01');
    expect(count.refresh).toHaveBeenCalled();
    const days = rows('suspended');
    expect(days.map((r) => text(r.querySelector('td')!))).toEqual(['Mon 12 Oct', 'Mon 19 Oct']);
    expect([text(days[0].querySelector('td:nth-child(2) strong')!), text(days[0].querySelector('td:nth-child(2) .muted')!)]).toEqual(['Hassan', 'Chef']);
    expect(text(days[0].querySelectorAll('td')[2])).toBe('16:00–01:00 +1');
    expect(parts(days[0].querySelector('.punch.stray')!)).toEqual(['fingerprint', '16:05', 'in, never out']);
    // The stray punch is the last that counts, not one ignored after it.
    expect(parts(days[1].querySelector('.punch.stray')!)).toEqual(['fingerprint', '16:02', 'in, never out']);
    expect(parts(days[1].querySelector('.punch.ignored')!)).toEqual(['fingerprint', '16:03', 'ignored']);
    expect([...days[0].querySelectorAll('td.actions button')].map(text)).toEqual(['logout Punch-out الانصراف', 'Ignore the punch']);
    expect(days[0].querySelector('a')!.getAttribute('href')).toBe('/hr/attendance?employee=hassan&month=2026-10-12');
    expect(el.querySelector('.suspended .table-wrap + .note')).toBeNull();
  });

  it('enters the punch-out a manager gives, and says what the day came to', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of({ punchedOutAt: '2026-10-13T01:00:00', reason: 'Forgot to scan out' }) });
    await page['resolve'](waiting[0], waiting[0].day.punches[2]);
    expect(open.mock.lastCall![0]).toBe(ResolveDayDialog);
    expect(open.mock.lastCall![1].data).toEqual({
      employee: 'Hassan', date: '2026-10-12', shift: '16:00–01:00 +1', shiftEndsAt: '2026-10-13T01:00:00', dayEndsAt: '2026-10-13T10:00:00',
      punchedIn: '2026-10-12T16:05:00',
    });
    expect(resolveDay).toHaveBeenCalledWith('hassan', '2026-10-12', '2026-10-13T01:00:00', 'Forgot to scan out');
    expect(notify.info).toHaveBeenCalledWith('Punch-out entered: Hassan worked 13h 00m on Mon 12 Oct.');
    expect(suspendedDays).toHaveBeenCalledTimes(2);
  });

  it('ignores a stray punch made by mistake', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of('Scanned by mistake') });
    await page['ignore'](waiting[1], waiting[1].day.punches[0]);
    expect(open.mock.lastCall![0]).toBe(IgnorePunchDialog);
    expect(open.mock.lastCall![1].data).toEqual({ time: '16:02' });
    expect(ignorePunch).toHaveBeenCalledWith('m1', 'Scanned by mistake');
    expect(suspendedDays).toHaveBeenCalledTimes(2);
  });

  it('lists the punches that came after the pay was finalized, a day at a time, until seen to', async () => {
    const late = (id: string, at: string): LatePunch => ({
      employee: hassan, day: '2026-09-28', month: '2026-09-01', finalizedAtUtc: '2026-10-02T09:00:00Z', punch: punch(id, at),
    });
    latePunches.mockReturnValue(of([late('l1', '2026-09-28T16:01:00'), late('l2', '2026-09-29T01:10:00')]));
    const { el, page } = await render();

    const rows = [...el.querySelectorAll('.late tbody tr')];
    expect(rows.length).toBe(1);
    expect(text(rows[0].querySelector('td')!)).toBe('Mon 28 Sept');
    expect([...rows[0].querySelectorAll('.punch strong')].map(text)).toEqual(['16:01', '01:10 +1']);

    await page['settle'](page['lateDays']()[0]);
    expect(settleLatePunches).toHaveBeenCalledWith(['l1', 'l2']);
    expect(notify.info).toHaveBeenCalledWith('Hassan\'s Mon 28 Sept: seen to.');
    expect(latePunches).toHaveBeenCalledTimes(2);
  });

  it('lists the month’s penalties with what they take off, in all', async () => {
    const { el, rows } = await render();

    expect(penaltiesOf).toHaveBeenCalledWith('2026-10-01');
    expect(rows('penalties').map((r) => [...r.querySelectorAll('td')].slice(0, 7).map(text))).toEqual([
      ['Mon 5 Oct', 'Hassan', '2h', 'L.E 20.00', 'L.E 40.00', 'Left the walk-in open', 'Omar'],
      ['Fri 9 Oct', 'Mona', '1.5h', 'L.E 25.00', 'L.E 37.50', 'Late twice this week', 'Omar'],
    ]);
    expect([...el.querySelectorAll('.penalties tfoot td')].map(text).slice(0, 4)).toEqual(['Total · الإجمالي', '3.5h', '', 'L.E 77.50']);
  });

  it('adds a penalty for whoever is employed in the month, and changes or takes one off', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of({ employeeId: 'mona', date: '2026-10-20', hours: 3, reason: 'Rude to a guest' }) });
    await page['addPenalty']();
    expect(open.mock.lastCall![0]).toBe(PenaltyDialog);
    expect(open.mock.lastCall![1].data).toEqual({
      employees: [hassan, mona],
      month: { first: '2026-10-01', last: '2026-10-31' },
      taken: [{ employeeId: 'hassan', date: '2026-10-05', hours: 2 }, { employeeId: 'mona', date: '2026-10-09', hours: 1.5 }],
    });
    expect(setPenalty).toHaveBeenCalledWith('mona', '2026-10-20', 3, 'Rude to a guest');
    expect(notify.info).toHaveBeenCalledWith('Penalty added.');
    expect(penaltiesOf).toHaveBeenCalledTimes(2);

    open.mockReturnValue({ afterClosed: () => of({ employeeId: 'hassan', date: '2026-10-05', hours: null, reason: null }) });
    await page['changePenalty'](penalties[0]);
    expect(open.mock.lastCall![1].data).toEqual({
      employee: { id: 'hassan', name: 'Hassan', hourlyRate: 20 }, date: '2026-10-05', hours: 2, reason: 'Left the walk-in open',
    });
    expect(setPenalty).toHaveBeenLastCalledWith('hassan', '2026-10-05', null, null);
    expect(notify.info).toHaveBeenLastCalledWith('Penalty taken off.');
  });

  it('shows an accountant what waits, with nothing to change', async () => {
    canApprove.set(false);
    const { el, rows } = await render();

    expect(el.querySelectorAll('button:not([aria-label*="month"])').length).toBe(0);
    expect(text(el.querySelector('.suspended .table-wrap + .note')!)).toBe('A manager enters the punch-out, or ignores the punch.');
    expect(rows('penalties')[0].querySelectorAll('td').length).toBe(7);
  });

  it('says when nothing waits, and when a month has no penalties', async () => {
    suspendedDays.mockReturnValue(of([]));
    penaltiesOf.mockReturnValue(of([]));
    const { rows } = await render();

    expect(text(rows('suspended')[0])).toBe('task_alt Nothing waiting: every punch-in since Tue 1 Sept has its punch-out.');
    expect(text(rows('penalties')[0])).toBe('No penalties in October 2026.');
  });
});
