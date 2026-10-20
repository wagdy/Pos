import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA } from '@angular/material/dialog';
import { PenaltyData, PenaltyDialog, ResolveDayData, ResolveDayDialog } from './attendance-dialogs';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

async function open<T>(component: Type<T>, data: unknown) {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: data }] });
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const dialog = fixture.componentInstance as unknown as Record<string, any>;
  // After a field is changed by hand, as ngModel would.
  const refresh = () => {
    fixture.componentRef.changeDetectorRef.markForCheck();
    fixture.detectChanges();
  };
  return { el, dialog, refresh };
}

describe('the punch-out dialog', () => {
  const data: ResolveDayData = {
    employee: 'Hassan', date: '2026-09-21', shift: '16:00–01:00 +1', shiftEndsAt: '2026-09-22T01:00:00', punchedIn: '2026-09-21T16:07:00',
  };

  it('opens on the date the shift ends, with the shift’s end a click away', async () => {
    const { el, dialog, refresh } = await open(ResolveDayDialog, data);

    expect(text(el.querySelector('h2')!)).toBe('Hassan’s punch-out, Mon 21 Sept بصمة الانصراف');
    expect(text(el.querySelector('p.muted')!)).toContain('In at 16:07 and never out; their shift runs 16:00–01:00 +1.');
    expect([dialog['date'], dialog['time']]).toEqual(['2026-09-22', '']);
    expect(dialog['ready']()).toBe(false);

    dialog['atShiftEnd']();
    dialog['reason'] = 'Forgot to scan out';
    refresh();
    expect(dialog['punchedOutAt']()).toBe('2026-09-22T01:00:00');
    expect(text(el.querySelector('mat-dialog-content')!)).toContain('8h 53m after the punch-in.');
    expect(dialog['ready']()).toBe(true);
  });

  it('takes a punch-out only after the punch-in, within 18 hours of it, and before the next day', async () => {
    const { el, dialog, refresh } = await open(ResolveDayDialog, data);
    dialog['reason'] = 'Forgot to scan out';

    dialog['date'] = '2026-09-21';
    dialog['time'] = '15:00';
    refresh();
    expect(text(el.querySelector('.problem')!)).toBe('The punch-out comes after the punch-in at 16:07.');
    expect(dialog['ready']()).toBe(false);

    dialog['date'] = '2026-09-22';
    dialog['time'] = '11:00';
    refresh();
    expect(text(el.querySelector('.problem')!)).toBe('The punch-out is within 18 hours of the punch-in.');

    dialog['time'] = '10:00';
    refresh();
    expect(text(el.querySelector('.problem')!)).toBe('The punch-out is before 10:00 +1, when the next day starts.');

    dialog['time'] = '09:59';
    refresh();
    expect(el.querySelector('.problem')).toBeNull();
    expect(dialog['ready']()).toBe(true);
  });

  it('offers no shortcut when the punch-in came after the shift had ended', async () => {
    const { el, dialog } = await open(ResolveDayDialog, { ...data, punchedIn: '2026-09-22T03:00:00' });

    expect(el.querySelector('.shortcut')).toBeNull();
    expect(dialog['date']).toBe('2026-09-22');
  });
});

describe('the penalty dialog', () => {
  const hassan = { id: 'hassan', name: 'Hassan', hourlyRate: 20 };
  const mona = { id: 'mona', name: 'Mona', hourlyRate: 25 };

  it('changes a day’s penalty, or takes it off', async () => {
    const data: PenaltyData = { employee: hassan, date: '2026-09-21', hours: 2, reason: 'Left the walk-in open' };
    const { el, dialog } = await open(PenaltyDialog, data);

    expect(text(el.querySelector('mat-dialog-content p')!)).toBe('Hassan, Mon 21 Sept');
    expect(text(el.querySelector('.cost')!)).toBe('− L.E 40.00 at L.E 20.00 an hour');
    expect([...el.querySelectorAll('mat-dialog-actions button')].map(text)).toEqual(['Take it off', 'Cancel', 'Save']);
    expect(dialog['answer'](null)).toEqual({ employeeId: 'hassan', date: '2026-09-21', hours: null, reason: null });
    expect(dialog['answer'](2)).toEqual({ employeeId: 'hassan', date: '2026-09-21', hours: 2, reason: 'Left the walk-in open' });
  });

  it('adds one for whoever is chosen, priced at their rate, and says when it replaces the day’s', async () => {
    const data: PenaltyData = {
      employees: [hassan, mona],
      month: { first: '2026-09-01', last: '2026-09-30' },
      taken: [{ employeeId: 'mona', date: '2026-09-21', hours: 2 }],
    };
    const { el, dialog, refresh } = await open(PenaltyDialog, data);

    expect([...el.querySelectorAll('mat-dialog-actions button')].map(text)).toEqual(['Cancel', 'Save']);
    expect(dialog['ready']()).toBe(false);

    Object.assign(dialog, { employeeId: 'mona', date: '2026-09-21', hours: 1.5, reason: 'Late twice this week' });
    refresh();
    expect(text(el.querySelector('.cost')!)).toBe('− L.E 37.50 at L.E 25.00 an hour');
    expect(text(el.querySelector('.problem')!)).toBe('There is a penalty of 2h on this day already: this replaces it, as there is one a day.');
    expect(dialog['ready']()).toBe(true);

    Object.assign(dialog, { employeeId: 'hassan' });
    refresh();
    expect(el.querySelector('.problem')).toBeNull();
    expect(text(el.querySelector('.cost')!)).toBe('− L.E 30.00 at L.E 20.00 an hour');
  });

  it('takes more than no hours, at most 72, to two decimal places', async () => {
    const { el, dialog, refresh } = await open(PenaltyDialog, { employee: hassan, date: '2026-09-21' });
    dialog['reason'] = 'Left the walk-in open';

    for (const [hours, valid] of [[0, false], [-1, false], [72.5, false], [1.333, false], [0.25, true], [72, true]] as const) {
      dialog['hours'] = hours;
      expect([hours, dialog['ready']()]).toEqual([hours, valid]);
    }
    dialog['hours'] = 1.333;
    refresh();
    expect(text(el.querySelector('.problem')!)).toBe('More than no hours and at most 72, to two decimal places: 1.5, 0.25.');
  });
});
