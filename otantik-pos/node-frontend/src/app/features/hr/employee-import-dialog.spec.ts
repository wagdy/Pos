import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA } from '@angular/material/dialog';
import { EmployeeImportResult } from '../../core/hr/hr.api';
import { EmployeeImportDialog } from './employee-import-dialog';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('what an employees import did', () => {
  function render(data: EmployeeImportResult): HTMLElement {
    TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: data }] });
    const fixture = TestBed.createComponent(EmployeeImportDialog);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('says who was added, the departments added with them, and each row skipped and why', () => {
    const el = render({
      imported: [
        { row: 2, id: 'a', name: 'Hassan', department: 'Kitchen', jobTitle: 'Chef', requested: false },
        { row: 3, id: 'b', name: 'Mona', department: 'Kitchen', jobTitle: 'Helper', requested: false },
      ],
      skipped: [{ row: 4, name: 'Karim', reason: 'Karim is on the till already.' }],
      departmentsAdded: ['Kitchen'],
    });

    expect(text(el.querySelector('.added')!)).toBe('2 employees added. تمت الإضافة');
    expect(text(el.querySelector('.muted')!)).toBe('New departments: Kitchen.');
    expect(text(el.querySelector('h3')!)).toBe('1 row skipped صفوف لم تُستورد');
    expect([...el.querySelectorAll('tbody td')].map(text)).toEqual(['4', 'Karim', 'Karim is on the till already.']);
  });

  it('says an accountant’s wait for a manager, and when nothing was added', () => {
    const asked = render({
      imported: [{ row: 2, id: 'a', name: 'Samir', department: 'Kitchen', jobTitle: 'Chef', requested: true }],
      skipped: [],
      departmentsAdded: [],
    });
    expect(text(asked.querySelector('.added')!)).toBe('1 employee asked of a manager: each is added once approved. بانتظار موافقة المدير');
    expect(asked.querySelector('h3')).toBeNull();
    TestBed.resetTestingModule();

    const none = render({ imported: [], skipped: [{ row: 2, name: null, reason: 'No name: every row is an employee, by their name.' }], departmentsAdded: [] });
    expect(text(none.querySelector('.added')!)).toBe('No employees added. لم يُضف أي موظف.');
    expect([...none.querySelectorAll('tbody td')].map(text)).toEqual(['2', '—', 'No name: every row is an employee, by their name.']);
  });
});
