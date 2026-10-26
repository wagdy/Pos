import { Type, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { HrAccess } from '../../core/hr/hr-access';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { LeaveBalance, LeaveRequest, LeaveType, LeavesApi } from '../../core/hr/leaves.api';
import { NotifyService } from '../../core/ui/notify.service';
import { BusinessDay } from '../costing/financials';
import { LeaveBalancesPage } from './leave-balances-page';
import { CancelLeaveDialog, LeaveBalanceDialog, LeaveRequestDialog, LeaveTypeDialog, RejectLeaveDialog } from './leave-dialogs';
import { LeaveRequestsPage } from './leave-requests-page';
import { LeaveTypesPage } from './leave-types-page';
import { PendingLeaves } from './pending-leaves';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function request(id: string, over: Partial<LeaveRequest> = {}): LeaveRequest {
  return {
    id, employeeId: 'hassan', employeeName: 'Hassan', leaveTypeId: 'annual', leaveTypeName: 'Annual', isPaid: true, startDate: '2026-11-01',
    endDate: '2026-11-05', days: 5, daysTaken: 5, status: 'Pending', reason: 'Family wedding', managerNotes: null, requestedBy: 'Hassan',
    requestedAtUtc: '2026-10-20T09:00:00Z', decidedBy: null, decidedAtUtc: null, cancelledFrom: null, cancelledDays: 0, cancelledBy: null,
    cancelledAtUtc: null, cancellationNotes: null, ...over,
  };
}

function balance(employeeId: string, name: string, typeId: string, typeName: string, over: Partial<LeaveBalance> = {}): LeaveBalance {
  return {
    employeeId, employeeName: name, leaveTypeId: typeId, leaveTypeName: typeName, year: 2026, allocatedDays: 21, usedDays: 4, remainingDays: 17,
    pendingDays: 0, opened: true, allocatedBy: null, ...over,
  };
}

const api = {
  requests: vi.fn(),
  balances: vi.fn(),
  types: vi.fn(),
  approve: vi.fn(),
  reject: vi.fn(),
  cancel: vi.fn(),
  days: vi.fn(),
  submit: vi.fn(),
  setBalance: vi.fn(),
  saveType: vi.fn(),
  deleteType: vi.fn(),
};
const open = vi.fn();
const notify = { info: vi.fn(), error: vi.fn() };
const pending = { count: signal(0), refresh: vi.fn() };

async function render<T>(component: Type<T>, who: 'manager' | 'accountant' | 'viewer' = 'manager') {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: LeavesApi, useValue: api },
      { provide: HrApi, useValue: { employees: () => of([{ id: 'hassan', name: 'Hassan', isActive: true }, { id: 'gone', name: 'Gone', isActive: false }] as Employee[]) } },
      { provide: AuthService, useValue: { employeeActing: () => false, can: (p: string) => p !== Permissions.HrManage || who !== 'viewer' } },
      { provide: HrAccess, useValue: { manager: signal(who === 'manager'), has: () => true } },
      { provide: MatDialog, useValue: { open } },
      { provide: NotifyService, useValue: notify },
      { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-11-03') } },
      { provide: PendingLeaves, useValue: pending },
    ],
  });
  const fixture = TestBed.createComponent(component);
  const el = fixture.nativeElement as HTMLElement;
  const refresh = async () => {
    for (let i = 0; i < 6; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
  };
  await refresh();
  return { el, refresh, page: fixture.componentInstance as unknown as Record<string, any> };
}

beforeEach(() => {
  Object.values(api).forEach((f) => f.mockReset());
  open.mockReset();
  notify.info.mockReset();
  notify.error.mockReset();
  pending.refresh.mockReset();
  pending.count.set(0);
});

describe('the leave requests', () => {
  const waiting = [request('r1'), request('r2', { employeeId: 'mona', employeeName: 'Mona', leaveTypeId: 'unpaid', leaveTypeName: 'Unpaid', isPaid: false, reason: null, startDate: '2026-11-10', endDate: '2026-11-10', days: 1 })];
  const year = [
    request('r3', { status: 'Approved', decidedBy: 'Omar', decidedAtUtc: '2026-10-02T09:00:00Z', startDate: '2026-11-02', endDate: '2026-11-05', days: 4, daysTaken: 4 }),
    request('r4', { status: 'Approved', decidedBy: 'Omar', cancelledFrom: '2026-10-14', cancelledDays: 2, daysTaken: 3, cancelledBy: 'Omar', cancellationNotes: 'Recalled' }),
    request('r5', { status: 'Rejected', decidedBy: 'Omar', managerNotes: 'Busy week' }),
    ...waiting,
  ];

  beforeEach(() => {
    api.requests.mockImplementation((f: { status?: string }) => of(f.status === 'Pending' ? waiting : year));
    api.balances.mockReturnValue(of([balance('hassan', 'Hassan', 'annual', 'Annual', { remainingDays: 3 })]));
    api.approve.mockReturnValue(of({ ...waiting[0], status: 'Approved' }));
    api.reject.mockReturnValue(of({ ...waiting[0], status: 'Rejected' }));
    api.cancel.mockReturnValue(of({ ...year[0], cancelledFrom: '2026-11-04', cancelledDays: 2 }));
    api.days.mockReturnValue(of({ days: 2, dates: [] }));
  });

  it('lists those waiting, the oldest first, with what is left of the balance, and the year’s decided ones', async () => {
    const { el } = await render(LeaveRequestsPage);

    expect(api.requests).toHaveBeenCalledWith({ status: 'Pending' });
    expect(api.requests).toHaveBeenCalledWith({ year: 2026 });
    expect(api.balances).toHaveBeenCalledWith(2026);
    expect(pending.count()).toBe(2);
    const rows = [...el.querySelectorAll('table.pending tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].slice(0, 5).map(text))).toEqual([
      ['Hassan', 'Annual', 'Sun 1 Nov – Thu 5 Nov', '5 3 left', 'Family wedding'],
      ['Mona', 'UnpaidUnpaid', 'Tue 10 Nov', '1', '—'],
    ]);
    // More than is left: said in red.
    expect(rows[0].querySelector('.short')).not.toBeNull();
    expect([...rows[0].querySelectorAll('td.actions button')].map(text)).toEqual(['check Approve', 'close Reject']);

    const decided = [...el.querySelectorAll('.year-part tbody tr')];
    expect(decided.map((r) => text(r.querySelectorAll('td')[4]))).toEqual([
      'Approvedموافق عليها',
      'Cut shortقُطعتfrom Wed 14 Oct by Omar · Recalled',
      'RejectedمرفوضةBusy week',
    ]);
    expect(text(decided[1].querySelectorAll('td')[3])).toBe('3 of 5');
    // Cancelled from a day: only an approved one not cut short already.
    expect(decided.map((r) => !!r.querySelector('button'))).toEqual([true, false, false]);
  });

  it('approves at a click, rejects with why, and counts what waits again', async () => {
    const { page } = await render(LeaveRequestsPage);

    await page['approve'](waiting[0]);
    expect(api.approve).toHaveBeenCalledWith('r1', null);
    expect(notify.info).toHaveBeenCalledWith('Approved: Hassan’s 5 days of Annual, marked on the attendance.');
    // The count on the tabs from the list read again.
    expect(pending.count()).toBe(2);

    open.mockReturnValue({ afterClosed: () => of('Busy week') });
    await page['reject'](waiting[0]);
    expect(open.mock.lastCall![0]).toBe(RejectLeaveDialog);
    expect(api.reject).toHaveBeenCalledWith('r1', 'Busy week');

    // Kept waiting after all.
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    await page['reject'](waiting[1]);
    expect(api.reject).toHaveBeenCalledTimes(1);
  });

  it('cancels an approved leave from the day chosen, counting the days from it to the end', async () => {
    const { page } = await render(LeaveRequestsPage);

    open.mockReturnValue({ afterClosed: () => of({ from: '2026-11-04', notes: 'Recalled' }) });
    await page['cancel'](year[0]);
    expect(open.mock.lastCall![0]).toBe(CancelLeaveDialog);
    const data = open.mock.lastCall![1].data;
    expect(data.today).toBe('2026-11-03');
    data.countDays('2026-11-04');
    expect(api.days).toHaveBeenCalledWith('hassan', '2026-11-04', '2026-11-05');
    expect(api.cancel).toHaveBeenCalledWith('r3', '2026-11-04', 'Recalled');
    expect(notify.info).toHaveBeenCalledWith('Hassan’s leave cut short from Wed 4 Nov: 2 days given back.');
  });

  it('asks for leave for an active employee, through the till’s leave API', async () => {
    api.types.mockReturnValue(of([{ id: 'annual', name: 'Annual', isPaid: true }]));
    api.submit.mockReturnValue(of(request('r9')));
    const { page } = await render(LeaveRequestsPage);

    open.mockReturnValue({ afterClosed: () => of(request('r9')) });
    await page['ask']();
    expect(open.mock.lastCall![0]).toBe(LeaveRequestDialog);
    const data = open.mock.lastCall![1].data;
    expect(data.employees.map((e: Employee) => e.id)).toEqual(['hassan']);
    data.submit({ employeeId: 'hassan', leaveTypeId: 'annual', startDate: '2026-11-01', endDate: '2026-11-05', reason: null });
    expect(api.submit).toHaveBeenCalledWith({ employeeId: 'hassan', leaveTypeId: 'annual', startDate: '2026-11-01', endDate: '2026-11-05', reason: null });
    expect(notify.info).toHaveBeenCalledWith('Leave asked for Hassan: 5 days of Annual, waiting for a manager.');
  });

  it('shows an accountant the requests with nothing to decide, and asks for leave for someone', async () => {
    const { el } = await render(LeaveRequestsPage, 'accountant');
    expect(el.querySelectorAll('td.actions, .year-part tbody button').length).toBe(0);
    expect(text(el.querySelector('.note')!)).toBe('A manager approves or rejects them.');
    expect(text(el.querySelector('.toolbar button')!)).toBe('add Request leave for someone طلب إجازة');

    const viewer = await render(LeaveRequestsPage, 'viewer');
    expect(viewer.el.querySelectorAll('button:not([aria-label*="year"])').length).toBe(0);
  });
});

describe('the leave balances', () => {
  const list = [
    balance('hassan', 'Hassan', 'annual', 'Annual', { remainingDays: 17, pendingDays: 2 }),
    balance('hassan', 'Hassan', 'sick', 'Sick', { allocatedDays: 7, usedDays: 0, remainingDays: 7, opened: false }),
    balance('mona', 'Mona', 'annual', 'Annual', { allocatedDays: 10.5, usedDays: 10.5, remainingDays: 0 }),
    balance('mona', 'Mona', 'sick', 'Sick', { allocatedDays: 7, usedDays: 1, remainingDays: 6 }),
  ];

  beforeEach(() => api.balances.mockReturnValue(of(list)));

  it('lays the year out an employee a row, a paid type a column, and a manager changes one', async () => {
    const { el, page, refresh } = await render(LeaveBalancesPage);

    expect(api.balances).toHaveBeenCalledWith(2026);
    expect([...el.querySelectorAll('thead th')].map((th) => text(th).split('ال')[0].trim())).toEqual(['Employee', 'Annual', 'Sick']);
    const rows = [...el.querySelectorAll('tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].map((td) => text(td).replace('edit', '').replace(/\s+/g, ' ').trim()))).toEqual([
      ['Hassan', '17 of 21 4 taken · 2 asked', '7 of 7 0 taken · as it opens'],
      ['Mona', '0 of 10.5 10.5 taken', '6 of 7 1 taken'],
    ]);
    expect(rows[1].querySelector('.left.none')).not.toBeNull();

    api.setBalance.mockReturnValue(of({ ...list[2], allocatedDays: 12, remainingDays: 1.5 }));
    open.mockReturnValue({ afterClosed: () => of(12) });
    await page['change'](list[2]);
    expect(open.mock.lastCall![0]).toBe(LeaveBalanceDialog);
    expect(api.setBalance).toHaveBeenCalledWith('mona', 'annual', 2026, 12);
    expect(notify.info).toHaveBeenCalledWith('Mona’s Annual: 12 days in 2026.');
    await refresh();
    expect(text(el.querySelectorAll('tbody tr')[1].querySelectorAll('td')[1])).toContain('1.5 of 12');

    page['search'].set('mon');
    await refresh();
    expect(el.querySelectorAll('tbody tr').length).toBe(1);
  });

  it('leaves the balances as they are for anyone but a manager', async () => {
    const { el } = await render(LeaveBalancesPage, 'accountant');
    expect(el.querySelectorAll('tbody button').length).toBe(0);
  });
});

describe('the leave types', () => {
  const list: LeaveType[] = [
    { id: 'annual', name: 'Annual (اعتيادي)', isPaid: true, defaultDaysPerYear: 21, proratedForNewHires: true, inUse: true },
    { id: 'sick', name: 'Sick (مرضي)', isPaid: true, defaultDaysPerYear: 7, proratedForNewHires: false, inUse: false },
    { id: 'unpaid', name: 'Unpaid (بدون راتب)', isPaid: false, defaultDaysPerYear: 0, proratedForNewHires: false, inUse: false },
  ];

  beforeEach(() => {
    api.types.mockReturnValue(of(list));
    api.saveType.mockReturnValue(of(list[0]));
    api.deleteType.mockReturnValue(of(undefined));
  });

  it('lists them, and a manager adds, changes or takes one off', async () => {
    const { el, page } = await render(LeaveTypesPage);

    expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(text))).toEqual([
      ['Annual (اعتيادي) · in use', 'Paidمدفوعة', '21', 'Their share of the year'],
      ['Sick (مرضي)', 'Paidمدفوعة', '7', 'All of it'],
      ['Unpaid (بدون راتب)', 'Unpaidبدون راتب', '—', '—'],
    ]);

    open.mockReturnValue({ afterClosed: () => of({ name: 'Maternity', isPaid: true, defaultDaysPerYear: 90, proratedForNewHires: false }) });
    await page['add']();
    expect(open.mock.lastCall![0]).toBe(LeaveTypeDialog);
    expect(open.mock.lastCall![1].data).toBeNull();
    expect(api.saveType).toHaveBeenCalledWith(expect.any(String), { name: 'Maternity', isPaid: true, defaultDaysPerYear: 90, proratedForNewHires: false });

    open.mockReturnValue({ afterClosed: () => of({ remove: true }) });
    await page['change'](list[1]);
    expect(api.deleteType).toHaveBeenCalledWith('sick');
    expect(notify.info).toHaveBeenLastCalledWith('Sick (مرضي) taken off.');
  });

  it('shows anyone but a manager the types with nothing to change', async () => {
    const { el } = await render(LeaveTypesPage, 'accountant');
    expect(el.querySelectorAll('button').length).toBe(0);
    expect(text(el.querySelector('.note')!)).toBe('A manager adds and changes them.');
  });
});
