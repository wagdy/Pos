import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest } from '../../core/costing/costing.api';
import { Department, Employee, HrApi, PayrollFigures, PayrollSlip } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay, Treasury } from '../costing/financials';
import { EmployeeDialog } from './employee-dialog';
import { EmployeeProfilePage, IdCardDialog, RemoveIdCardDialog } from './employee-profile-page';
import { EmploymentDialog } from './employment-dialog';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

const hassan: Employee = {
  id: 'hassan', name: 'Hassan Mahmoud Ali', jobTitle: 'Chef', monthlySalary: 5_400, shiftStartsAt: '10:00:00', shiftEndsAt: '19:00:00',
  daysOff: ['Friday', 'Saturday'], deviceUserId: '7', hiredOn: '2026-01-05', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20,
  nationalId: '29001010123456', mobileNumber: '01012345678',
  idPhoto: { fileName: 'card.jpg', contentType: 'image/jpeg', bytes: 2_000, uploadedAtUtc: '2026-10-20T09:30:00Z', uploadedBy: 'Omar' },
  employmentType: 'Monthly', dailyWage: 0, departmentId: 'kitchen', jobRoleId: 'chef',
};
const departments: Department[] = [
  { id: 'general', name: 'General', isGeneral: true, employed: 0, everyone: 0, roles: [] },
  { id: 'kitchen', name: 'Kitchen', isGeneral: false, employed: 1, everyone: 1, roles: [{ id: 'chef', departmentId: 'kitchen', name: 'Chef', employed: 1 }] },
];

function slip(employee: Employee, month: string): PayrollSlip {
  const figures = {
    monthlySalary: 5_400, daysInMonth: 31, daysEmployed: 31, baseSalary: 5_400, incentives: 0, leaveAllowances: 0, overtimeMinutes: 0, overtimePay: 0,
    shortMinutes: 0, latenessPenalty: 0, absentDays: 0, absencePenalty: 0, advances: 300, broughtForward: 0, daysWorked: 18, daysOffWorked: 0,
    missingPunchDays: 0, openDays: 9, daysOffWorkedPay: 0, administrativePenaltyHours: 0, administrativePenalty: 0, holidaysWorked: 0, holidaysWorkedPay: 0,
    employmentType: 'Monthly', dailyWage: 0, gross: 5_400, deductions: 300, net: 5_100, netPayable: 5_100, carriedForward: 0, wagesCost: 5_400,
  } as PayrollFigures;
  return {
    employee, month, status: 'Draft', figures, salaryPaid: 0, salaryOwed: 0, calculatedAtUtc: null, finalizedAtUtc: null, finalizedBy: null,
    adjustments: [], advances: [], salaries: [],
  };
}

describe('an employee’s profile', () => {
  const employee = vi.fn();
  const payroll = vi.fn();
  const idPhoto = vi.fn();
  const uploadIdPhoto = vi.fn();
  const removeIdPhoto = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const pending = signal<ApprovalRequest[]>([]);
  const approvals = {
    canApprove: signal(true),
    refresh: vi.fn(),
    requested: vi.fn(),
    pendingFor: (id: string) => pending().find((r) => r.recordId === id),
  };

  async function render(manage = true) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HrApi,
          useValue: { employee, departments: () => of(departments), payroll, idPhoto, uploadIdPhoto, removeIdPhoto },
        },
        { provide: AuthService, useValue: { can: (p: string) => manage || p !== Permissions.HrManage } },
        { provide: Approvals, useValue: approvals },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: Treasury, useValue: { enter: vi.fn() } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-22') } },
      ],
    });
    const fixture = TestBed.createComponent(EmployeeProfilePage);
    fixture.componentRef.setInput('employeeId', 'hassan');
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    };
    await refresh();
    const details = () =>
      Object.fromEntries([...el.querySelectorAll('.card dl > div')].map((d) => [cell(d.querySelector('dt')!), cell(d.querySelector('dd')!)]));
    return { el, refresh, details, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    employee.mockReset().mockReturnValue(of(hassan));
    payroll.mockReset().mockImplementation((month: string) => of({ month, totals: {}, slips: [slip(hassan, month)] }));
    idPhoto.mockReset().mockReturnValue(of(new Blob(['jpeg'], { type: 'image/jpeg' })));
    uploadIdPhoto.mockReset();
    removeIdPhoto.mockReset();
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
    pending.set([]);
    approvals.requested.mockReset();
    URL.createObjectURL = vi.fn(() => 'blob:card');
    URL.revokeObjectURL = vi.fn();
  });

  it('shows who they are, their work and pay, and their ID card', async () => {
    const { el, details } = await render();

    expect(employee).toHaveBeenCalledWith('hassan');
    expect(cell(el.querySelector('.profile header')!)).toBe('HM Hassan Mahmoud Ali Chef · Kitchen Employed على رأس العمل');
    expect(details()).toEqual({
      'Full name الاسم بالكامل': 'Hassan Mahmoud Ali',
      'National ID رقم البطاقة': '29001010123456',
      'Mobile رقم الموبايل': '01012345678',
      'Started on تاريخ التعيين': '5 Jan 2026',
      'Department · role القسم والوظيفة': 'Kitchen · Chef',
      'Pay الأجر': 'L.E 5,400.00 a month · L.E 180.00 a day',
      'Shift الوردية': '10:00–19:00 · off Sat, Fri',
      'Device no. رقم البصمة': '7',
    });
    expect(el.querySelector('a[href="tel:01012345678"]')).not.toBeNull();
    expect(idPhoto).toHaveBeenCalledWith('hassan');
    expect(el.querySelector('.id-card app-paper-preview img')?.getAttribute('src')).toBe('blob:card');
    expect(text(el.querySelector('.id-card p.muted')!)).toMatch(/^Uploaded by Omar, 20 Oct 2026, \d\d:30$/);
  });

  it('says what is not entered yet, and offers to upload their ID card', async () => {
    employee.mockReturnValue(of({ ...hassan, nationalId: null, mobileNumber: null, idPhoto: null, deviceUserId: null }));
    const { el, details } = await render();

    expect(details()).toMatchObject({
      'National ID رقم البطاقة': 'Not entered',
      'Mobile رقم الموبايل': 'Not entered',
      'Device no. رقم البصمة': 'Not on the device',
    });
    expect(cell(el.querySelector('.id-card .empty')!)).toBe('badge No photo of their ID card yet.');
    expect(idPhoto).not.toHaveBeenCalled();
    expect([...el.querySelectorAll('.card-actions button')].map(text)).toEqual(['upload Upload رفع']);
  });

  it('embeds the business month’s payslip, a month before at a time, and never past this one', async () => {
    const { el, page, refresh } = await render();

    expect(payroll).toHaveBeenLastCalledWith('2026-10-01');
    expect(text(el.querySelector('app-payslip-page h2')!)).toBe('Hassan Mahmoud Ali');
    expect(cell(el.querySelector('app-payslip-page .total')!)).toBe('= Net payable صافي المستحق L.E 5,100.00');
    // In the profile, no way back to Payroll: a link to open it there.
    const links = [...el.querySelectorAll('app-payslip-page .toolbar a')].map(text);
    expect(links).toContain('open_in_new On Payroll');
    expect(links.some((l) => l.includes('مسير الرواتب'))).toBe(false);
    expect(text(el.querySelector('.pay-head .month')!)).toBe('October 2026');
    expect(el.querySelector<HTMLButtonElement>('button[aria-label="The month after"]')!.disabled).toBe(true);

    page['moveMonth'](-1);
    await refresh();
    expect(payroll).toHaveBeenLastCalledWith('2026-09-01');
    expect(text(el.querySelector('.pay-head .month')!)).toBe('September 2026');
    page['moveMonth'](1);
    page['moveMonth'](1);
    await refresh();
    expect(page['month']()).toBe('2026-10-01');
  });

  it('edits through the Employees form, and deactivates by leaving', async () => {
    const { el, page, refresh } = await render();
    expect([...el.querySelectorAll('.profile-toolbar > button')].map(text)).toEqual(['edit Edit تعديل', 'person_off Deactivate إنهاء الخدمة']);

    open.mockReturnValue({ afterClosed: () => of({ ...hassan, mobileNumber: '01198765432' }) });
    await page['edit'](hassan);
    expect(open.mock.lastCall![0]).toBe(EmployeeDialog);
    expect(open.mock.lastCall![1].data).toEqual({ employee: hassan, departments, canApprove: true, today: '2026-10-22' });
    expect(notify.info).toHaveBeenCalledWith('Hassan Mahmoud Ali saved.');
    await refresh();
    expect(text(el.querySelector('a[href^="tel:"]')!)).toBe('01198765432');

    open.mockReturnValue({ afterClosed: () => of({ ...hassan, isActive: false, leftOn: '2026-10-31' }) });
    await page['leave'](hassan);
    expect(open.mock.lastCall![0]).toBe(EmploymentDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ employee: hassan, mode: 'leave', today: '2026-10-22' });
    await refresh();
    expect(text(el.querySelector('.profile header .status')!)).toBe('Leftترك العمل');
    expect([...el.querySelectorAll('.profile-toolbar > button')].map(text)).toEqual(['edit Edit تعديل', 'person_add_alt Bring back إعادة التعيين']);
  });

  it('locks the profile while a change waits for a manager, saying what it is', async () => {
    pending.set([{ id: 'r1', module: 'Employee', action: 'Update', recordId: 'hassan', summary: 'Employee changed · Hassan Mahmoud Ali', requestedBy: 'Sara' }] as ApprovalRequest[]);
    const { el } = await render();

    expect(el.querySelectorAll('.profile-toolbar > button').length).toBe(0);
    expect(text(el.querySelector('.profile-toolbar .locked')!)).toBe('lock_clock Waiting for a manager’s approval');
    expect(text(el.querySelector('.waiting')!)).toBe('Change requestedطلب تعديل Employee changed · Hassan Mahmoud Ali · asked by Sara');
  });

  it('uploads, replaces and takes off the ID card, refusing a file the till would', async () => {
    const { el, page, refresh } = await render();
    const pick = async (file: File) => {
      const picker = el.querySelector<HTMLInputElement>('input[type="file"]')!;
      Object.defineProperty(picker, 'files', { value: [file], configurable: true });
      await page['upload'](page['employee'](), picker);
      await refresh();
    };

    await pick(new File(['gif'], 'card.gif', { type: 'image/gif' }));
    expect(notify.error).toHaveBeenLastCalledWith(null, 'Upload a photo (JPEG, PNG or WebP) or a PDF of the ID card.');
    await pick(new File([new Uint8Array(5 * 1024 * 1024 + 1)], 'card.jpg', { type: 'image/jpeg' }));
    expect(notify.error.mock.lastCall![1]).toMatch(/^The file must be under 5 MB/);
    expect(uploadIdPhoto).not.toHaveBeenCalled();

    const back = new File(['png'], 'card-back.png', { type: 'image/png' });
    const replaced = { ...hassan, idPhoto: { ...hassan.idPhoto!, fileName: 'card-back.png', contentType: 'image/png', uploadedAtUtc: '2026-10-22T10:00:00Z' } };
    uploadIdPhoto.mockReturnValue(of(replaced));
    await pick(back);
    expect(uploadIdPhoto).toHaveBeenCalledWith('hassan', back);
    expect(notify.info).toHaveBeenCalledWith('Hassan Mahmoud Ali’s ID card saved.');
    // The new one fetched and shown.
    expect(idPhoto).toHaveBeenCalledTimes(2);
    expect(text(el.querySelector('.id-card app-paper-preview .name')!)).toBe('card-back.png');

    open.mockReturnValue({ afterClosed: () => of(undefined) });
    page['enlarge'](replaced, 'card-back.png');
    expect(open.mock.lastCall![0]).toBe(IdCardDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ name: 'Hassan Mahmoud Ali', fileName: 'card-back.png' });

    // Taken off only once it is confirmed.
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    await page['removePhoto'](replaced);
    expect(open.mock.lastCall![0]).toBe(RemoveIdCardDialog);
    expect(removeIdPhoto).not.toHaveBeenCalled();
    open.mockReturnValue({ afterClosed: () => of(true) });
    removeIdPhoto.mockReturnValue(of({ ...hassan, idPhoto: null }));
    await page['removePhoto'](replaced);
    await refresh();
    expect(removeIdPhoto).toHaveBeenCalledWith('hassan');
    expect(cell(el.querySelector('.id-card .empty')!)).toBe('badge No photo of their ID card yet.');
  });

  it('says why an upload was refused', async () => {
    const { el, page } = await render();
    uploadIdPhoto.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['That file is not the photo or PDF it says it is.'] } })));
    const picker = el.querySelector<HTMLInputElement>('input[type="file"]')!;
    Object.defineProperty(picker, 'files', { value: [new File(['x'], 'card.jpg', { type: 'image/jpeg' })], configurable: true });

    await page['upload'](hassan, picker);
    expect(notify.error.mock.lastCall![1]).toBe('The ID card could not be uploaded.');
    expect(page['uploading']()).toBe(false);
  });

  it('offers no changes to whoever only sees HR, but shows the card', async () => {
    const { el } = await render(false);

    expect(el.querySelectorAll('.profile-toolbar > button').length).toBe(0);
    expect([...el.querySelectorAll('.card-actions button')].map(text)).toEqual(['zoom_in View larger']);
    expect(el.querySelector('.id-card app-paper-preview img')).not.toBeNull();
  });

  it('says when they cannot be found', async () => {
    employee.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 404 })));
    const { el } = await render();

    expect(el.querySelector('p.error')).not.toBeNull();
    expect(el.querySelector('app-payslip-page')).toBeNull();
  });
});
