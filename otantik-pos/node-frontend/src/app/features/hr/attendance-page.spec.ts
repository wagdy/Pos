import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AttendanceDay, AttendanceDayReport, Employee, HrApi, Punch, Timesheet, clock, duration } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { MAT_DIALOG_DATA } from '@angular/material/dialog';
import { AddPunchDialog, IgnorePunchDialog, MarkDayData, MarkDayDialog, PenaltyDialog, ResolveDayDialog } from './attendance-dialogs';
import { AttendancePage } from './attendance-page';
import { CorrectionsCount } from './corrections-count';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function employee(id: string, name: string, shift = '16:00:00'): Employee {
  return {
    id, name, jobTitle: 'Chef', monthlySalary: 5_400, shiftStartsAt: shift, shiftEndsAt: '01:00:00', daysOff: ['Friday'], deviceUserId: '7',
    hiredOn: '2026-09-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20,
  };
}

function punch(id: string, at: string, extra: Partial<Punch> = {}): Punch {
  return {
    id, employeeId: 'hassan', source: 'Device', deviceSerialNumber: 'CQZ', deviceUserId: '7', punchedAt: at, deviceStatus: 0, verifyMode: 1, reason: null,
    recordedBy: null, isIgnored: false, ignoreReason: null, ignoredBy: null, ...extra,
  };
}

function day(date: string, extra: Partial<AttendanceDay> = {}): AttendanceDay {
  const next = new Date(`${date}T12:00:00`);
  next.setDate(next.getDate() + 1);
  const tomorrow = next.toISOString().slice(0, 10);
  return {
    date, status: 'Worked', shiftStartsAt: `${date}T16:00:00`, shiftEndsAt: `${tomorrow}T01:00:00`, firstIn: `${date}T16:12:00`,
    lastOut: `${tomorrow}T01:20:00`, workedMinutes: 548, lateMinutes: 12, earlyLeaveMinutes: 0, awayMinutes: 0, overtimeMinutes: 20, shortMinutes: 12,
    isWeeklyDayOff: false, mark: null, markNotes: null, punches: [punch('p1', `${date}T16:12:00`), punch('p2', `${tomorrow}T01:20:00`)],
    penaltyHours: 0, penaltyReason: null, penaltySetBy: null, holidayName: null, dayEndsAt: `${tomorrow}T10:00:00`, ...extra,
  };
}

const hassan = employee('hassan', 'Hassan');
const mona = employee('mona', 'Mona', '10:00:00');

const report: AttendanceDayReport = {
  date: '2026-09-21',
  policy: { graceMinutes: 0, doubleScanMinutes: 3 },
  totals: { present: 1, late: 1, absent: 1, missingPunch: 0, offOrOnLeave: 0, overtimeMinutes: 20 },
  employees: [
    { employee: hassan, day: day('2026-09-21', { penaltyHours: 2, penaltyReason: 'Left the walk-in open', penaltySetBy: 'Omar' }) },
    { employee: mona, day: day('2026-09-21', { status: 'Absent', firstIn: null, lastOut: null, workedMinutes: 0, lateMinutes: 0, overtimeMinutes: 0, punches: [] }) },
  ],
  unassigned: [punch('u1', '2026-09-21T11:00:00', { employeeId: null, deviceUserId: '99' })],
};

describe('the attendance', () => {
  const attendance = vi.fn();
  const timesheet = vi.fn();
  const addPunch = vi.fn();
  const ignorePunch = vi.fn();
  const markDay = vi.fn();
  const setPenalty = vi.fn();
  const resolveDay = vi.fn();
  const suspended = { refresh: vi.fn() };
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render(inputs: Record<string, string> = {}) {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: HrApi,
          useValue: { attendance, timesheet, addPunch, ignorePunch, restorePunch: vi.fn(), markDay, setPenalty, resolveDay, employees: () => of([hassan, mona]) },
        },
        { provide: CorrectionsCount, useValue: suspended },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-09-21') } },
      ],
    });
    const fixture = TestBed.createComponent(AttendancePage);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const cells = (row: Element) => [...row.querySelectorAll('td')].slice(1, 9).map(text);
    return { el, refresh, cells, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    attendance.mockReset().mockReturnValue(of(report));
    timesheet.mockReset();
    addPunch.mockReset().mockReturnValue(of({}));
    ignorePunch.mockReset().mockReturnValue(of({}));
    markDay.mockReset().mockReturnValue(of(undefined));
    setPenalty.mockReset().mockReturnValue(of(undefined));
    resolveDay.mockReset().mockReturnValue(of({}));
    suspended.refresh.mockReset();
    open.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
  });

  it('reads times off the wall clock and minutes as hours', () => {
    expect(clock('2026-09-22T01:20:44', '2026-09-21')).toBe('01:20 +1');
    expect(clock('2026-09-21T16:12:05', '2026-09-21')).toBe('16:12');
    expect(clock(null)).toBe('—');
    expect([duration(548), duration(12), duration(0)]).toEqual(['9h 08m', '12m', '—']);
  });

  it('shows everyone’s day: its shift past midnight, its figures, the totals, and punches nobody has yet', async () => {
    const { el, cells } = await render();

    expect(attendance).toHaveBeenCalledWith('2026-09-21');
    expect([...el.querySelectorAll('.totals .chip')].map(text)).toEqual([
      '1 came in · حضور', '1 late · تأخير', '1 absent · غياب', '0 suspended · موقوف للمراجعة', '0 off, on leave or a holiday · راحة وإجازات', '20m overtime · إضافي',
    ]);
    const rows = [...el.querySelectorAll('tbody tr')];
    expect(cells(rows[0])).toEqual(['16:00–01:00 +1', '16:12', '01:20 +1', '9h 08m', '12m', '—', '—', '20m']);
    expect(rows[1].classList).toContain('status-Absent');
    expect(text(rows[0].querySelector('.bad-text')!)).toBe('Penalty 2h: Left the walk-in open');
    expect(text(el.querySelector('.unassigned')!)).toContain('no. 99 at 11:00');
    expect(text(el.querySelector('.note')!)).toContain("Late counts from the shift's start: no grace is set");
  });

  it('opens a day to its punches, and lets a manager put it right', async () => {
    const { el, page, refresh } = await render();

    page['toggle']('hassan');
    await refresh();
    expect([...el.querySelectorAll('.punch strong')].map(text).slice(0, 2)).toEqual(['16:12', '01:20 +1']);
    expect([...el.querySelectorAll('.detail .actions button')].map(text)).toEqual([
      'more_time Add a punch إضافة بصمة', 'event_available Leave or day off إجازة أو راحة', 'gavel Change the penalty جزاء إداري',
    ]);

    open.mockReturnValue({ afterClosed: () => of({ punchedAt: '2026-09-22T01:05:00', reason: 'Forgot to scan out' }) });
    await page['addPunch'](page['rows']()[0]);
    expect(open.mock.lastCall![0]).toBe(AddPunchDialog);
    expect(open.mock.lastCall![1].data).toEqual({ employee: 'Hassan', day: '2026-09-21', shift: '16:00–01:00 +1' });
    expect(addPunch).toHaveBeenCalledWith('hassan', '2026-09-22T01:05:00', 'Forgot to scan out');
    expect(attendance).toHaveBeenCalledTimes(2);

    open.mockReturnValue({ afterClosed: () => of('Scanned twice') });
    await page['ignore'](report.employees[0].day.punches[0], report.employees[0].day);
    expect(open.mock.lastCall![0]).toBe(IgnorePunchDialog);
    expect(ignorePunch).toHaveBeenCalledWith('p1', 'Scanned twice');

    open.mockReturnValue({ afterClosed: () => of({ kind: 'None', notes: 'ignored' }) });
    await page['markDay'](page['rows']()[1]);
    expect(open.mock.lastCall![0]).toBe(MarkDayDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ employee: 'Mona', mark: null, weeklyDayOff: false });
    expect(markDay).toHaveBeenCalledWith('mona', '2026-09-21', null, null);
  });

  it('gives a day an administrative penalty, changed or taken off', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of({ employeeId: 'hassan', date: '2026-09-21', hours: null, reason: null }) });
    await page['penalty'](page['rows']()[0]);
    expect(open.mock.lastCall![0]).toBe(PenaltyDialog);
    expect(open.mock.lastCall![1].data).toEqual({ employee: hassan, date: '2026-09-21', hours: 2, reason: 'Left the walk-in open' });
    expect(setPenalty).toHaveBeenCalledWith('hassan', '2026-09-21', null, null);
    expect(notify.info).toHaveBeenCalledWith('Penalty taken off.');

    open.mockReturnValue({ afterClosed: () => of({ employeeId: 'mona', date: '2026-09-21', hours: 1.5, reason: 'Late twice this week' }) });
    await page['penalty'](page['rows']()[1]);
    expect(open.mock.lastCall![1].data).toEqual({ employee: mona, date: '2026-09-21', hours: null, reason: null });
    expect(setPenalty).toHaveBeenLastCalledWith('mona', '2026-09-21', 1.5, 'Late twice this week');
    expect(suspended.refresh).toHaveBeenCalled();
  });

  it('enters a suspended day’s punch-out, after the punch-in left without one', async () => {
    const stuck = day('2026-09-21', {
      status: 'Suspended', lastOut: null, workedMinutes: 0, overtimeMinutes: 0,
      punches: [punch('p0', '2026-09-21T16:05:00', { isIgnored: true }), punch('p1', '2026-09-21T16:07:00')],
    });
    attendance.mockReturnValue(of({ ...report, employees: [{ employee: hassan, day: stuck }] }));
    const { el, page, refresh } = await render();

    page['toggle']('hassan');
    await refresh();
    expect(text(el.querySelector('.detail .actions button')!)).toBe('logout Punch-out بصمة الانصراف');

    open.mockReturnValue({ afterClosed: () => of({ punchedOutAt: '2026-09-22T01:00:00', reason: 'Forgot to scan out' }) });
    await page['resolve'](page['rows']()[0]);
    expect(open.mock.lastCall![0]).toBe(ResolveDayDialog);
    expect(open.mock.lastCall![1].data).toEqual({
      employee: 'Hassan', date: '2026-09-21', shift: '16:00–01:00 +1', shiftEndsAt: '2026-09-22T01:00:00', dayEndsAt: '2026-09-22T10:00:00',
      punchedIn: '2026-09-21T16:07:00',
    });
    expect(resolveDay).toHaveBeenCalledWith('hassan', '2026-09-21', '2026-09-22T01:00:00', 'Forgot to scan out');
    expect(notify.info).toHaveBeenCalledWith('Punch-out entered.');
    expect(suspended.refresh).toHaveBeenCalled();
  });

  it('shows flexible hours for a day with no shift', async () => {
    attendance.mockReturnValue(of({ ...report, employees: [{ employee: mona, day: day('2026-09-21', { shiftStartsAt: null, shiftEndsAt: null, lateMinutes: 0 }) }] }));
    const { el, cells } = await render();

    expect(cells(el.querySelectorAll('tbody tr')[0])[0]).toBe('Flexible');
  });

  it('says when the day is a public holiday: worked by some, a paid day off for the rest', async () => {
    attendance.mockReturnValue(of({
      ...report,
      employees: [
        { employee: hassan, day: day('2026-10-06', { status: 'WorkedOnHoliday', holidayName: 'Armed Forces Day' }) },
        { employee: mona, day: day('2026-10-06', { status: 'Holiday', holidayName: 'Armed Forces Day', firstIn: null, lastOut: null, punches: [] }) },
      ],
    }));
    const { el } = await render();

    expect(text(el.querySelector('.holiday')!)).toBe('celebration Public holiday: Armed Forces Day عطلة رسمية');
    expect([...el.querySelectorAll('tbody tr .status')].map((s) => s.firstChild!.textContent!.trim())).toEqual(['Worked a holiday', 'Public holiday']);
    expect(el.querySelectorAll('tbody tr')[1].classList).toContain('status-Holiday');
  });

  it('offers a working day, a swap, only for a weekly day off', async () => {
    async function options(data: MarkDayData): Promise<string[]> {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: data }] });
      const fixture = TestBed.createComponent(MarkDayDialog);
      await fixture.whenStable();
      fixture.detectChanges();
      return [...(fixture.nativeElement as HTMLElement).querySelectorAll('mat-radio-button')].map((r) => text(r).split(' ')[0] + ' ' + text(r).split(' ')[1]);
    }

    expect(await options({ employee: 'Hassan', day: 'Fri 25 Sept', mark: null, notes: null, weeklyDayOff: true })).toEqual([
      'Leave, paid', 'A day', 'A working', 'No mark:',
    ]);
    expect(await options({ employee: 'Hassan', day: 'Mon 21 Sept', mark: null, notes: null, weeklyDayOff: false })).toEqual(['Leave, paid', 'A day', 'No mark:']);
  });

  it('shows an accountant the punches, with nothing to change', async () => {
    canApprove.set(false);
    const { el, page, refresh } = await render();

    page['toggle']('hassan');
    await refresh();
    expect(el.querySelectorAll('.detail button').length).toBe(0);
  });

  it('opens on one employee’s month from the payslip’s link, a row a day', async () => {
    const sheet: Timesheet = {
      employee: hassan,
      month: '2026-09-01',
      policy: { graceMinutes: 10, doubleScanMinutes: 3 },
      totals: { daysWorked: 1, workedMinutes: 548, lateMinutes: 12, earlyLeaveMinutes: 0, awayMinutes: 0, overtimeMinutes: 20, absentDays: 0, missingPunchDays: 0 },
      days: [day('2026-09-21'), day('2026-09-25', { status: 'DayOff', isWeeklyDayOff: true, punches: [], workedMinutes: 0, lateMinutes: 0, overtimeMinutes: 0 })],
    };
    timesheet.mockReturnValue(of(sheet));
    const { el } = await render({ employee: 'hassan', month: '2026-09-15' });

    expect(timesheet).toHaveBeenCalledWith('hassan', '2026-09-01');
    expect(attendance).not.toHaveBeenCalled();
    expect([...el.querySelectorAll('tbody tr td:first-child strong')].map(text)).toEqual(['Mon 21 Sept', 'Fri 25 Sept']);
    expect(text(el.querySelectorAll('tbody tr')[1].querySelector('td .muted')!)).toBe('Weekly day off · راحة أسبوعية');
    expect([...el.querySelectorAll('.totals .chip')].map(text)[0]).toBe('1 days worked · أيام العمل');
    expect(text(el.querySelector('.note')!)).toContain("Late counts from the shift's start, once past 10 minutes’ grace");
  });
});
