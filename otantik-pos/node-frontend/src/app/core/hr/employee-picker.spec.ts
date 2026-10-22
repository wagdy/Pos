import { TestBed } from '@angular/core/testing';
import { Employee } from './hr.api';
import { EmployeePicker, searchKey } from './employee-picker';

function employee(id: string, name: string, jobTitle: string): Employee {
  return {
    id, name, jobTitle, jobRoleId: null, employmentType: 'Monthly', monthlySalary: 5_000, dailyWage: 0, departmentId: 'general', shiftStartsAt: '10:00:00',
    shiftEndsAt: '19:00:00', daysOff: [], deviceUserId: null, hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 166.67, hourlyRate: 18.52, nationalId: null, mobileNumber: null, idPhoto: null,
  };
}

const staff = [employee('hassan', 'Hassan Ali', 'Chef'), employee('mona', 'منى سعيد', 'Waiter'), employee('karim', 'Karim', 'Delivery')];

describe('choosing an employee by typing', () => {
  function render(none = false) {
    TestBed.configureTestingModule({});
    const fixture = TestBed.createComponent(EmployeePicker);
    fixture.componentRef.setInput('employees', staff);
    fixture.componentRef.setInput('none', none);
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  it('compares names as the till’s sheets do: capitals and Arabic spellings aside', () => {
    expect(searchKey('  مـنى  ')).toBe('مني');
    expect(searchKey('HASSAN')).toBe('hassan');
    expect(searchKey('أحمد')).toBe(searchKey('احمد'));
  });

  it('narrows the list by name or job as it is typed, and shows the one chosen by name', () => {
    const { page } = render();

    page['typed']('مني');
    expect(page['shown']().map((e: Employee) => e.id)).toEqual(['mona']);
    page['typed']('chef');
    expect(page['shown']().map((e: Employee) => e.id)).toEqual(['hassan']);

    page['chose']({ option: { value: 'hassan' } });
    expect(page['value']()).toBe('hassan');
    expect(page['shown']().length).toBe(3);
    expect(page['display']('hassan')).toBe('Hassan Ali');

    // Cleared and closed without choosing: the one chosen stays, as there must be one.
    page['typed']('');
    page['settle']();
    expect(page['value']()).toBe('hassan');
  });

  it('takes no one as a choice where no one may be named, the box cleared or the choice made', () => {
    const { page } = render(true);

    page['chose']({ option: { value: 'karim' } });
    page['typed']('');
    expect(page['value']()).toBeNull();
    page['chose']({ option: { value: 'karim' } });
    page['chose']({ option: { value: null } });
    expect(page['value']()).toBeNull();
    expect(page['display'](null)).toBe('');
  });
});
