import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest } from '../../core/costing/costing.api';
import { saveDeviceLink } from '../../core/hr/device-link';
import { AttendanceDevice, Department, DeviceCommand, DeviceSyncResult, DeviceUser, Employee, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { EmployeeDialog } from './employee-dialog';
import { EmployeeImportDialog } from './employee-import-dialog';
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
    hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20, nationalId: null, mobileNumber: null, idPhoto: null, employmentType: 'Monthly', dailyWage: 0, departmentId: 'general', jobRoleId: null, ...extra,
  };
}

const hassan = employee('hassan', 'Hassan', { deviceUserId: '7', daysOff: ['Friday', 'Saturday'] });
const mona = employee('mona', 'Mona', { jobTitle: 'Waiter', monthlySalary: 4_000, dailyRate: 133.33, hourlyRate: 14.81, shiftStartsAt: '16:00:00', shiftEndsAt: '01:00:00', daysOff: [] });
const gone = employee('gone', 'Gone Already', { isActive: false, leftOn: '2026-09-30' });
const departments: Department[] = [
  { id: 'general', name: 'General', isGeneral: true, employed: 2, everyone: 3, roles: [], monthlyCost: 0, dailyCost: 0 },
  { id: 'kitchen', name: 'Kitchen', isGeneral: false, employed: 0, everyone: 0, roles: [], monthlyCost: 0, dailyCost: 0 },
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
  const devicesOf = vi.fn();
  const readDeviceUsers = vi.fn();
  const syncDevice = vi.fn();
  const employeesTemplate = vi.fn();
  const importEmployees = vi.fn();
  const exportEmployees = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn(), attention: vi.fn() };
  const pending = signal<ApprovalRequest[]>([]);
  const approvals = {
    canApprove: signal(true),
    refresh: vi.fn(),
    requested: vi.fn(),
    pendingFor: (id: string) => pending().find((r) => r.recordId === id),
    waitingToBeMade: (module: string) => pending().filter((r) => r.module === module && r.action === 'Create'),
  };

  async function render(manage = true, view?: string) {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: HrApi,
          useValue: { employees, deviceUsers, saveEmployee, departments: () => of(departments), devices: devicesOf, readDeviceUsers, syncDevice,
            employeesTemplate, importEmployees, exportEmployees },
        },
        { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, can: (p: string) => manage || p !== Permissions.HrManage } },
        { provide: Approvals, useValue: approvals },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-20') } },
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(EmployeesPage);
    if (view) {
      fixture.componentRef.setInput('view', view);
    }
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
    devicesOf.mockReset().mockReturnValue(of([]));
    readDeviceUsers.mockReset();
    syncDevice.mockReset();
    employeesTemplate.mockReset().mockReturnValue(of(new Blob(['xlsx'])));
    importEmployees.mockReset();
    exportEmployees.mockReset().mockReturnValue(of(new Blob(['xlsx'])));
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
    notify.attention.mockReset();
    localStorage.clear();
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

  it('opens an employee’s profile from their row or their name, and not from the row’s buttons', async () => {
    const { el } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const [hassanRow, monaRow] = [...el.querySelectorAll<HTMLElement>('tbody tr')];

    expect(hassanRow.querySelector('a.name')!.getAttribute('href')).toBe('/hassan');
    (monaRow.querySelector('td') as HTMLElement).click();
    expect(navigate).toHaveBeenCalledWith(['/hr/employees', 'mona']);

    navigate.mockClear();
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    (hassanRow.querySelector('button[aria-label="Edit Hassan"]') as HTMLElement).click();
    await vi.waitFor(() => expect(open.mock.lastCall?.[0]).toBe(EmployeeDialog));
    expect(navigate).not.toHaveBeenCalled();
    navigateByUrl.mockRestore();
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

  describe('Excel', () => {
    const imported = {
      imported: [{ row: 2, id: 'new', name: 'Samir', department: 'Kitchen', jobTitle: 'Chef', requested: false }],
      skipped: [{ row: 3, name: 'Karim', reason: 'Karim is on the till already.' }],
      departmentsAdded: ['Kitchen'],
    };

    function saving() {
      const files: string[] = [];
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
        files.push(this.download);
      });
      URL.createObjectURL = vi.fn(() => 'blob:file');
      URL.revokeObjectURL = vi.fn();
      return { files, done: () => click.mockRestore() };
    }

    const picked = (file: File | null) => ({ files: file ? [file] : [], value: 'C:\\fakepath\\x.xlsx' }) as unknown as HTMLInputElement;

    it('downloads the template and exports the employees under their own names', async () => {
      const { page } = await render();
      const saved = saving();

      await page['downloadTemplate']();
      await page['exportEmployees']();
      expect(employeesTemplate).toHaveBeenCalled();
      expect(exportEmployees).toHaveBeenCalled();
      expect(saved.files).toEqual(['employees-template.xlsx', 'employees-2026-10-20.xlsx']);
      expect(page['excelBusy']()).toBe(false);
      saved.done();
    });

    it('imports the chosen sheet, shows what it did, and reads the list again', async () => {
      importEmployees.mockReturnValue(of(imported));
      const { page } = await render();
      const file = new File(['xlsx'], 'staff.xlsx');
      const input = picked(file);

      await page['importFile'](input);
      expect(importEmployees).toHaveBeenCalledWith(file);
      expect(input.value).toBe('');
      expect(open).toHaveBeenCalledWith(EmployeeImportDialog, expect.objectContaining({ data: imported }));
      expect(employees).toHaveBeenCalledTimes(2);
      expect(approvals.refresh).toHaveBeenCalled();

      importEmployees.mockReturnValue(throwError(() => new Error('That file could not be read as an Excel workbook (.xlsx).')));
      await page['importFile'](picked(new File(['?'], 'notes.xlsx')));
      expect(notify.error).toHaveBeenCalledWith(expect.anything(), 'The file could not be imported.');

      importEmployees.mockClear();
      await page['importFile'](picked(null));
      expect(importEmployees).not.toHaveBeenCalled();
    });

    it('offers whoever only sees HR the export alone', async () => {
      const { el } = await render(false);
      (el.querySelector('button[aria-label="Excel"]') as HTMLButtonElement).click();
      await new Promise((resolve) => setTimeout(resolve));
      expect([...document.querySelectorAll('.mat-mdc-menu-item')].map(text)).toEqual(['file_download Export employees · تصدير الموظفين']);
    });
  });

  describe('Sync device', () => {
    const asked: DeviceCommand = {
      id: 'c1', number: 3, kind: 'ReadUsers', text: 'DATA QUERY USERINFO', queuedBy: 'Omar', queuedAtUtc: '2026-10-21T10:00:00Z', sentAtUtc: null,
      answeredAtUtc: null, returnCode: null,
    };
    const door: AttendanceDevice = {
      id: 'd1', serialNumber: 'CQZ', name: 'Kitchen door', isApproved: true, approvedBy: 'Omar', approvedAtUtc: null, firstSeenAtUtc: '', lastSeenAtUtc: '',
      lastAddress: '192.168.1.106', commands: [], lastCalledInAtUtc: null, callingIn: false, clockAheadMinutes: null,
    };
    const synced = (extra: Partial<DeviceSyncResult> = {}): DeviceSyncResult => ({
      serialNumber: 'CQZ', deviceName: 'Kitchen door', newDevice: false, users: 3, usersNotOnTheTill: 1, punches: 40, newPunches: 12, ...extra,
    });
    const closed = new HttpErrorResponse({
      status: 422,
      error: { errors: ['192.168.1.106 answered, but nothing listens on port 4370. Its own port may be closed while it uses ADMS (Cloud Server): its punches still come that way.'] },
    });

    it('with no device known, says where to give its address', async () => {
      const { page } = await render();

      devicesOf.mockReturnValue(of([{ ...door, isApproved: false }]));
      await page['sync']();
      expect(syncDevice).not.toHaveBeenCalled();
      expect(readDeviceUsers).not.toHaveBeenCalled();
      expect(notify.info).toHaveBeenLastCalledWith(
        'No attendance device yet: give its IP address under Connection settings on the Attendance devices tab first.',
      );
    });

    it('reads the device directly, at the address last used here and where each approved one last called from', async () => {
      saveDeviceLink({ address: '192.168.1.120', port: 4370, commKey: 12345 });
      devicesOf.mockReturnValue(of([door]));
      syncDevice.mockImplementation((address: string) =>
        of(address === '192.168.1.120' ? synced({ serialNumber: 'CQZ2', deviceName: 'Bar', users: 1, newPunches: 1 }) : synced()),
      );
      const { page } = await render();
      deviceUsers.mockReturnValue(of([deviceUser('31', 'Youssef')]));

      await page['sync']();
      expect(syncDevice.mock.calls).toEqual([
        ['192.168.1.120', 4370, 12345],
        ['192.168.1.106', 4370, 0],
      ]);
      expect(readDeviceUsers).not.toHaveBeenCalled();
      expect(notify.info).toHaveBeenLastCalledWith('Synced Bar: 1 user, 1 new punch; Kitchen door: 3 users, 12 new punches.');
      expect(page['unknown']().map((u: DeviceUser) => u.deviceUserId)).toEqual(['31']);
      expect(page['syncing']()).toBe(false);
    });

    it('asks an approved device over ADMS when its own port is closed, reading the list again until it answers', async () => {
      const { page } = await render();
      vi.useFakeTimers();
      try {
        devicesOf.mockReturnValueOnce(of([door])).mockReturnValue(of([{ ...door, commands: [{ ...asked, answeredAtUtc: '2026-10-21T10:00:08Z', returnCode: 0 }] }]));
        syncDevice.mockReturnValue(throwError(() => closed));
        readDeviceUsers.mockReturnValue(of(asked));
        deviceUsers.mockReturnValue(of([deviceUser('31', 'Youssef')]));
        const syncing = page['sync']();
        await vi.advanceTimersByTimeAsync(5_000);
        await syncing;
      } finally {
        vi.useRealTimers();
      }
      expect(syncDevice).toHaveBeenCalledWith('192.168.1.106', 4370, 0);
      expect(readDeviceUsers).toHaveBeenCalledWith('d1');
      expect(notify.attention).toHaveBeenCalledWith(
        `${closed.error.errors[0]} Asked Kitchen door for its users over ADMS: they appear under Device users within seconds.`,
      );
      expect(devicesOf).toHaveBeenCalledTimes(2);
      expect(page['unknown']().map((u: DeviceUser) => u.deviceUserId)).toEqual(['31']);
    });

    it('says why when a device known only by its address cannot be read', async () => {
      saveDeviceLink({ address: '192.168.1.106', port: 4370, commKey: 0 });
      syncDevice.mockReturnValue(throwError(() => closed));
      const { page } = await render();

      await page['sync']();
      expect(readDeviceUsers).not.toHaveBeenCalled();
      expect(notify.error).toHaveBeenLastCalledWith(null, closed.error.errors[0]);
    });
  });

  it('shows flexible hours for one without a start time', async () => {
    employees.mockReturnValue(of([employee('karim', 'Karim', { shiftStartsAt: null, shiftEndsAt: null })]));
    const { rows } = await render();

    expect(rows()[0][2]).toBe('Flexible ساعات مرنة');
  });

  it('shows one list at a time, the employees first, each with its count, and keeps the one chosen in the address', async () => {
    deviceUsers.mockReturnValue(of([deviceUser('12', 'منى'), deviceUser('7', 'Hassan', { employeeId: 'hassan', employeeName: 'Hassan' })]));
    const { el, refresh } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const toggles = () => [...el.querySelectorAll('.lists mat-button-toggle')];

    expect(toggles().map(cell)).toEqual(['System employees الموظفين العاملين 2', 'Device users مسجلين على جهاز البصمة 1']);
    expect(toggles()[0].classList).toContain('mat-button-toggle-checked');
    expect(el.querySelectorAll('table.grid tbody tr').length).toBe(2);
    expect(el.querySelector('.on-device')).toBeNull();
    expect(el.querySelector('mat-slide-toggle')).not.toBeNull();

    toggles()[1].querySelector('button')!.click();
    await refresh();
    expect(navigate).toHaveBeenCalledWith([], { queryParams: { view: 'device' }, queryParamsHandling: 'merge', replaceUrl: true });
    expect(el.querySelector('.on-device')).not.toBeNull();
    expect(el.querySelectorAll('.on-device tbody tr').length).toBe(1);
    expect(el.querySelector('a.name')).toBeNull();
    // Its filters are the employees'; Sync, Excel and Add employee stay above both.
    expect(el.querySelector('mat-slide-toggle')).toBeNull();
    expect([...el.querySelectorAll('.toolbar button')].map(text)).toEqual(['table_view Excel إكسيل', 'sync Sync device مزامنة الجهاز', 'person_add Add employee إضافة موظف']);

    toggles()[0].querySelector('button')!.click();
    await refresh();
    expect(navigate).toHaveBeenLastCalledWith([], { queryParams: { view: null }, queryParamsHandling: 'merge', replaceUrl: true });
    expect(el.querySelector('.on-device')).toBeNull();
  });

  it('opens on the device’s users when the address asks for them, and says when there are none', async () => {
    const { el } = await render(true, 'device');

    expect(text(el.querySelector('.on-device p')!)).toBe(
      'No users read from the attendance device yet. Sync device reads them, as does a device’s “Read its users” on the Attendance devices tab.',
    );
    expect(el.querySelector('.on-device a')!.getAttribute('href')).toBe('/hr/devices');
    expect(el.querySelector('table.grid')).toBeNull();
  });

  it('lists the device’s users nobody has the number of, to add as employees or link to one', async () => {
    deviceUsers.mockReturnValue(of([
      deviceUser('7', 'Hassan', { employeeId: 'hassan', employeeName: 'Hassan' }),
      deviceUser('12', 'منى', { waitingPunches: 40 }),
    ]));
    const { el, page, refresh } = await render(true, 'device');

    const listed = [...el.querySelectorAll('.on-device tbody tr')];
    expect(listed.map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(cell))).toEqual([['12', 'منى', 'Kitchen door', '40']]);

    open.mockReturnValue({ afterClosed: () => of(undefined) });
    await page['addFromDevice'](deviceUser('12', 'منى'));
    expect(open.mock.lastCall![0]).toBe(EmployeeDialog);
    expect(open.mock.lastCall![1].data).toEqual({ fromDevice: { name: 'منى', deviceUserId: '12' }, departments, canApprove: true, today: '2026-10-20' });

    // Added: both lists read again, the page staying on the device's users, the one added gone
    // from them and on the employees' count.
    const added = employee('mona2', 'منى', { deviceUserId: '12' });
    open.mockReturnValue({ afterClosed: () => of(added) });
    employees.mockReturnValue(of([gone, hassan, mona, added]));
    deviceUsers.mockReturnValue(of([
      deviceUser('7', 'Hassan', { employeeId: 'hassan', employeeName: 'Hassan' }),
      deviceUser('12', 'منى', { employeeId: 'mona2', employeeName: 'منى' }),
    ]));
    await page['addFromDevice'](deviceUser('12', 'منى'));
    await refresh();
    expect(notify.info).toHaveBeenCalledWith('منى added, with number 12 on the device.');
    expect(page['list']()).toBe('device');
    expect(el.querySelectorAll('.on-device tbody tr').length).toBe(0);
    expect(text(el.querySelector('.on-device p')!)).toBe('Everyone the device has listed is an employee here. Sync device reads it again for anyone new.');
    expect([...el.querySelectorAll('.lists .count')].map(text)).toEqual(['3', '0']);

    // Linked to Mona, employed without a number: saved as she is, with it, her ID card's number and
    // her mobile kept.
    const carded = { ...mona, nationalId: '29001010123456', mobileNumber: '01012345678' };
    page['employees'].set([gone, hassan, carded]);
    open.mockReturnValue({ afterClosed: () => of('mona') });
    await page['link'](deviceUser('12', 'منى'));
    expect(open.mock.lastCall![0]).toBe(LinkDeviceUserDialog);
    expect(open.mock.lastCall![1].data.employees).toEqual([carded]);
    expect(saveEmployee.mock.lastCall![0]).toBe('mona');
    expect(saveEmployee.mock.lastCall![1]).toMatchObject({
      name: 'Mona', shiftStartsAt: '16:00:00', employmentType: 'Monthly', monthlySalary: 4_000, departmentId: 'general', deviceUserId: '12',
      nationalId: '29001010123456', mobileNumber: '01012345678',
    });
    expect(notify.info).toHaveBeenCalledWith('Mona has number 12: their punches count.');
  });

  it('offers no changes to whoever only sees HR', async () => {
    const { el } = await render(false);

    expect([...el.querySelectorAll('button')].map(text).some((t) => t.includes('Add employee'))).toBe(false);
    expect(el.querySelectorAll('tbody button').length).toBe(0);
  });
});
