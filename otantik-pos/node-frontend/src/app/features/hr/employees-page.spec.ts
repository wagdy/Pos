import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest } from '../../core/costing/costing.api';
import { Department, DeviceUser, Employee, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { EmployeeDialog } from './employee-dialog';
import { EmployeesPage, LinkDeviceUserDialog } from './employees-page';
import { EmploymentDialog } from './employment-dialog';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// A cell's text node by node: the template leaves no space between its elements.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

function employee(id: string, name: string, extra: Partial<Employee> = {}): Employee {
  return {
    id, name, jobTitle: 'Chef', monthlySalary: 5_400, shiftStartsAt: '10:00:00', shiftEndsAt: '19:00:00', daysOff: ['Friday'], deviceUserId: null,
    hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20, employmentType: 'Monthly', dailyWage: 0, departmentId: 'general', ...extra,
  };
}

const hassan = employee('hassan', 'Hassan', { deviceUserId: '7', daysOff: ['Friday', 'Saturday'] });
const mona = employee('mona', 'Mona', { jobTitle: 'Waiter', monthlySalary: 4_000, dailyRate: 133.33, hourlyRate: 14.81, shiftStartsAt: '16:00:00', shiftEndsAt: '01:00:00', daysOff: [] });
const gone = employee('gone', 'Gone Already', { isActive: false, leftOn: '2026-09-30' });
const departments: Department[] = [
  { id: 'general', name: 'General', isGeneral: true, employed: 2, everyone: 3 },
  { id: 'kitchen', name: 'Kitchen', isGeneral: false, employed: 0, everyone: 0 },
];

function deviceUser(number: string, name: string, extra: Partial<DeviceUser> = {}): DeviceUser {
  return {
    id: `u${number}`, deviceSerialNumber: 'CQZ', deviceName: 'Kitchen door', deviceUserId: number, name, lastSeenAtUtc: '2026-10-20T08:00:00Z',
    employeeId: null, employeeName: null, waitingPunches: 0, ...extra,
  };
}

describe('the employees', () => {
  const employees = vi.fn();
  const deviceUsers = vi.fn();
  const saveEmployee = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const pending = signal<ApprovalRequest[]>([]);
  const approvals = {
    canApprove: signal(true),
    refresh: vi.fn(),
    requested: vi.fn(),
    pendingFor: (id: string) => pending().find((r) => r.recordId === id),
    waitingToBeMade: (module: string) => pending().filter((r) => r.module === module && r.action === 'Create'),
  };

  async function render(manage = true) {
    TestBed.configureTestingModule({
      providers: [
        { provide: HrApi, useValue: { employees, deviceUsers, saveEmployee, departments: () => of(departments) } },
        { provide: AuthService, useValue: { can: (p: string) => manage || p !== Permissions.HrManage } },
        { provide: Approvals, useValue: approvals },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-20') } },
      ],
    });
    const fixture = TestBed.createComponent(EmployeesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const rows = () => [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 6).map(cell));
    return { el, refresh, rows, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    employees.mockReset().mockReturnValue(of([gone, hassan, mona]));
    deviceUsers.mockReset().mockReturnValue(of([]));
    saveEmployee.mockReset().mockImplementation((id: string) => of({ ...mona, id }));
    open.mockReset();
    notify.info.mockReset();
    pending.set([]);
    approvals.requested.mockReset();
  });

  it('lists who is employed, their salary, shift, days off and device number, and who has left when asked', async () => {
    const { el, page, rows, refresh } = await render();

    expect(text(el.querySelector('.toolbar .muted')!)).toBe('2 employed · L.E 9,400.00 a month');
    expect(rows()).toEqual([
      // The restaurant's week starts on Saturday.
      ['Hassan Chef · General', 'L.E 5,400.00 L.E 180.00 a day · L.E 20.00 an hour', '10:00–19:00', 'Sat, Fri', '7', 'Employed على رأس العمل since 1 Jan 2026'],
      ['Mona Waiter · General', 'L.E 4,000.00 L.E 133.33 a day · L.E 14.81 an hour', '16:00–01:00', 'None', '—', 'Employed على رأس العمل since 1 Jan 2026'],
    ]);

    page['showLeft'].set(true);
    await refresh();
    expect(rows()[0]).toEqual(['Gone Already Chef · General', 'L.E 5,400.00 L.E 180.00 a day · L.E 20.00 an hour', '10:00–19:00', 'Fri', '—', 'Left ترك العمل last day 30 Sep 2026']);
  });

  it('lists a new one an accountant asked for, and locks one whose change waits', async () => {
    pending.set([
      { id: 'r1', module: 'Employee', action: 'Create', recordId: 'new', summary: 'New employee · Karim · Waiter · L.E 4,000.00 a month, from 12:00', requestedBy: 'Sara' },
      { id: 'r2', module: 'Employee', action: 'Update', recordId: 'mona', summary: 'Employee changed · Mona', requestedBy: 'Sara' },
    ] as ApprovalRequest[]);
    const { el } = await render();

    expect(text(el.querySelector('.waiting li')!)).toBe(
      'Waiting for approvalبانتظار الموافقة New employee · Karim · Waiter · L.E 4,000.00 a month, from 12:00 · asked by Sara',
    );
    const monaRow = [...el.querySelectorAll('tbody tr')][1];
    expect(text(monaRow.querySelector('.pending-badge')!)).toBe('Change requestedطلب تعديل');
    expect(monaRow.querySelector('mat-icon.locked')).not.toBeNull();
    expect(monaRow.querySelector('button')).toBeNull();
  });

  it('adds, edits and lets go through the dialogs, and says when it went to a manager', async () => {
    const { el, page } = await render();
    open.mockReturnValue({ afterClosed: () => of({ ...hassan, name: 'Hassan' }) });

    await page['edit'](hassan);
    expect(open.mock.lastCall![0]).toBe(EmployeeDialog);
    expect(open.mock.lastCall![1].data).toEqual({ employee: hassan, departments, canApprove: true, today: '2026-10-20' });
    expect(notify.info).toHaveBeenCalledWith('Hassan saved.');

    open.mockReturnValue({ afterClosed: () => of({ outcome: 'Requested', request: { id: 'r', action: 'Update' } }) });
    await page['leave'](mona);
    expect(open.mock.lastCall![0]).toBe(EmploymentDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ employee: mona, mode: 'leave' });
    expect(approvals.requested).toHaveBeenCalled();

    expect([...el.querySelectorAll('button')].map(text)).toContain('person_add Add employee إضافة موظف');
  });

  it('shows a daily worker’s wage, and one department’s employees when chosen', async () => {
    const samir = employee('samir', 'Samir', { employmentType: 'Daily', monthlySalary: 0, dailyWage: 270, dailyRate: 270, hourlyRate: 30, departmentId: 'kitchen' });
    employees.mockReturnValue(of([hassan, samir]));
    const { el, page, rows, refresh } = await render();

    expect(rows()[1].slice(0, 2)).toEqual(['Samir Chef · Kitchen', 'L.E 270.00 a day Daily wage · L.E 30.00 an hour']);
    expect(text(el.querySelector('.toolbar > .muted')!)).toBe('2 employed · L.E 5,400.00 a month · 1 on daily wages');

    page['department'].set('kitchen');
    await refresh();
    expect(rows().map((r) => r[0])).toEqual(['Samir Chef · Kitchen']);
  });

  it('shows flexible hours for one without a start time', async () => {
    employees.mockReturnValue(of([employee('karim', 'Karim', { shiftStartsAt: null, shiftEndsAt: null })]));
    const { rows } = await render();

    expect(rows()[0][2]).toBe('Flexible ساعات مرنة');
  });

  it('lists the device’s users nobody has the number of, to add as employees or link to one', async () => {
    deviceUsers.mockReturnValue(of([
      deviceUser('7', 'Hassan', { employeeId: 'hassan', employeeName: 'Hassan' }),
      deviceUser('12', 'منى', { waitingPunches: 40 }),
    ]));
    const { el, page } = await render();

    const listed = [...el.querySelectorAll('.on-device tbody tr')];
    expect(listed.map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(cell))).toEqual([['12', 'منى', 'Kitchen door', '40']]);

    open.mockReturnValue({ afterClosed: () => of(undefined) });
    await page['addFromDevice'](deviceUser('12', 'منى'));
    expect(open.mock.lastCall![0]).toBe(EmployeeDialog);
    expect(open.mock.lastCall![1].data).toEqual({ fromDevice: { name: 'منى', deviceUserId: '12' }, departments, canApprove: true, today: '2026-10-20' });

    // Linked to Mona, employed without a number: saved as she is, with it.
    open.mockReturnValue({ afterClosed: () => of('mona') });
    await page['link'](deviceUser('12', 'منى'));
    expect(open.mock.lastCall![0]).toBe(LinkDeviceUserDialog);
    expect(open.mock.lastCall![1].data.employees).toEqual([mona]);
    expect(saveEmployee.mock.lastCall![0]).toBe('mona');
    expect(saveEmployee.mock.lastCall![1]).toMatchObject({
      name: 'Mona', shiftStartsAt: '16:00:00', employmentType: 'Monthly', monthlySalary: 4_000, departmentId: 'general', deviceUserId: '12',
    });
    expect(notify.info).toHaveBeenCalledWith('Mona has number 12: their punches count.');
  });

  it('offers no changes to whoever only sees HR', async () => {
    const { el } = await render(false);

    expect([...el.querySelectorAll('button')].map(text).some((t) => t.includes('Add employee'))).toBe(false);
    expect(el.querySelectorAll('tbody button').length).toBe(0);
  });
});
