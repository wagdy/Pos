import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { DailyLabourCost, HrApi } from '../../core/hr/hr.api';
import { BusinessDay } from '../costing/financials';
import { DailyCostPage } from './daily-cost-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

function costOf(date: string): DailyLabourCost {
  return {
    date, employed: 3, worked: 2, cost: 450, overtimePay: 40,
    departments: [
      { departmentId: 'kitchen', name: 'Kitchen', worked: 1, cost: 270, overtimePay: 0 },
      { departmentId: 'service', name: 'Service', worked: 1, cost: 180, overtimePay: 40 },
    ],
    employees: [
      {
        employeeId: 'hassan', name: 'Hassan', jobTitle: 'Waiter', departmentId: 'service', employmentType: 'Monthly', status: 'Worked',
        firstIn: `${date}T10:00:00`, lastOut: `${date}T21:00:00`, workedMinutes: 660, overtimeMinutes: 120, dayRate: 180, overtimePay: 40, forgotToCheckOut: false,
      },
      {
        employeeId: 'samir', name: 'Samir', jobTitle: 'Helper', departmentId: 'kitchen', employmentType: 'Daily', status: 'Open',
        firstIn: `${date}T10:05:00`, lastOut: null, workedMinutes: 0, overtimeMinutes: 0, dayRate: 270, overtimePay: 0, forgotToCheckOut: true,
      },
    ],
  };
}

describe('a day’s labour cost', () => {
  const dailyCost = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: HrApi, useValue: { dailyCost } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-22') } },
      ],
    });
    const fixture = TestBed.createComponent(DailyCostPage);
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    };
    await refresh();
    return { el, refresh, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    dailyCost.mockReset().mockImplementation((date: string) => of(costOf(date)));
  });

  it('opens on the business day, so far, with the cost, who came in and the overtime beside it', async () => {
    const { el } = await render();

    expect(dailyCost).toHaveBeenCalledWith('2026-10-22');
    expect(text(el.querySelector('.toolbar')!)).toContain('Today, so far');
    expect([...el.querySelectorAll('.tile')].map(cell)).toEqual([
      'Labour cost · تكلفة العمالة L.E 450.00 each who came in, at their day\'s rate',
      'Came in · حضروا 2 of 3 employed on the day',
      'Overtime · الإضافي L.E 40.00 paid on top, not in the cost',
    ]);
    expect([...el.querySelectorAll('table.departments tbody tr')].map((r) => [...r.querySelectorAll('td')].map(cell))).toEqual([
      ['Kitchen', '1', 'L.E 270.00', '60%', 'L.E 0.00'],
      ['Service', '1', 'L.E 180.00', '40%', 'L.E 40.00'],
    ]);
  });

  it('lists who came in at their day’s rate, marking who forgot to check out', async () => {
    const { el } = await render();

    const rows = [...el.querySelectorAll('table.employees tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].map(cell))).toEqual([
      ['Hassan Waiter · Service', '10:00', '21:00', '11h 00m', 'L.E 180.00 salary ÷ 30', 'L.E 40.00 2h 00m'],
      ['Samir Helper · Kitchen', '10:05', 'Forgot to check out نسي تسجيل الانصراف', '—', 'L.E 270.00 daily wage', 'L.E 0.00'],
    ]);
    expect(rows[1].classList).toContain('forgot');
    expect(cell(el.querySelector('table.employees tfoot tr')!)).toBe('Total · الإجمالي L.E 450.00 L.E 40.00');
  });

  it('goes a day back at a time, and never past the business day', async () => {
    const { el, page, refresh } = await render();

    expect(el.querySelector<HTMLButtonElement>('button[aria-label="The day after"]')!.disabled).toBe(true);
    page['move'](-1);
    await refresh();
    expect(dailyCost).toHaveBeenLastCalledWith('2026-10-21');
    expect(text(el.querySelector('.toolbar')!)).not.toContain('Today, so far');
    page['choose']('2026-10-30');
    expect(page['date']()).toBe('2026-10-21');

    dailyCost.mockReturnValue(of({ ...costOf('2026-10-20'), worked: 0, cost: 0, overtimePay: 0, departments: [], employees: [] }));
    page['move'](-1);
    await refresh();
    expect(text(el.querySelector('p.muted')!)).toBe('No one came in on this day.');
  });
});
