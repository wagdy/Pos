import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AttendanceSignal } from '../../core/hr/attendance-signal';
import { Department, Employee, HrApi, Presence } from '../../core/hr/hr.api';
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

describe('who is in now', () => {
  const presenceOf = vi.fn();
  const punches = { changes: signal(0) };
  const waiting = { count: signal(0), refresh: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HrApi, useValue: { presence: presenceOf, departments: () => of(departments) } },
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

  it('reads again the moment the till says punches came, and points to what waits on Corrections', async () => {
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
