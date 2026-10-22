import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { Employee, HrApi, PendingAdvance } from '../../core/hr/hr.api';
import { DistributeAdvanceData, DistributeAdvanceDialog } from './distribute-advance-dialog';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function employee(id: string, name: string): Employee {
  return {
    id, name, jobTitle: 'Chef', jobRoleId: null, employmentType: 'Monthly', monthlySalary: 5_000, dailyWage: 0, departmentId: 'general', shiftStartsAt: '10:00:00',
    shiftEndsAt: '19:00:00', daysOff: [], deviceUserId: null, hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 166.67, hourlyRate: 18.52, nationalId: null, mobileNumber: null, idPhoto: null,
  };
}

const lump: PendingAdvance = {
  id: 'lump', date: '2026-10-22', amount: 1_000, allocated: 0, remaining: 1_000, fromTakingsOf: '2026-10-22', notes: 'Kitchen', recordedBy: 'Sara',
  shares: [], fullyAllocated: false, outsideTill: false,
};

describe('giving out advances handed out with no one named', () => {
  const distributeAdvance = vi.fn();
  const close = vi.fn();

  function render(advance = lump) {
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: { advance, employees: [employee('hassan', 'Hassan'), employee('mona', 'Mona'), employee('omar', 'Omar')] } satisfies DistributeAdvanceData },
        { provide: MatDialogRef, useValue: { close } },
        { provide: HrApi, useValue: { distributeAdvance } },
      ],
    });
    const fixture = TestBed.createComponent(DistributeAdvanceDialog);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    distributeAdvance.mockReset();
    close.mockReset();
  });

  it('splits what is left equally in whole pounds, the odd ones on the first, among those chosen', () => {
    const { fixture, el, page } = render();
    expect(text(el.querySelector('h2')!)).toBe('Give out L.E 1,000.00 توزيع السلفة');

    page['set'](0, { employeeId: 'hassan' });
    page['add']();
    page['set'](1, { employeeId: 'mona' });
    page['add']();
    page['set'](2, { employeeId: 'omar' });
    page['add']();
    page['split']();
    fixture.detectChanges();
    // The empty fourth row goes; 333 each, 334 on the first.
    expect(page['rows']().map((r: { employeeId: string; amount: number }) => [r.employeeId, r.amount])).toEqual([['hassan', 334], ['mona', 333], ['omar', 333]]);
    expect(page['total']()).toBe(1_000);
    expect(page['holdUp']()).toBeNull();
    // Whoever is on one row is not offered on another.
    expect(page['choosable'](page['rows']()[1]).map((e: Employee) => e.id)).toEqual(['mona']);
  });

  it('gives out part of it, never more than is left, and says what the till refuses', async () => {
    const { fixture, el, page } = render({ ...lump, allocated: 400, remaining: 600, shares: [] });

    page['set'](0, { employeeId: 'hassan', amount: 700 });
    fixture.detectChanges();
    expect(page['holdUp']()).toBe('The shares come to more than is left.');
    expect(el.querySelector('.total')!.classList).toContain('bad');

    page['set'](0, { amount: 250, notes: ' Grill ' });
    fixture.detectChanges();
    expect(text(el.querySelector('.total')!)).toBe('Shares L.E 250.00 of L.E 600.00 left · L.E 350.00 stays to give out later');

    distributeAdvance.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['Hassan was not employed on 22 Oct 2026, when these advances were handed out.'] } })),
    );
    await page['save']();
    expect(page['problem']()).toBe('Hassan was not employed on 22 Oct 2026, when these advances were handed out.');
    expect(close).not.toHaveBeenCalled();

    const given = { ...lump, allocated: 650, remaining: 350 };
    distributeAdvance.mockReturnValue(of(given));
    await page['save']();
    expect(distributeAdvance).toHaveBeenLastCalledWith('lump', [{ employeeId: 'hassan', amount: 250, notes: 'Grill' }]);
    expect(close).toHaveBeenCalledWith(given);
  });
});
