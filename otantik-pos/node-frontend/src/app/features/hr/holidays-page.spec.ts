import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { HrApi, PublicHoliday, egyptFixedHolidays, shamElNessim } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { HolidayData, HolidayDialog } from './holiday-dialog';
import { HolidaysPage } from './holidays-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function holiday(id: string, name: string, firstDay: string, lastDay = firstDay, days = 1): PublicHoliday {
  return { id, name, firstDay, lastDay, days, setBy: 'Omar', setAtUtc: '2026-01-02T10:00:00Z' };
}

describe('Egypt’s fixed-date holidays', () => {
  it('fall on their dates, and Sham El-Nessim on the Monday after Coptic Easter', () => {
    expect([2024, 2025, 2026, 2027].map(shamElNessim)).toEqual(['2024-05-06', '2025-04-21', '2026-04-13', '2027-05-03']);
    expect(egyptFixedHolidays(2026).map((h) => h.day)).toEqual([
      '2026-01-07', '2026-01-25', '2026-04-13', '2026-04-25', '2026-05-01', '2026-06-30', '2026-07-23', '2026-10-06',
    ]);
  });
});

describe('the holidays', () => {
  const holidays = vi.fn();
  const saveHoliday = vi.fn();
  const deleteHoliday = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  const entered = [
    holiday('h1', 'Coptic Christmas', '2026-01-07'),
    holiday('h2', 'Eid al-Fitr', '2026-03-20', '2026-03-22', 3),
    // Moved to the Thursday: still the one suggested, by its name.
    holiday('h3', 'Armed Forces Day', '2026-10-08'),
  ];

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: HrApi, useValue: { holidays, saveHoliday, deleteHoliday } },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-21') } },
      ],
    });
    const fixture = TestBed.createComponent(HolidaysPage);
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    holidays.mockReset().mockReturnValue(of(entered));
    saveHoliday.mockReset().mockReturnValue(of({}));
    deleteHoliday.mockReset().mockReturnValue(of(undefined));
    open.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
  });

  it('lists the year’s holidays, and the fixed-date ones not entered yet', async () => {
    const { el } = await render();

    expect(holidays).toHaveBeenCalledWith(2026);
    expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(text))).toEqual([
      ['Coptic Christmas', 'Wed 7 Jan', '1', 'Omar'],
      ['Eid al-Fitr', 'Fri 20 Mar – Sun 22 Mar', '3', 'Omar'],
      ['Armed Forces Day', 'Thu 8 Oct', '1', 'Omar'],
    ]);
    expect([...el.querySelectorAll('.suggested li strong')].map(text)).toEqual([
      'January 25 Revolution and Police Day', 'Sham El-Nessim', 'Sinai Liberation Day', 'Labour Day', 'June 30 Revolution', 'July 23 Revolution',
    ]);
  });

  it('adds a suggested holiday as the manager confirms it, and changes or takes one off', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of({ name: 'Sham El-Nessim', firstDay: '2026-04-13', lastDay: '2026-04-13' }) });
    await page['add']('Sham El-Nessim', '2026-04-13');
    expect(open.mock.lastCall![0]).toBe(HolidayDialog);
    expect(open.mock.lastCall![1].data).toEqual({ name: 'Sham El-Nessim', firstDay: '2026-04-13', lastDay: '2026-04-13' });
    expect(saveHoliday).toHaveBeenCalledWith(expect.any(String), 'Sham El-Nessim', '2026-04-13', '2026-04-13');
    expect(notify.info).toHaveBeenCalledWith('Holiday added.');
    expect(holidays).toHaveBeenCalledTimes(2);

    open.mockReturnValue({ afterClosed: () => of({ name: 'Eid al-Fitr', firstDay: '2026-03-20', lastDay: '2026-03-23' }) });
    await page['change'](entered[1]);
    expect(open.mock.lastCall![1].data).toEqual({ name: 'Eid al-Fitr', firstDay: '2026-03-20', lastDay: '2026-03-22', existing: true });
    expect(saveHoliday).toHaveBeenLastCalledWith('h2', 'Eid al-Fitr', '2026-03-20', '2026-03-23');

    open.mockReturnValue({ afterClosed: () => of({ remove: true }) });
    await page['change'](entered[2]);
    expect(deleteHoliday).toHaveBeenCalledWith('h3');
    expect(notify.info).toHaveBeenLastCalledWith('Holiday taken off.');
  });

  it('shows an accountant the holidays, with nothing to change', async () => {
    canApprove.set(false);
    const { el } = await render();

    expect(el.querySelectorAll('button:not([aria-label*="year"])').length).toBe(0);
    expect(el.querySelectorAll('.suggested li').length).toBe(6);
  });

  it('opens a new year with none entered', async () => {
    holidays.mockReturnValue(of([]));
    const { el, page } = await render();

    expect(text(el.querySelector('tbody tr')!)).toBe('No holidays entered for 2026.');
    page['moveYear'](1);
    expect(holidays).toHaveBeenLastCalledWith(2027);
  });
});

describe('the holiday dialog', () => {
  async function open(data: HolidayData) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: data }] });
    const fixture = TestBed.createComponent(HolidayDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    const refresh = () => {
      fixture.componentRef.changeDetectorRef.markForCheck();
      fixture.detectChanges();
    };
    return { el: fixture.nativeElement as HTMLElement, dialog: fixture.componentInstance as unknown as Record<string, any>, refresh };
  }

  it('runs one day unless told otherwise, and at most ten', async () => {
    const { el, dialog, refresh } = await open({});
    dialog['name'] = 'Eid al-Adha';

    dialog['pickFirst']('2026-05-26');
    refresh();
    expect(dialog['lastDay']).toBe('2026-05-26');
    expect(text(el.querySelector('mat-dialog-content p')!)).toBe('1 day: Tue 26 May');

    dialog['lastDay'] = '2026-05-29';
    refresh();
    expect(text(el.querySelector('mat-dialog-content p')!)).toBe('4 days: Tue 26 May – Fri 29 May');
    expect(dialog['ready']()).toBe(true);
    // A later first day keeps a last day still after it.
    dialog['pickFirst']('2026-05-27');
    expect(dialog['lastDay']).toBe('2026-05-29');

    dialog['lastDay'] = '2026-06-10';
    refresh();
    expect(text(el.querySelector('.problem')!)).toBe('A holiday runs 10 days at most: enter a longer one as two.');
    expect(dialog['ready']()).toBe(false);

    dialog['lastDay'] = '2026-05-20';
    refresh();
    expect(text(el.querySelector('.problem')!)).toBe('The last day comes on or after the first.');
  });

  it('offers to take off a holiday already entered', async () => {
    const { el } = await open({ name: 'Eid', firstDay: '2026-03-20', lastDay: '2026-03-22', existing: true });

    expect(text(el.querySelector('h2')!)).toBe('Change the holiday عطلة رسمية');
    expect([...el.querySelectorAll('mat-dialog-actions button')].map(text)).toEqual(['Take it off', 'Cancel', 'Save']);
  });
});
