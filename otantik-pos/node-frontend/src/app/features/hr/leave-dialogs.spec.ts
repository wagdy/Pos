import { HttpErrorResponse } from '@angular/common/http';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { Employee } from '../../core/hr/hr.api';
import { LeaveBalance, LeaveRequest, LeaveType } from '../../core/hr/leaves.api';
import { CancelLeaveData, CancelLeaveDialog, LeaveBalanceDialog, LeaveRequestData, LeaveRequestDialog, LeaveTypeDialog } from './leave-dialogs';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

async function open<T>(component: Type<T>, data: unknown) {
  const close = vi.fn();
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: { close } },
    ],
  });
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const dialog = fixture.componentInstance as unknown as Record<string, any>;
  const refresh = async () => {
    await fixture.whenStable();
    fixture.componentRef.changeDetectorRef.markForCheck();
    fixture.detectChanges();
  };
  return { el, dialog, refresh, close };
}

const hassan = { id: 'hassan', name: 'Hassan', jobTitle: 'Chef', isActive: true } as Employee;
const mona = { id: 'mona', name: 'Mona', jobTitle: 'Cashier', isActive: true } as Employee;
const types = [
  { id: 'annual', name: 'Annual (اعتيادي)', isPaid: true },
  { id: 'unpaid', name: 'Unpaid (بدون راتب)', isPaid: false },
];

function balance(over: Partial<LeaveBalance> = {}): LeaveBalance {
  return {
    employeeId: 'hassan', employeeName: 'Hassan', leaveTypeId: 'annual', leaveTypeName: 'Annual (اعتيادي)', year: 2026, allocatedDays: 21, usedDays: 16,
    remainingDays: 5, pendingDays: 2, opened: true, allocatedBy: null, ...over,
  };
}

function request(over: Partial<LeaveRequest> = {}): LeaveRequest {
  return {
    id: 'r1', employeeId: 'hassan', employeeName: 'Hassan', leaveTypeId: 'annual', leaveTypeName: 'Annual (اعتيادي)', isPaid: true, startDate: '2026-11-01',
    endDate: '2026-11-05', days: 5, daysTaken: 5, status: 'Approved', reason: null, managerNotes: null, requestedBy: 'Hassan', requestedAtUtc: '2026-10-20T09:00:00Z',
    decidedBy: 'Omar', decidedAtUtc: '2026-10-21T09:00:00Z', cancelledFrom: null, cancelledDays: 0, cancelledBy: null, cancelledAtUtc: null,
    cancellationNotes: null, ...over,
  };
}

describe('asking for leave', () => {
  const countDays = vi.fn();
  const balances = vi.fn();
  const submit = vi.fn();

  beforeEach(() => {
    countDays.mockReset().mockReturnValue(of({ days: 3, dates: ['2026-11-01', '2026-11-02', '2026-11-03'] }));
    balances.mockReset().mockReturnValue(of([balance()]));
    submit.mockReset().mockReturnValue(of(request({ status: 'Pending', days: 3 })));
  });

  it('takes whom it is for on HR, shows the working days and what is left, and sends it', async () => {
    const data: LeaveRequestData = { employees: [hassan, mona], types, countDays, balances, submit };
    const { el, dialog, refresh, close } = await open(LeaveRequestDialog, data);

    expect(text(el.querySelector('h2')!)).toBe('Request leave طلب إجازة');
    // Asked without whom it is for: said, and nothing sent.
    await dialog['send']();
    await refresh();
    expect(text(el.querySelector('.problem')!)).toBe('Choose whom it is for.');
    expect(submit).not.toHaveBeenCalled();

    dialog['pickEmployee']('hassan');
    dialog['pickStart']('2026-11-01');
    // The end follows the start until it is set apart.
    expect(dialog['endDate']()).toBe('2026-11-01');
    dialog['pickEnd']('2026-11-03');
    await refresh();
    expect(balances).toHaveBeenCalledWith('hassan', 2026);
    expect(countDays).toHaveBeenLastCalledWith('hassan', '2026-11-01', '2026-11-03');
    expect(text(el.querySelector('.facts')!)).toBe('3 days of work: Sun 1 Nov, Mon 2 Nov, Tue 3 Nov 5 of 21 days of Annual (اعتيادي) left in 2026, 2 asked for already.');
    // 3 is within what is left once 2 asked already are counted: 5 − 2.
    expect(el.querySelector('.facts .problem')).toBeNull();

    dialog['reason'].set('  Family wedding ');
    await dialog['send']();
    expect(submit).toHaveBeenCalledWith({ employeeId: 'hassan', leaveTypeId: 'annual', startDate: '2026-11-01', endDate: '2026-11-03', reason: 'Family wedding' });
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ status: 'Pending', days: 3 }));
  });

  it('says when it is more than is left, when the dates hold no working day, and the till’s refusal', async () => {
    const data: LeaveRequestData = { employees: [hassan], types, countDays, balances, submit };
    const { el, dialog, refresh, close } = await open(LeaveRequestDialog, data);
    dialog['pickEmployee']('hassan');
    countDays.mockReturnValue(of({ days: 4, dates: ['2026-11-01', '2026-11-02', '2026-11-03', '2026-11-04'] }));
    dialog['pickStart']('2026-11-01');
    dialog['pickEnd']('2026-11-04');
    await refresh();
    expect(el.querySelector('.facts .problem')).not.toBeNull();
    await dialog['send']();
    expect(dialog['problem']()).toBe('That is more than is left of the balance, less what is asked for already.');

    // Unpaid: no balance to run out.
    dialog['typeId'].set('unpaid');
    await refresh();
    expect(text(el.querySelector('.facts')!)).toContain('Unpaid: a day’s pay off each day, never an absence.');

    countDays.mockReturnValue(of({ days: 0, dates: [] }));
    dialog['pickStart']('2026-11-06');
    dialog['pickEnd']('2026-11-07');
    await refresh();
    expect(text(el.querySelector('.facts')!)).toContain('Those dates hold no working day');
    await dialog['send']();
    expect(dialog['problem']()).toBe('Those dates hold no working day: there is nothing to take.');

    dialog['pickEnd']('2027-01-02');
    await dialog['send']();
    expect(dialog['problem']()).toBe('A leave runs within one year: ask for each year’s days apart.');

    countDays.mockReturnValue(of({ days: 2, dates: ['2026-11-08', '2026-11-09'] }));
    submit.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { detail: 'Sun 8 Nov is asked for already.' } })));
    dialog['pickStart']('2026-11-08');
    dialog['pickEnd']('2026-11-09');
    await refresh();
    await dialog['send']();
    await refresh();
    expect(text(el.querySelector('.problem')!)).toBe('Sun 8 Nov is asked for already.');
    expect(close).not.toHaveBeenCalled();
  });

  it('asks for the employee’s own in the portal, with no one to choose', async () => {
    const data: LeaveRequestData = { types, countDays, balances, submit };
    const { el, dialog, refresh } = await open(LeaveRequestDialog, data);
    expect(el.querySelector('app-employee-picker')).toBeNull();
    dialog['pickStart']('2026-11-01');
    dialog['pickEnd']('2026-11-03');
    await refresh();
    expect(countDays).toHaveBeenCalledWith(null, '2026-11-01', '2026-11-03');
    await dialog['send']();
    expect(submit.mock.lastCall![0].employeeId).toBeNull();
  });
});

describe('cancelling an approved leave', () => {
  const countDays = vi.fn();

  beforeEach(() => countDays.mockReset().mockReturnValue(of({ days: 2, dates: ['2026-11-04', '2026-11-05'] })));

  it('opens on the business day, counts the days given back, and cancels all of it from its first day', async () => {
    const data: CancelLeaveData = { request: request(), today: '2026-11-04', countDays };
    const { el, dialog, refresh, close } = await open(CancelLeaveDialog, data);
    await refresh();

    expect(dialog['from']()).toBe('2026-11-04');
    expect(countDays).toHaveBeenCalledWith('2026-11-04');
    expect(text(el.querySelector('mat-dialog-content p[aria-live]')!)).toBe('2 days given back to the balance; the 3 days before 4 Nov stay taken.');

    countDays.mockReturnValue(of({ days: 4, dates: [] }));
    dialog['pick']('2026-11-02');
    await refresh();
    expect(text(el.querySelector('mat-dialog-content p[aria-live]')!)).toBe('4 days given back to the balance; the day before 2 Nov stays taken.');

    dialog['pick']('2026-10-30');
    await refresh();
    expect(text(el.querySelector('mat-dialog-content p[aria-live]')!)).toBe('All of it is cancelled: none of its 5 days is taken.');

    dialog['pick']('2026-11-06');
    await refresh();
    expect(text(el.querySelector('.problem')!)).toBe('It ended on 5 Nov: choose a day of it.');
    expect((el.querySelectorAll('mat-dialog-actions button')[1] as HTMLButtonElement).disabled).toBe(true);
    expect(close).not.toHaveBeenCalled();
  });

  it('opens on its first day when it has not begun, and on its last when it is over', async () => {
    expect((await open(CancelLeaveDialog, { request: request(), today: '2026-10-26', countDays })).dialog['from']()).toBe('2026-11-01');
    expect((await open(CancelLeaveDialog, { request: request(), today: '2026-12-01', countDays })).dialog['from']()).toBe('2026-11-05');
  });
});

describe('a balance’s days', () => {
  it('takes whole or half days, never below what is taken', async () => {
    const { el, dialog, refresh, close } = await open(LeaveBalanceDialog, balance());
    expect(text(el.querySelector('.facts')!)).toBe('16 taken · 2 asked for · 5 left of 21');

    dialog['days'] = 20.25;
    dialog['save']();
    await refresh();
    expect(text(el.querySelector('.problem')!)).toBe('Give a balance in whole or half days: 21, 10.5.');
    dialog['days'] = 15;
    dialog['save']();
    expect(dialog['problem']()).toBe('16 of its days are taken already: give at least that many.');
    dialog['days'] = 22.5;
    dialog['save']();
    expect(close).toHaveBeenCalledWith(22.5);
  });

  it('says when the year’s days are not kept yet', async () => {
    const { el } = await open(LeaveBalanceDialog, balance({ opened: false, usedDays: 0, remainingDays: 21, pendingDays: 0 }));
    expect(text(el.querySelector('.facts')!)).toContain('The year’s days as it would open, not kept yet.');
  });
});

describe('a leave type', () => {
  it('is paid with its days a year, shared for new hires or not, or unpaid with none', async () => {
    const { dialog, close } = await open(LeaveTypeDialog, null);
    dialog['name'] = ' Maternity ';
    dialog['days'] = 90;
    dialog['save']();
    expect(close).toHaveBeenLastCalledWith({ name: 'Maternity', isPaid: true, defaultDaysPerYear: 90, proratedForNewHires: false });

    dialog['isPaid'] = false;
    dialog['prorated'] = true;
    dialog['save']();
    expect(close).toHaveBeenLastCalledWith({ name: 'Maternity', isPaid: false, defaultDaysPerYear: 0, proratedForNewHires: false });
  });

  it('keeps whether one in use is paid, and does not offer to take it off', async () => {
    const annual: LeaveType = { id: 'annual', name: 'Annual', isPaid: true, defaultDaysPerYear: 21, proratedForNewHires: true, inUse: true };
    const { el } = await open(LeaveTypeDialog, annual);
    expect(el.querySelector('mat-slide-toggle button')!.hasAttribute('disabled') || el.querySelector('mat-slide-toggle')!.classList.contains('mat-mdc-slide-toggle-disabled')).toBe(true);
    expect([...el.querySelectorAll('mat-dialog-actions button')].map(text)).toEqual(['Cancel', 'Save']);

    const sick: LeaveType = { ...annual, id: 'sick', name: 'Sick', inUse: false };
    const { el: unused } = await open(LeaveTypeDialog, sick);
    expect([...unused.querySelectorAll('mat-dialog-actions button')].map(text)).toEqual(['Take it off', 'Cancel', 'Save']);
  });
});
