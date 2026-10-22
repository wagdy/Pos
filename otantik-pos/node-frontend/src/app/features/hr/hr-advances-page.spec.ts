import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { AdvancesReport, Employee, HrApi, PendingAdvance } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay, SafeVoucherDialog } from '../costing/financials';
import { DistributeAdvanceDialog } from './distribute-advance-dialog';
import { HrAdvancesPage } from './hr-advances-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function employee(id: string, name: string, extra: Partial<Employee> = {}): Employee {
  return {
    id, name, jobTitle: 'Chef', jobRoleId: null, employmentType: 'Monthly', monthlySalary: 5_000, dailyWage: 0, departmentId: 'general', shiftStartsAt: '10:00:00',
    shiftEndsAt: '19:00:00', daysOff: [], deviceUserId: null, hiredOn: '2026-01-01', leftOn: null, isActive: true, dailyRate: 166.67, hourlyRate: 18.52, ...extra,
  };
}

const lump: PendingAdvance = {
  id: 'lump', date: '2026-10-22', amount: 1_000, allocated: 500, remaining: 500, fromTakingsOf: '2026-10-22', notes: 'Kitchen', recordedBy: 'Sara',
  shares: [{ id: 's1', employeeId: 'hassan', employee: 'Hassan', amount: 500, payrollMonth: '2026-10-01', notes: 'Grill', allocatedBy: 'Omar', allocatedAtUtc: '2026-10-22T10:00:00Z' }],
  fullyAllocated: false, outsideTill: false,
};
const done: PendingAdvance = { ...lump, id: 'done', date: '2026-10-05', amount: 300, allocated: 300, remaining: 0, fromTakingsOf: null, notes: null, shares: [], fullyAllocated: true };
const pocket: PendingAdvance = { ...lump, id: 'pocket', date: '2026-10-21', amount: 200, allocated: 0, remaining: 200, fromTakingsOf: null, notes: 'Eid', shares: [], outsideTill: true };

const report: AdvancesReport = {
  month: '2026-10-01',
  allocated: [
    { id: 'a1', origin: 'Direct', sourceTransactionId: 'a1', employeeId: 'mona', employee: 'Mona', amount: 150, date: '2026-10-20', payrollMonth: '2026-10-01',
      fromTakingsOf: null, notes: null, recordedBy: 'Omar', outsideTill: false },
    { id: 's1', origin: 'Share', sourceTransactionId: 'lump', employeeId: 'hassan', employee: 'Hassan', amount: 500, date: '2026-10-22', payrollMonth: '2026-10-01',
      fromTakingsOf: '2026-10-22', notes: 'Grill', recordedBy: 'Omar', outsideTill: false },
  ],
  pending: [lump, done],
};

describe('HR’s advances', () => {
  const advances = vi.fn();
  const takeBackAdvanceShare = vi.fn();
  const recordHrAdvance = vi.fn();
  const voidHrAdvance = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);
  const approvals = { canApprove, refresh: vi.fn(), waitingToBeMade: vi.fn(() => []), requested: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HrApi,
          useValue: {
            advances, takeBackAdvanceShare, recordHrAdvance, voidHrAdvance,
            employees: () => of([employee('hassan', 'Hassan'), employee('mona', 'Mona'), employee('gone', 'Gone', { isActive: false, leftOn: '2026-09-30' })]),
          },
        },
        { provide: Approvals, useValue: approvals },
        { provide: AuthService, useValue: { can: () => true } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-22') } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
      ],
    });
    const fixture = TestBed.createComponent(HrAdvancesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    advances.mockReset().mockReturnValue(of(report));
    takeBackAdvanceShare.mockReset().mockReturnValue(of({ ...lump, allocated: 0, remaining: 1_000, shares: [] }));
    recordHrAdvance.mockReset();
    voidHrAdvance.mockReset().mockReturnValue(of({}));
    approvals.requested.mockReset();
    open.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
  });

  it('lists the lump sums waiting, with what is given out and what is left, and who has a share', async () => {
    const { el } = await render();

    expect(text(el.querySelector('.toolbar > .muted')!)).toBe('L.E 650.00 off this month\'s pay · L.E 500.00 to give out');
    const [waiting, allGiven] = [...el.querySelectorAll('section.lump')];
    expect(text(waiting.querySelector('header')!)).toBe(
      'L.E 1,000.00 handed out 22 Oct · from the takings of 22 Oct Kitchen · entered by SaraL.E 500.00 leftمتبقيcall_split Distribute توزيع',
    );
    expect([...waiting.querySelectorAll('tbody td')].slice(0, 3).map(text)).toEqual(['HassanGrill', 'October 2026', 'L.E 500.00']);
    expect(text(allGiven.querySelector('.status')!)).toBe('Fully allocatedتم التوزيع بالكامل');
    expect(allGiven.querySelector('header button')).toBeNull();
  });

  it('gives a lump sum out through the Distribute dialog, to those employed on its day, and takes a share back', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of({ ...lump, allocated: 1_000, remaining: 0, fullyAllocated: true }) });

    await page['distribute'](lump);
    expect(open.mock.lastCall![0]).toBe(DistributeAdvanceDialog);
    expect(open.mock.lastCall![1].data.employees.map((e: Employee) => e.id)).toEqual(['hassan', 'mona']);
    expect(notify.info).toHaveBeenCalledWith('All L.E 1,000.00 given out: each share comes off its employee\'s pay.');
    expect(advances).toHaveBeenCalledTimes(2);

    await page['takeBack'](lump.shares[0]);
    expect(takeBackAdvanceShare).toHaveBeenCalledWith('s1');
    expect(notify.info).toHaveBeenLastCalledWith('Hassan\'s L.E 500.00 is taken back, to give out again.');
  });

  it('records an advance paid outside the till in the Financials dialog, and an accountant’s goes to a manager', async () => {
    const { page } = await render();
    const voucher = {
      requestId: 'r1', transactionId: 't1', kind: 'EmployeeAdvance', amount: 250, date: '2026-10-22', source: null, revenueDate: null, supplierId: null,
      expenseItemId: null, notes: 'Rent', reason: null, includesTax: false, employeeId: 'mona',
    };
    open.mockReturnValue({ afterClosed: () => of(voucher) });
    recordHrAdvance.mockReturnValue(of({ id: 't1', amount: 250, date: '2026-10-22', employeeId: 'mona', employee: 'Mona', payrollMonth: '2026-10-01' }));

    await page['newAdvance']();
    expect(open.mock.lastCall![0]).toBe(SafeVoucherDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ kind: 'EmployeeAdvance', outsideTill: true, today: '2026-10-22' });
    expect(recordHrAdvance).toHaveBeenCalledWith({ advanceId: 't1', requestId: 'r1', amount: 250, date: '2026-10-22', employeeId: 'mona', notes: 'Rent' });
    expect(notify.info).toHaveBeenCalledWith('L.E 250.00 recorded outside the till: it comes off Mona\'s pay.');

    const asked = { outcome: 'Requested', request: { id: 'q1', action: 'Create' } };
    recordHrAdvance.mockReturnValue(of(asked));
    await page['newAdvance']();
    expect(approvals.requested).toHaveBeenCalledWith(asked);
  });

  it('says where each lump sum came from, outside the till too, and a manager voids one not given out', async () => {
    advances.mockReturnValue(of({ ...report, pending: [pocket] }));
    const { el, page } = await render();

    expect(text(el.querySelector('section.lump header')!)).toContain('L.E 200.00 handed out 21 Oct · outside the till');
    expect(el.querySelector('section.lump header button[aria-label="Void these advances"]')).not.toBeNull();
    await page['voidOutside']('pocket', 200);
    expect(voidHrAdvance).toHaveBeenCalledWith('pocket');
    expect(notify.info).toHaveBeenLastCalledWith('L.E 200.00 of advances voided.');
  });

  it('shows the month’s advances off whose pay, paid to them or a share; and nothing to change to whoever may not approve', async () => {
    canApprove.set(false);
    const { fixture, el, page } = await render();
    expect(el.querySelectorAll('section.lump button').length).toBe(0);

    page['view'].set('allocated');
    fixture.detectChanges();
    const rows = [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text));
    expect(rows.map((r) => r.slice(1, 4))).toEqual([
      ['Mona', 'Paid to themسلفة مباشرة From the safe', 'L.E 150.00'],
      ['HassanGrill', 'Share of the advances of 22 Octحصة من سلفة From the takings of 22 Oct', 'L.E 500.00'],
    ]);
    expect(text(el.querySelector('tfoot')!)).toBe("Off October's pay · PayrollL.E 650.00");
  });
});
