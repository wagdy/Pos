import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { Department, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { DeleteDepartmentDialog, DeleteRoleDialog, DepartmentDialog, DepartmentsPage } from './departments-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

const general: Department = { id: 'general', name: 'General', isGeneral: true, employed: 1, everyone: 1, roles: [] };
const kitchen: Department = { id: 'kitchen', name: 'Kitchen', isGeneral: false, employed: 3, everyone: 4, roles: [] };
const empty: Department = { id: 'empty', name: 'Management', isGeneral: false, employed: 0, everyone: 0, roles: [] };

describe('the departments', () => {
  const departments = vi.fn();
  const saveDepartment = vi.fn();
  const deleteDepartment = vi.fn();
  const saveRole = vi.fn();
  const deleteRole = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: HrApi, useValue: { departments, saveDepartment, deleteDepartment, saveRole, deleteRole } },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
      ],
    });
    const fixture = TestBed.createComponent(DepartmentsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    departments.mockReset().mockReturnValue(of([general, kitchen, empty]));
    saveDepartment.mockReset().mockReturnValue(of(kitchen));
    deleteDepartment.mockReset().mockReturnValue(of(undefined));
    saveRole.mockReset().mockReturnValue(of({}));
    deleteRole.mockReset().mockReturnValue(of(undefined));
    open.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
  });

  it('lists the departments and how many are in each, General kept', async () => {
    const { el } = await render();

    const rows = [...el.querySelectorAll('tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].slice(1, 3).map(text))).toEqual([['1', '1'], ['3', '4'], ['0', '0']]);
    expect(text(rows[0].querySelector('td .muted')!)).toBe('Where new employees start · always there');
    expect(rows[0].querySelector('button[aria-label^="Delete"]')).toBeNull();
    expect(rows[1].querySelector('button[aria-label="Delete Kitchen"]')).not.toBeNull();
  });

  it('adds and renames one', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of('Service') });
    await page['add']();
    expect(open.mock.lastCall![0]).toBe(DepartmentDialog);
    expect(saveDepartment).toHaveBeenCalledWith(expect.any(String), 'Service');
    expect(notify.info).toHaveBeenCalledWith('Service added.');

    open.mockReturnValue({ afterClosed: () => of('Kitchen & Bar') });
    await page['rename'](kitchen);
    expect(open.mock.lastCall![1].data).toEqual({ name: 'Kitchen' });
    expect(saveDepartment).toHaveBeenLastCalledWith('kitchen', 'Kitchen & Bar');
  });

  it('lists each department’s roles, added, renamed and deleted with their holders moved', async () => {
    const chef = { id: 'chef', departmentId: 'kitchen', name: 'Chef', employed: 2 };
    const helper = { id: 'helper', departmentId: 'kitchen', name: 'Helper', employed: 1 };
    const withRoles = { ...kitchen, roles: [chef, helper] };
    departments.mockReturnValue(of([general, withRoles]));
    const { el, page } = await render();

    expect([...el.querySelectorAll('tbody tr')[1].querySelectorAll('.role')].map((r) => text(r).replace(/editclose/g, '').trim())).toEqual(['Chef · 2', 'Helper · 1']);
    expect(text(el.querySelectorAll('tbody tr')[0].querySelector('.roles')!)).toContain('No roles yet');

    open.mockReturnValue({ afterClosed: () => of('Captain') });
    await page['addRole'](withRoles);
    expect(open.mock.lastCall![1].data).toEqual({ title: 'Add a role to Kitchen', placeholder: 'Chef, helper, waiter, captain' });
    expect(saveRole).toHaveBeenCalledWith('kitchen', expect.any(String), 'Captain');

    open.mockReturnValue({ afterClosed: () => of('Head chef') });
    await page['renameRole'](withRoles, chef);
    expect(saveRole).toHaveBeenLastCalledWith('kitchen', 'chef', 'Head chef');
    expect(notify.info).toHaveBeenLastCalledWith('Renamed Head chef, for whoever holds it.');

    open.mockReturnValue({ afterClosed: () => of({ moveTo: 'chef' }) });
    await page['removeRole'](withRoles, helper);
    expect(open.mock.lastCall![0]).toBe(DeleteRoleDialog);
    expect(open.mock.lastCall![1].data).toEqual({ role: helper, department: 'Kitchen', others: [chef] });
    expect(deleteRole).toHaveBeenCalledWith('helper', 'chef');
    expect(notify.info).toHaveBeenLastCalledWith('The Helper role deleted: they are Chef now.');
  });

  it('deletes one, moving its employees to another first', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of({ moveTo: 'general' }) });
    await page['remove'](kitchen);
    expect(open.mock.lastCall![0]).toBe(DeleteDepartmentDialog);
    expect(open.mock.lastCall![1].data).toEqual({ department: kitchen, others: [general, empty] });
    expect(deleteDepartment).toHaveBeenCalledWith('kitchen', 'general');
    expect(notify.info).toHaveBeenCalledWith('Kitchen deleted: its employees are in General now.');

    open.mockReturnValue({ afterClosed: () => of({ moveTo: null }) });
    await page['remove'](empty);
    expect(deleteDepartment).toHaveBeenLastCalledWith('empty', null);
  });

  it('asks where the employees go before deleting one that has any', async () => {
    async function dialog(department: Department) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: { department, others: [general] } }] });
      const fixture = TestBed.createComponent(DeleteDepartmentDialog);
      await fixture.whenStable();
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    const full = await dialog(kitchen);
    expect(text(full.querySelector('mat-dialog-content p')!)).toBe('Its 4 employees move to another department first, those who left too.');
    expect((full.querySelector('mat-dialog-actions button:last-child') as HTMLButtonElement).disabled).toBe(true);
    const none = await dialog(empty);
    expect(text(none.querySelector('mat-dialog-content p')!)).toBe('Nobody is in it.');
    expect((none.querySelector('mat-dialog-actions button:last-child') as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows an accountant the departments, with nothing to change', async () => {
    canApprove.set(false);
    const { el } = await render();

    expect(el.querySelectorAll('button').length).toBe(0);
  });
});
