import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { EmployeeHrAccess, HrApi, StaffHrAccess } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { PermissionsPage, TabsDialog, TabsDialogData } from './permissions-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

const hassan: EmployeeHrAccess = { id: 'hassan', name: 'Hassan', jobTitle: 'Supervisor', department: 'Kitchen', hasMobile: true, hasPin: true, tabs: ['Now', 'Attendance'] };
const kemo: EmployeeHrAccess = { id: 'kemo', name: 'Kemo', jobTitle: 'Chef', department: 'Kitchen', hasMobile: false, hasPin: false, tabs: [] };
const mona: StaffHrAccess = { id: 'staff-accountant', name: 'Mona', role: 'Accountant', restricted: false, tabs: ['Now', 'Employees', 'Departments', 'Attendance', 'Corrections', 'Payroll', 'Advances', 'Holidays', 'Devices'], setBy: null, setAtUtc: null };

describe('employee permissions', () => {
  const access = vi.fn();
  const setEmployeeTabs = vi.fn();
  const setStaffTabs = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HrApi, useValue: { access, setEmployeeTabs, setStaffTabs } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PermissionsPage);
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    };
    await refresh();
    const rows = (table: string) => [...el.querySelectorAll(`table.${table} tbody tr`)].map((r) => [...r.querySelectorAll('td')].slice(0, 3).map(cell));
    return { el, refresh, rows, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    access.mockReset().mockReturnValue(of({ employees: [hassan, kemo], staff: [mona] }));
    setEmployeeTabs.mockReset();
    setStaffTabs.mockReset();
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('lists everyone employed with their tabs and whether they can sign in, and the accountants with every tab until ticked', async () => {
    const { rows, page, refresh } = await render();
    expect(rows('employees')).toEqual([
      ['Hassan Supervisor · Kitchen', 'Now Attendance', 'Can sign in'],
      ['Kemo Chef · Kitchen', 'None', 'No mobile number'],
    ]);
    expect(rows('staff')).toEqual([['Mona', 'Every tab, until you tick theirs', '—']]);

    page['query'].set('kem');
    await refresh();
    expect(rows('employees').map((r) => r[0])).toEqual(['Kemo Chef · Kitchen']);
    expect(rows('staff')).toEqual([['No accountant is called that.']]);
  });

  it('gives an employee the tabs ticked, and an accountant only theirs', async () => {
    const { rows, page, refresh } = await render();

    setEmployeeTabs.mockReturnValue(of({ ...hassan, tabs: ['Now', 'Attendance', 'Payroll'] }));
    open.mockReturnValue({ afterClosed: () => of(['Now', 'Attendance', 'Payroll']) });
    await page['changeEmployee'](hassan);
    expect(open.mock.lastCall![0]).toBe(TabsDialog);
    expect(open.mock.lastCall![1].data).toEqual({ name: 'Hassan', tabs: ['Now', 'Attendance'], employee: { hasMobile: true, hasPin: true } });
    expect(setEmployeeTabs).toHaveBeenCalledWith('hassan', ['Now', 'Attendance', 'Payroll']);
    expect(notify.info).toHaveBeenLastCalledWith('Hassan’s HR tabs: Now, Attendance, Payroll.');
    await refresh();
    expect(rows('employees')[0][1]).toBe('Now Attendance Payroll');

    setStaffTabs.mockReturnValue(of({ ...mona, restricted: true, tabs: ['Payroll', 'Advances'], setBy: 'Omar', setAtUtc: '2026-10-25T18:00:00Z' }));
    open.mockReturnValue({ afterClosed: () => of(['Payroll', 'Advances']) });
    await page['changeStaff'](mona);
    expect(setStaffTabs).toHaveBeenCalledWith('staff-accountant', ['Payroll', 'Advances']);
    await refresh();
    expect(rows('staff')).toEqual([['Mona', 'Payroll Advances', 'Omar, 25 Oct 2026']]);

    // Cancelled: nothing sent. Refused: said.
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    await page['changeEmployee'](kemo);
    expect(setEmployeeTabs).toHaveBeenCalledTimes(1);
    setEmployeeTabs.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 422, error: { detail: 'Kemo has left: bring them back before giving them HR tabs.' } })));
    open.mockReturnValue({ afterClosed: () => of(['Now']) });
    await page['changeEmployee'](kemo);
    expect(notify.error).toHaveBeenLastCalledWith('Kemo has left: bring them back before giving them HR tabs.');
  });
});

describe('the tabs dialog', () => {
  function render(data: TabsDialogData) {
    TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: data }] });
    const fixture = TestBed.createComponent(TabsDialog);
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  it('ticks tabs one by one or all at once, and closes with them in the order shown', () => {
    const { page, el } = render({ name: 'Hassan', tabs: ['Attendance'], employee: { hasMobile: true, hasPin: true } });
    expect(text(el.querySelector('h2')!)).toBe('Hassan’s HR tabs الصلاحيات');
    expect([...el.querySelectorAll('.tabs mat-checkbox')].map((c) => c.getAttribute('data-tab'))).toEqual([
      'Now', 'Employees', 'Departments', 'Attendance', 'Corrections', 'Payroll', 'Advances', 'Holidays', 'Devices', 'Custody',
    ]);
    expect(el.querySelector('.warn')).toBeNull();
    expect(page['some']()).toBe(true);

    page['tick']('Now', true);
    expect(page['picked']()).toEqual(['Now', 'Attendance']);
    page['all'](true);
    expect(page['every']()).toBe(true);
    page['all'](false);
    expect(page['picked']()).toEqual([]);
  });

  it('says an employee needs a mobile number and a PIN to sign in to use them', () => {
    const { el } = render({ name: 'Kemo', tabs: [], employee: { hasMobile: false, hasPin: false } });
    expect(text(el.querySelector('.warn')!)).toBe('They need a mobile number and a PIN to sign in to the portal: set them on their profile.');
    TestBed.resetTestingModule();
    const accountant = render({ name: 'Mona', tabs: [] });
    expect(accountant.el.querySelector('.warn')).toBeNull();
  });
});
