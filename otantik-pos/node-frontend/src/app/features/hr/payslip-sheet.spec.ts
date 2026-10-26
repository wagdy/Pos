import { TestBed } from '@angular/core/testing';
import { PayrollFigures, PayrollSlip } from '../../core/hr/hr.api';
import { PayslipSheet } from './payslip-sheet';

const text = (el: Node) => el.textContent!.replace(/\s+/g, ' ').trim();

function slip(figures: Partial<PayrollFigures>, over: Partial<PayrollSlip> = {}): PayrollSlip {
  return {
    employee: { id: 'hassan', name: 'Hassan', jobTitle: 'Chef', hourlyRate: 20, dailyRate: 180 },
    month: '2026-10-01',
    status: 'Draft',
    figures: {
      monthlySalary: 5_400, daysInMonth: 31, daysEmployed: 31, baseSalary: 5_400, incentives: 0, leaveAllowances: 0, overtimeMinutes: 0, overtimePay: 0,
      shortMinutes: 0, latenessPenalty: 0, absentDays: 0, absencePenalty: 0, advances: 0, broughtForward: 0, daysWorked: 20, daysOffWorked: 0,
      missingPunchDays: 0, openDays: 0, daysOffWorkedPay: 0, administrativePenaltyHours: 0, administrativePenalty: 0, holidaysWorked: 0, holidaysWorkedPay: 0,
      employmentType: 'Monthly', dailyWage: 0, gross: 5_400, deductions: 0, net: 5_400, netPayable: 5_400, carriedForward: 0, wagesCost: 5_400,
      ...figures,
    },
    salaryPaid: 0, salaryOwed: 0, calculatedAtUtc: null, finalizedAtUtc: null, finalizedBy: null, adjustments: [], advances: [], salaries: [],
    ...over,
  } as unknown as PayrollSlip;
}

function render(s: PayrollSlip) {
  const fixture = TestBed.createComponent(PayslipSheet);
  fixture.componentRef.setInput('slip', s);
  fixture.componentRef.setInput('month', '2026-10-01');
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  // A line of the formula as it reads: its name, how it is made, and its amount.
  const rows = [...el.querySelectorAll('.breakdown tr')].map((r) =>
    [text(r.querySelector('th')!.firstChild!), ...[...r.querySelectorAll('th .how')].map(text), text(r.querySelector('td')!)].join(' | '),
  );
  return { el, rows };
}

describe('a payslip’s deductions', () => {
  it('takes unpaid leave off day by day, apart from absence, and the money penalties of disciplinary actions apart from the hours', () => {
    const { el, rows } = render(
      slip(
        {
          unpaidLeaveDays: 2, unpaidLeaveDeduction: 360, administrativePenaltyHours: 2, administrativePenalty: 290, administrativePenaltyAmounts: 250,
          netPayable: 4_750,
        },
        {
          unpaidLeaveDays: ['2026-10-14', '2026-10-15'],
          penalties: [
            { date: '2026-10-05', hours: 2, amount: null, reason: 'Left the walk-in open', disciplinaryActionId: null },
            { date: '2026-10-05', hours: 0, amount: 200, reason: 'Penalty deduction: Absence', disciplinaryActionId: 'a1' },
            { date: '2026-10-20', hours: 0, amount: 50, reason: 'Penalty deduction: Hygiene', disciplinaryActionId: 'a2' },
          ],
        },
      ),
    );

    expect(rows).toContain('− Unpaid leave | 2 days at L.E 180.00, approved leave not paid | 14 Oct, 15 Oct | L.E 360.00');
    expect(rows).toContain('− Administrative penalties | 2 hours at L.E 20.00 | L.E 40.00');
    expect(rows).toContain('− Disciplinary penalties | 5 Oct · Penalty deduction: AbsenceL.E 200.00 | 20 Oct · Penalty deduction: HygieneL.E 50.00 | L.E 250.00');
    expect([...el.querySelectorAll('.days > div')].map(text)).toContain('Unpaid leave2');
  });

  it('says them without the days when those no longer add up to the slip', () => {
    const { rows } = render(
      slip(
        { unpaidLeaveDays: 2, unpaidLeaveDeduction: 360, administrativePenalty: 200, administrativePenaltyAmounts: 200 },
        { unpaidLeaveDays: ['2026-10-14'], penalties: [{ date: '2026-10-05', hours: 0, amount: 150, reason: 'Penalty deduction: Absence', disciplinaryActionId: 'a1' }] },
      ),
    );
    expect(rows).toContain('− Unpaid leave | 2 days at L.E 180.00, approved leave not paid | L.E 360.00');
    expect(rows).toContain('− Disciplinary penalties | Penalty deductions of disciplinary actions, as issued | L.E 200.00');
    expect(rows.some((r) => r.startsWith('− Administrative penalties'))).toBe(false);
  });

  it('has neither line in a month with none', () => {
    const { el, rows } = render(slip({}));
    expect(rows.some((r) => r.includes('Unpaid leave') || r.includes('Disciplinary') || r.includes('Administrative'))).toBe(false);
    expect(text(el.querySelector('.days')!)).not.toContain('Unpaid leave');
  });
});
