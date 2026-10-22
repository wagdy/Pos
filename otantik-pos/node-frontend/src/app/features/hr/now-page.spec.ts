import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { AttendanceSignal } from '../../core/hr/attendance-signal';
import { AttendanceDevice, Department, Employee, EmployeeStatus, HrApi, Presence } from '../../core/hr/hr.api';
import { CorrectionsCount } from './corrections-count';
import { NowPage } from './now-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Each element's text inside, as Angular drops the spaces between elements.
const parts = (el: Element) => [...el.children].map(text);

function employee(id: string, name: string, departmentId: string, jobTitle = 'Chef'): Employee {
  return {
    id, name, jobTitle, jobRoleId: null, employmentType: 'Monthly', monthlySalary: 5_400, dailyWage: 0, departmentId, shiftStartsAt: '16:00:00',
    shiftEndsAt: '01:00:00', daysOff: [], deviceUserId: null, hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20,
  };
}

function presence(e: Employee, extra: Partial<Presence>): Presence {
  return { employee: e, day: '2026-10-21', state: 'NotArrived', since: null, dueAt: '2026-10-21T16:00:00', dayStatus: 'Open', holidayName: null, ...extra };
}

const departments: Department[] = [
  { id: 'kitchen', name: 'Kitchen', isGeneral: false, employed: 3, everyone: 3, roles: [] },
  { id: 'service', name: 'Service', isGeneral: false, employed: 2, everyone: 2, roles: [] },
];

const everyone: Presence[] = [
  presence(employee('hassan', 'Hassan', 'kitchen'), { state: 'In', since: '2026-10-21T16:03:00' }),
  presence(employee('samir', 'Samir', 'kitchen', 'Helper'), { state: 'Left', since: '2026-10-22T01:10:00' }),
  presence(employee('omar', 'Omar', 'kitchen'), { state: 'Late' }),
  presence(employee('mona', 'Mona', 'service', 'Waiter'), { state: 'NotArrived', dueAt: null }),
  presence(employee('ali', 'Ali', 'service', 'Captain'), { state: 'Off', dayStatus: 'Holiday', holidayName: 'Armed Forces Day' }),
];

const k40: AttendanceDevice = {
  id: 'd1', serialNumber: 'GED7234801177', name: 'K40 Pro (192.168.1.8)', isApproved: true, approvedBy: 'Omar', approvedAtUtc: null,
  firstSeenAtUtc: '', lastSeenAtUtc: '', lastAddress: '192.168.1.8', commands: [], lastCalledInAtUtc: null, callingIn: false,
};

function pushed(employeeId: string, extra: Partial<EmployeeStatus>): EmployeeStatus {
  return { employeeId, day: '2026-10-21', state: 'In', since: null, dueAt: '2026-10-21T16:00:00', dayStatus: 'Open', holidayName: null, at: '2026-10-21T16:22:05', ...extra };
}

describe('who is in now', () => {
  const presenceOf = vi.fn();
  const devices = vi.fn();
  const punches = { changes: signal(0), statuses: new Subject<EmployeeStatus[]>() };
  const waiting = { count: signal(0), refresh: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HrApi,
          useValue: { presence: presenceOf, departments: () => of(departments), devices, deviceConnection: () => of({ devicePort: 5082, addresses: ['192.168.1.57'] }) },
        },
        { provide: AttendanceSignal, useValue: punches },
        { provide: CorrectionsCount, useValue: waiting },
      ],
    });
    const fixture = TestBed.createComponent(NowPage);
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  beforeEach(() => {
    presenceOf.mockReset().mockReturnValue(of({ now: '2026-10-21T16:20:00', employees: everyone }));
    devices.mockReset().mockReturnValue(of([{ ...k40, callingIn: true, lastCalledInAtUtc: '2026-10-21T13:19:50Z' }]));
    waiting.count.set(0);
  });

  it('counts who is in, left, not arrived and off', async () => {
    const { el } = await render();

    expect([...el.querySelectorAll('.count')].map(text)).toEqual([
      '1 inموجود', '1 leftغادر', '2 not arrived, 1 lateلم يحضر بعد', '1 off todayإجازة',
    ]);
    expect(el.querySelectorAll('.count')[2].classList).toContain('late');
    expect(el.querySelector('.corrections')).toBeNull();
  });

  it('shows each department’s people, in first, then late, not arrived, left and off', async () => {
    const { el } = await render();

    const sections = [...el.querySelectorAll('section.department')];
    expect(sections.map((s) => text(s.querySelector('h3')!))).toEqual(['Kitchen · 1 in of 3', 'Service · 0 in of 2']);
    const cards = (s: Element) => [...s.querySelectorAll('.card')].map((c) => [c.classList[1], ...parts(c.querySelector('.state')!)]);
    expect(cards(sections[0])).toEqual([
      ['in', 'Inموجود', 'since 16:03'],
      ['late', 'Lateمتأخر', 'due 16:00'],
      ['left', 'Leftغادر', 'at 01:10 +1'],
    ]);
    expect(cards(sections[1])).toEqual([
      ['waiting', 'Not arrived yetلم يحضر بعد', 'flexible hours'],
      ['off', 'Public holidayعطلة رسمية', 'Armed Forces Day'],
    ]);
    expect(sections[0].querySelector('.card')!.getAttribute('href')).toBe('/hr/attendance?employee=hassan&month=2026-10-21');
  });

  it('changes a pushed card in place, lit up for a moment, without reading the page again', async () => {
    const { fixture, el } = await render();
    const card = () => [...el.querySelectorAll('.card')].find((c) => c.textContent!.includes('Omar'))!;
    expect(card().classList).toContain('late');

    vi.useFakeTimers();
    try {
      punches.statuses.next([pushed('omar', { state: 'In', since: '2026-10-21T16:22:00' })]);
      fixture.detectChanges();
      expect(presenceOf).toHaveBeenCalledTimes(1);
      expect([card().classList.contains('in'), card().classList.contains('fresh')]).toEqual([true, true]);
      expect(parts(card().querySelector('.state')!)).toEqual(['Inموجود', 'since 16:22']);
      expect(text(el.querySelector('.count.in')!)).toBe('2 inموجود');
      expect(text(el.querySelector('.toolbar .muted')!)).toBe('As of 16:22 · live');

      vi.advanceTimersByTime(4_000);
      fixture.detectChanges();
      expect(card().classList.contains('fresh')).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('reads the page again for a pushed employee it does not show yet', async () => {
    await render();
    punches.statuses.next([pushed('new-hire', {})]);
    expect(presenceOf).toHaveBeenCalledTimes(2);
  });

  it('says when the device is not calling in over ADMS, and where to point it', async () => {
    devices.mockReturnValue(of([k40]));
    const { el } = await render();

    expect(text(el.querySelector('.quiet')!)).toBe(
      'wifi_offK40 Pro (192.168.1.8) is not calling in over ADMS: punches reach the till only when someone presses Sync. ' +
        'On the device, set Cloud Server Setting to 192.168.1.57, port 5082. Attendance devices',
    );
    expect(el.querySelector('.quiet a')!.getAttribute('href')).toBe('/hr/devices');
    TestBed.resetTestingModule();

    devices.mockReturnValue(of([{ ...k40, callingIn: true }]));
    expect((await render()).el.querySelector('.quiet')).toBeNull();
  });

  it('reads it all again after the connection was lost, and points to what waits on Corrections', async () => {
    const { fixture, el } = await render();
    expect(presenceOf).toHaveBeenCalledTimes(1);

    presenceOf.mockReturnValue(of({ now: '2026-10-21T16:21:00', employees: [presence(everyone[2].employee, { state: 'In', since: '2026-10-21T16:21:00' })] }));
    punches.changes.set(1);
    waiting.count.set(2);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(presenceOf).toHaveBeenCalledTimes(2);
    expect(parts(el.querySelector('.card .state')!)).toEqual(['Inموجود', 'since 16:21']);
    expect(text(el.querySelector('.corrections')!)).toBe('rule 2 waiting on Corrections');
  });
});
