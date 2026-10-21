import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { Employee, HrApi, PayrollFigures, PayrollMonth, PayrollSlip } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { Treasury } from '../costing/financials';
import { AdjustmentDialog, FinalizeDialog, PayslipPage } from './payslip-page';
import { PayrollPage } from './payroll-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

const hassan: Employee = {
  id: 'hassan', name: 'Hassan', jobTitle: 'Chef', monthlySalary: 5_400, shiftStartsAt: '16:00:00', shiftEndsAt: '01:00:00', daysOff: ['Friday'],
  deviceUserId: '7', hiredOn: '2026-09-01', leftOn: null, isActive: true, dailyRate: 180, hourlyRate: 20,
};
const mona: Employee = { ...hassan, id: 'mona', name: 'Mona', jobTitle: 'Waiter', monthlySalary: 4_050, dailyRate: 135, hourlyRate: 15 };

function figures(extra: Partial<PayrollFigures> = {}): PayrollFigures {
  const f = {
    monthlySalary: 5_400, daysInMonth: 30, daysEmployed: 30, baseSalary: 5_400, incentives: 500, leaveAllowances: 0, overtimeMinutes: 120, overtimePay: 40,
    shortMinutes: 101, latenessPenalty: 33.67, absentDays: 1, absencePenalty: 180, advances: 200, broughtForward: 0, daysWorked: 25, daysOffWorked: 0,
    missingPunchDays: 0, openDays: 0, daysOffWorkedPay: 0, administrativePenaltyHours: 0, administrativePenalty: 0, holidaysWorked: 0, holidaysWorkedPay: 0,
    ...extra,
  };
  const gross = f.baseSalary + f.incentives + f.leaveAllowances + f.daysOffWorkedPay + f.holidaysWorkedPay + f.overtimePay;
  const deductions = f.advances + f.broughtForward + f.latenessPenalty + f.absencePenalty + f.administrativePenalty;
  const net = Math.round((gross - deductions) * 100) / 100;
  return {
    ...f, gross, deductions, net, netPayable: Math.max(0, net), carriedForward: Math.max(0, -net),
    wagesCost: Math.round((gross - f.latenessPenalty - f.absencePenalty - f.administrativePenalty) * 100) / 100,
  };
}

function slip(employee: Employee, extra: Partial<PayrollSlip> = {}, f: Partial<PayrollFigures> = {}): PayrollSlip {
  return {
    employee, month: '2026-09-01', status: null, figures: figures(f), salaryPaid: 0, salaryOwed: 0, calculatedAtUtc: null, finalizedAtUtc: null,
    finalizedBy: null, adjustments: [], advances: [], salaries: [], ...extra,
  };
}

function month(slips: PayrollSlip[]): PayrollMonth {
  const sum = (of: (f: PayrollFigures) => number) => slips.reduce((s, x) => s + of(x.figures), 0);
  return {
    month: '2026-09-01',
    totals: {
      baseSalaries: sum((f) => f.baseSalary), incentives: sum((f) => f.incentives), leaveAllowances: sum((f) => f.leaveAllowances),
      overtimePay: sum((f) => f.overtimePay), advances: sum((f) => f.advances), broughtForward: sum((f) => f.broughtForward),
      latenessPenalties: sum((f) => f.latenessPenalty), absencePenalties: sum((f) => f.absencePenalty), netPayable: sum((f) => f.netPayable),
      carriedForward: sum((f) => f.carriedForward), wagesCost: sum((f) => f.wagesCost), salaryPaid: slips.reduce((s, x) => s + x.salaryPaid, 0),
      daysOffWorkedPay: sum((f) => f.daysOffWorkedPay),
      holidaysWorkedPay: sum((f) => f.holidaysWorkedPay),
    },
    slips,
  };
}

describe('the monthly payroll', () => {
  const payroll = vi.fn();
  const calculatePayroll = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render(manage = true) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HrApi, useValue: { payroll, calculatePayroll } },
        { provide: AuthService, useValue: { can: (p: string) => manage || p !== Permissions.HrManage } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PayrollPage);
    fixture.componentRef.setInput('month', '2026-09-20');
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    payroll.mockReset().mockReturnValue(of(month([
      slip(hassan, { status: 'Finalized', salaryOwed: 5_000, salaryPaid: 1_006.33 }, { administrativePenaltyHours: 2, administrativePenalty: 40 }),
      slip(mona, { status: null }, { monthlySalary: 4_050, daysEmployed: 16, baseSalary: 2_160, incentives: 0, overtimePay: 0, latenessPenalty: 0, absencePenalty: 0, advances: 3_000, missingPunchDays: 2 }),
    ])));
    calculatePayroll.mockReset().mockImplementation(() => payroll());
  });

  it('breaks each slip into the formula’s parts, column by column, with the month’s totals', async () => {
    const { el } = await render();

    expect(payroll).toHaveBeenCalledWith('2026-09-01');
    const rows = [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(cell));
    expect(rows[0]).toEqual([
      'Hassan Chef', 'L.E 5,400.00', 'L.E 500.00', 'L.E 0.00', 'L.E 40.00', 'L.E 200.00', 'L.E 33.67', 'L.E 180.00', 'L.E 40.00', 'L.E 0.00',
      'L.E 5,486.33', 'Finalized نهائي L.E 5,000.00 to pay',
    ]);
    // More owed than paid: nothing is paid, and the rest goes to next month.
    expect(rows[1][0]).toBe('Mona Waiter · 16 of 30 days');
    expect(rows[1][10]).toBe('L.E 0.00 L.E 840.00 to next month');
    expect(rows[1][11]).toBe('Not calculated لم يُحتسب 2 days suspended');
    expect(text(el.querySelector('tfoot tr')!)).toContain('L.E 5,486.33');
    expect(el.querySelector('tbody a')!.getAttribute('href')).toBe('/hassan?month=2026-09-01');
  });

  it('counts the days off and holidays worked with the leave allowances entered', async () => {
    payroll.mockReturnValue(of(month([slip(hassan, {}, { leaveAllowances: 100, daysOffWorked: 2, daysOffWorkedPay: 360, holidaysWorked: 1, holidaysWorkedPay: 180 })])));
    const { el } = await render();

    const leave = el.querySelectorAll('tbody tr')[0].querySelectorAll('td')[3];
    expect(cell(leave)).toBe('L.E 640.00 2 days off worked 1 holiday worked');
    expect(cell(el.querySelectorAll('tfoot td')[3])).toBe('L.E 640.00');
  });

  it('saves the drafts for whoever manages HR, and offers no calculating to anyone else', async () => {
    const { el, page } = await render();
    await page['calculate']();
    expect(calculatePayroll).toHaveBeenCalledWith('2026-09-01');
    expect(notify.info).toHaveBeenCalledWith('The drafts are saved as they stand now.');
    TestBed.resetTestingModule();

    const viewer = await render(false);
    expect([...viewer.el.querySelectorAll('button')].map(text).some((t) => t.includes('Calculate drafts'))).toBe(false);
    expect(el).toBeTruthy();
  });
});

describe('a payslip', () => {
  const payroll = vi.fn();
  const finalizeSlip = vi.fn();
  const addAdjustment = vi.fn();
  const voidAdjustment = vi.fn();
  const open = vi.fn();
  const enter = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HrApi, useValue: { payroll, finalizeSlip, addAdjustment, voidAdjustment } },
        { provide: MatDialog, useValue: { open } },
        { provide: Treasury, useValue: { enter } },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PayslipPage);
    fixture.componentRef.setInput('employeeId', 'hassan');
    fixture.componentRef.setInput('month', '2026-09-01');
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  const draft = slip(hassan, {
    status: 'Draft',
    adjustments: [{ id: 'a1', employeeId: 'hassan', month: '2026-09-01', kind: 'Incentive', amount: 500, reason: 'Best month', source: 'Manual', forDay: null, recordedBy: 'Omar', recordedAtUtc: '', voidedAtUtc: null, voidedBy: null }],
    advances: [{ id: 't1', kind: 'EmployeeAdvance', date: '2026-09-10', amount: 200, fromTakingsOf: null, notes: null, recordedBy: 'Omar', approvedBy: null, voidedAtUtc: null, voidedBy: null }],
  });

  beforeEach(() => {
    payroll.mockReset().mockReturnValue(of(month([draft, slip(mona)])));
    finalizeSlip.mockReset().mockReturnValue(of({}));
    addAdjustment.mockReset().mockReturnValue(of({}));
    voidAdjustment.mockReset().mockReturnValue(of({}));
    open.mockReset();
    enter.mockReset().mockResolvedValue(true);
    canApprove.set(true);
  });

  it('shows the formula line by line, from the base salary to the net payable', async () => {
    const { el } = await render();

    const lines = [...el.querySelectorAll('.breakdown tr')].map((r) => [text(r.querySelector('th')!).split(/\s(?=[؀-ۿ])/)[0], text(r.querySelector('td')!)]);
    expect(lines).toEqual([
      ['Base salary', 'L.E 5,400.00'],
      ['+ Incentives', 'L.E 500.00'],
      ['+ Days off worked', 'L.E 0.00'],
      ['+ Leave allowances', 'L.E 0.00'],
      ['+ Overtime', 'L.E 40.00'],
      ['= Gross pay', 'L.E 5,940.00'],
      ['− Advances', 'L.E 200.00'],
      ['− Lateness', 'L.E 33.67'],
      ['− Absence', 'L.E 180.00'],
      ['= Net payable', 'L.E 5,526.33'],
    ]);
    expect(text(el.querySelectorAll('.breakdown tr')[4].querySelector('.how')!)).toBe('2h 00m at L.E 20.00 an hour');
    expect(text(el.querySelectorAll('.breakdown tr')[8].querySelector('.how')!)).toBe('1 day at L.E 180.00');
    expect([...el.querySelectorAll('.lists .line')].map(cell)).toEqual(['Incentive حوافز · Best month L.E 500.00 delete', '10 Sep · from the safe L.E 200.00']);
  });

  it('shows the holidays worked as their own line, when there are any', async () => {
    payroll.mockReturnValue(of(month([slip(hassan, { status: 'Draft' }, { holidaysWorked: 2, holidaysWorkedPay: 360 })])));
    const { el } = await render();

    const line = [...el.querySelectorAll('.breakdown tr')].find((r) => text(r.querySelector('th')!).startsWith('+ Holidays worked'))!;
    expect(text(line.querySelector('.how')!)).toBe("2 holidays: a day's pay for half a shift or more, the hours for less");
    expect(text(line.querySelector('td')!)).toBe('L.E 360.00');
    expect(text(el.querySelector('.breakdown tr.total td')!)).toBe('L.E 5,886.33');
  });

  it('shows a manager’s administrative penalties as their own deduction', async () => {
    payroll.mockReturnValue(of(month([slip(hassan, { status: 'Draft' }, { administrativePenaltyHours: 2.5, administrativePenalty: 50 })])));
    const { el } = await render();

    const line = [...el.querySelectorAll('.breakdown tr')].find((r) => text(r.querySelector('th')!).startsWith('− Administrative penalties'))!;
    expect(text(line.querySelector('.how')!)).toBe('2.5 hours at L.E 20.00');
    expect(text(line.querySelector('td')!)).toBe('L.E 50.00');
    expect(text(el.querySelector('.breakdown tr.total td')!)).toBe('L.E 5,476.33');
  });

  it('points to Corrections when suspended days keep the month from being finalized', async () => {
    payroll.mockReturnValue(of(month([slip(hassan, { status: 'Draft' }, { missingPunchDays: 1 })])));
    const { el } = await render();

    expect(el.querySelector('p.note a')!.getAttribute('href')).toBe('/hr/corrections');
    expect(text(el.querySelector('p.note')!)).toContain('1 day is suspended');
  });

  it('is finalized by a manager once the month is over and every day is settled, after asking', async () => {
    const { page } = await render();

    expect(page['finalizeBlocked'](slip(hassan, {}, { openDays: 10 }))).toBe('The month is not over yet: a payslip is finalized once all its days are.');
    expect(page['finalizeBlocked'](slip(hassan, {}, { missingPunchDays: 2 }))).toBe(
      '2 days are suspended, a punch-in with no punch-out: enter the punch-out, or ignore the stray punch, on Corrections first.',
    );
    expect(page['finalizeBlocked'](draft)).toBeNull();

    open.mockReturnValue({ afterClosed: () => of(true) });
    await page['finalize'](draft);
    expect(open.mock.lastCall![0]).toBe(FinalizeDialog);
    expect(finalizeSlip).toHaveBeenCalledWith('hassan', '2026-09-01');
    expect(notify.info).toHaveBeenCalledWith('Hassan’s pay is finalized.');
  });

  it('takes incentives and allowances, voids them, and pays what is owed from the safe entry dialog', async () => {
    const { page } = await render();

    open.mockReturnValue({ afterClosed: () => of({ kind: 'LeaveAllowance', amount: 180, reason: 'Worked 6 Oct' }) });
    await page['addAdjustment'](draft);
    expect(open.mock.lastCall![0]).toBe(AdjustmentDialog);
    expect(addAdjustment).toHaveBeenCalledWith('hassan', '2026-09-01', 'LeaveAllowance', 180, 'Worked 6 Oct');
    await page['voidAdjustment'](draft.adjustments[0]);
    expect(voidAdjustment).toHaveBeenCalledWith('a1');

    await page['pay'](slip(hassan, { status: 'Finalized', salaryOwed: 5_526.33 }));
    expect(enter).toHaveBeenCalledWith('SalaryPayment', { employeeId: 'hassan', payrollMonth: '2026-09-01', amount: 5_526.33 });
  });

  it('offers an accountant nothing to finalize or add', async () => {
    canApprove.set(false);
    const { el } = await render();

    expect([...el.querySelectorAll('.actions button')].length).toBe(0);
    expect(el.querySelectorAll('.lists button').length).toBe(0);
  });
});
