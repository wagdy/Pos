import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest } from '../../core/costing/costing.api';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { EmployeeDialog } from './employee-dialog';
import { EmployeesPage } from './employees-page';
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
    hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20, ...extra,
  };
}

const hassan = employee('hassan', 'Hassan', { deviceUserId: '7', daysOff: ['Friday', 'Saturday'] });
const mona = employee('mona', 'Mona', { jobTitle: 'Waiter', monthlySalary: 4_000, dailyRate: 133.33, hourlyRate: 14.81, shiftStartsAt: '16:00:00', shiftEndsAt: '01:00:00', daysOff: [] });
const gone = employee('gone', 'Gone Already', { isActive: false, leftOn: '2026-09-30' });

describe('the employees', () => {
  const employees = vi.fn();
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
        { provide: HrApi, useValue: { employees } },
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
      ['Hassan Chef', 'L.E 5,400.00 L.E 180.00 a day · L.E 20.00 an hour', '10:00–19:00', 'Sat, Fri', '7', 'Employed على رأس العمل since 1 Jan 2026'],
      ['Mona Waiter', 'L.E 4,000.00 L.E 133.33 a day · L.E 14.81 an hour', '16:00–01:00', 'None', '—', 'Employed على رأس العمل since 1 Jan 2026'],
    ]);

    page['showLeft'].set(true);
    await refresh();
    expect(rows()[0]).toEqual(['Gone Already Chef', 'L.E 5,400.00 L.E 180.00 a day · L.E 20.00 an hour', '10:00–19:00', 'Fri', '—', 'Left ترك العمل last day 30 Sep 2026']);
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
    expect(open.mock.lastCall![1].data).toEqual({ employee: hassan, canApprove: true, today: '2026-10-20' });
    expect(notify.info).toHaveBeenCalledWith('Hassan saved.');

    open.mockReturnValue({ afterClosed: () => of({ outcome: 'Requested', request: { id: 'r', action: 'Update' } }) });
    await page['leave'](mona);
    expect(open.mock.lastCall![0]).toBe(EmploymentDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ employee: mona, mode: 'leave' });
    expect(approvals.requested).toHaveBeenCalled();

    expect([...el.querySelectorAll('button')].map(text)).toContain('person_add Add employee إضافة موظف');
  });

  it('offers no changes to whoever only sees HR', async () => {
    const { el } = await render(false);

    expect([...el.querySelectorAll('button')].map(text).some((t) => t.includes('Add employee'))).toBe(false);
    expect(el.querySelectorAll('tbody button').length).toBe(0);
  });
});
