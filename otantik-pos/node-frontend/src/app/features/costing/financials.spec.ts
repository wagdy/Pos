import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi, SafeVoucherInput } from '../../core/costing/costing.api';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { SafeVoucherDialog, SafeVoucherDialogData, Treasury, taxIn } from './financials';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

const employee = (id: string, name: string, isActive = true): Employee => ({
  id, name, jobTitle: 'Chef', monthlySalary: 5_400, shiftStartsAt: '10:00:00', shiftEndsAt: '19:00:00', daysOff: [], deviceUserId: null,
  hiredOn: '2026-01-01', leftOn: isActive ? null : '2026-09-30', isActive, dailyRate: 180, hourlyRate: 20, nationalId: null, mobileNumber: null, idPhoto: null, employmentType: 'Monthly', dailyWage: 0, departmentId: 'general', jobRoleId: null,
});

describe('entering a safe entry', () => {
  const employees = vi.fn();
  const supplierBalances = vi.fn();
  const expenseItems = vi.fn();
  const takingsPreview = vi.fn();
  const close = vi.fn();

  async function dialog(data: SafeVoucherDialogData) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { supplierBalances, expenseItems, takingsPreview } },
        { provide: HrApi, useValue: { employees } },
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(SafeVoucherDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    supplierBalances.mockReset().mockReturnValue(of([
      { supplierId: 'nile', supplier: 'Nile Farms', isArchived: false, openingDebt: 0, creditPurchases: 2_500, payments: 500, balance: 2_000, lastPaidOn: null, openInvoices: 1 },
      { supplierId: 'delta', supplier: 'Delta Oils', isArchived: false, openingDebt: 0, creditPurchases: 100, payments: 100, balance: 0, lastPaidOn: null, openInvoices: 0 },
    ]));
    expenseItems.mockReset().mockReturnValue(of([]));
    employees.mockReset().mockReturnValue(of([employee('hassan', 'Hassan'), employee('old', 'Left Already', false)]));
    takingsPreview.mockReset().mockImplementation((day: string) => of({ date: day, entered: null, paidOut: 0, invoices: 0 }));
    close.mockReset();
  });

  it('pays a supplier what they are owed, never more, and only those owed are offered', async () => {
    const { page } = await dialog({ kind: 'SupplierPayment', canApprove: true, supplierId: 'nile' });

    expect(page['owedSuppliers']().map((s: { supplier: string }) => s.supplier)).toEqual(['Nile Farms']);
    // Owed in full to begin with.
    expect(page['amount']).toBe(2_000);
    page['amount'] = 2_500;
    expect(page['problem']()).toBe('That is more than they are owed.');

    page['amount'] = 750;
    page['notes'] = ' Cash on delivery ';
    page['done']();
    const voucher = close.mock.lastCall![0] as SafeVoucherInput;
    expect(voucher).toMatchObject({ kind: 'SupplierPayment', amount: 750, supplierId: 'nile', notes: 'Cash on delivery', expenseItemId: null, source: null });
    expect(voucher.requestId).not.toBe(voucher.transactionId);
  });

  it('advances an employee still employed, from the safe or a day’s takings', async () => {
    const { el, page } = await dialog({ kind: 'EmployeeAdvance', canApprove: true, today: '2026-10-20' });

    expect(text(el.querySelector('h2')!)).toBe('Employee advance سلفة موظف');
    expect(text(el)).toContain('It is not an expense: it comes off their pay for the month');
    expect(page['payable']().map((e: Employee) => e.name)).toEqual(['Hassan']);
    page['amount'] = 300;
    // No one named: a lump sum handed out as advances, given out on HR → Advances.
    expect(page['problem']()).toBeNull();
    expect(text(el)).toContain('A lump sum handed out as advances: a manager gives it out on HR → Advances');
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ kind: 'EmployeeAdvance', amount: 300, employeeId: null });
    page['employeeId'].set('hassan');
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ kind: 'EmployeeAdvance', amount: 300, employeeId: 'hassan', revenueDate: null, date: '2026-10-20' });

    page['paidFrom'] = 'DayRevenue';
    page['revenueDate'] = '2026-10-21';
    expect(page['problem']()).toBe('Choose the day whose takings pay it: that day or one before the advance.');
    page['revenueDate'] = '2026-10-19';
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ employeeId: 'hassan', revenueDate: '2026-10-19', supplierId: null, expenseItemId: null });
  });

  it('asks for an advance paid outside the till, for HR: no safe or takings to say it is paid from', async () => {
    const { el, page } = await dialog({ kind: 'EmployeeAdvance', canApprove: true, today: '2026-10-20', outsideTill: true });

    expect(text(el.querySelector('h2')!)).toBe('Advance outside the till سلفة من خارج الخزنة');
    expect(text(el)).toContain('No cash leaves the safe or a day’s takings, and Financials never sees it.');
    expect(el.querySelector('[aria-label="Paid from"]')).toBeNull();
    page['amount'] = 400;
    page['paidFrom'] = 'DayRevenue';
    page['revenueDate'] = '2026-10-25';
    expect(page['problem']()).toBeNull();
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ kind: 'EmployeeAdvance', amount: 400, employeeId: null, revenueDate: null, date: '2026-10-20' });
  });

  it('pays a finalized month’s salary to the employee it is for, left or not', async () => {
    const { el, page } = await dialog({ kind: 'SalaryPayment', canApprove: true, today: '2026-10-20', employeeId: 'old', payrollMonth: '2026-09-01', amount: 5_526.33 });

    expect(text(el)).toContain('The pay of September 2026, as their finalized payslip says.');
    expect(page['payable']().map((e: Employee) => e.name)).toEqual(['Hassan', 'Left Already']);
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({
      kind: 'SalaryPayment', amount: 5_526.33, employeeId: 'old', payrollMonth: '2026-09-01', revenueDate: null, date: '2026-10-20',
    });
  });

  it('asks where the money came from, and tells an accountant that a manager approves it', async () => {
    const { el, page } = await dialog({ kind: 'Funding', canApprove: false });

    expect(page['sources']).toEqual(['OwnerFinancing', 'Other']);
    expect(text(el)).toContain('A manager approves safe entries: no cash moves until then.');
    expect([...el.querySelectorAll('button')].map(text)).toContain('Send for approval');
    expect(page['problem']()).toBe('Enter an amount more than nothing.');
  });

  it('is dated the till’s business day, not the browser’s', async () => {
    const { page } = await dialog({ kind: 'Funding', canApprove: true, today: '2026-10-12' });

    expect([page['date'], page['today']]).toEqual(['2026-10-12', '2026-10-12']);
  });

  it('takes the tax out of takings that include it, at the till’s rate, from the gross', async () => {
    const { el, page } = await dialog({ kind: 'RevenueTransfer', canApprove: true, revenueDate: '2026-10-12', amount: 1_140, taxPercent: 14, includesTax: true });

    expect(page['taxSplit']()).toEqual({ net: 1_000, tax: 140 });
    expect(text(el)).toContain('Before tax L.E 1,000.00 · tax at 14% L.E 140.00, in the gross.');
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ kind: 'RevenueTransfer', amount: 1_140, includesTax: true });
  });

  it('enters takings typed by hand without tax unless ticked, and never with tax when the till has no rate', async () => {
    const byHand = await dialog({ kind: 'RevenueTransfer', canApprove: true, amount: 500, taxPercent: 14 });
    expect(byHand.page['includesTax']).toBe(false);
    expect(byHand.page['taxSplit']()).toBeNull();
    TestBed.resetTestingModule();

    const { el, page } = await dialog({ kind: 'RevenueTransfer', canApprove: true, amount: 500, taxPercent: null, includesTax: true });
    expect(text(el)).toContain('The till has no tax rate yet');
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ includesTax: false });
  });

  it('enters a day’s takings gross, and shows what was paid out of them and what goes into the safe', async () => {
    takingsPreview.mockImplementation((day: string) =>
      of(day === '2026-10-13' ? { date: day, entered: null, paidOut: 1_454.07, invoices: 5 } : { date: day, entered: null, paidOut: 0, invoices: 0 }));
    const { el, page } = await dialog({ kind: 'RevenueTransfer', canApprove: true, revenueDate: '2026-10-13', amount: 10_495, today: '2026-10-14' });
    // Each line: its English label (the Arabic apart), and the sum.
    const label = (dt: Element) =>
      [...dt.childNodes].filter((n) => n.nodeType !== Node.COMMENT_NODE && !(n instanceof Element && n.classList.contains('ar'))).map((n) => n.textContent).join('').replace(/\s+/g, ' ').trim();
    const breakdown = () => [...el.querySelectorAll('.breakdown > div')].map((row) => [label(row.querySelector('dt')!), text(row.querySelector('dd')!)]);

    expect(takingsPreview).toHaveBeenCalledWith('2026-10-13');
    expect(breakdown()).toEqual([
      ['Gross takings entered', 'L.E 10,495.00'],
      ['Less paid out of them · 5 invoices', 'L.E 1,454.07'],
      ['Net into the safe', 'L.E 9,040.93'],
    ]);
    expect([...el.querySelectorAll('.breakdown .ar')].map(text)).toEqual(['إجمالي الإيراد', 'المنصرف منه', 'الصافي للخزنة']);

    // Another day: what was paid out of that one.
    page['revenueDate'] = '2026-10-12';
    page['takingsDayChosen']();
    await Promise.resolve();
    expect(page['netIntoSafe']()).toBe(10_495);

    page['revenueDate'] = '2026-10-13';
    page['takingsDayChosen']();
    await Promise.resolve();
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ kind: 'RevenueTransfer', amount: 10_495, revenueDate: '2026-10-13' });
  });

  it('says when more was paid out of the takings than their gross, and refuses a day already entered', async () => {
    takingsPreview.mockReturnValue(of({ date: '2026-10-13', entered: null, paidOut: 1_454, invoices: 2 }));
    const short = await dialog({ kind: 'RevenueTransfer', canApprove: true, revenueDate: '2026-10-13', amount: 1_000, today: '2026-10-14' });
    expect(text(short.el)).toContain('The invoices paid out of these takings come to L.E 454.00 more than them: that comes out of the safe.');
    expect(short.page['problem']()).toBeNull();
    TestBed.resetTestingModule();

    takingsPreview.mockReturnValue(of({ date: '2026-10-13', entered: 10_495, paidOut: 1_454.07, invoices: 5 }));
    const { el, page } = await dialog({ kind: 'RevenueTransfer', canApprove: true, revenueDate: '2026-10-13', amount: 500, today: '2026-10-14' });
    expect(page['problem']()).toBe('These takings are entered already: L.E 10,495.00. To change them, void that entry on the Safe tab and enter them again.');
    // Nothing to break down: they are in already.
    expect(el.querySelector('.breakdown')).toBeNull();
  });

  it('works the tax out to the piastre, as the server does', () => {
    expect(taxIn(100, 14)).toEqual({ net: 87.72, tax: 12.28 });
    expect(taxIn(1_140, 14)).toEqual({ net: 1_000, tax: 140 });
  });

  it('moves a day’s takings in on that day or after it', async () => {
    const { page } = await dialog({ kind: 'RevenueTransfer', canApprove: true, revenueDate: '2026-10-10', amount: 3_200 });
    page['date'] = '2026-10-09';

    expect(page['problem']()).toContain('Choose the day whose takings these are');
    page['date'] = '2026-10-11';
    page['done']();
    expect(close.mock.lastCall![0]).toMatchObject({ kind: 'RevenueTransfer', amount: 3_200, revenueDate: '2026-10-10', date: '2026-10-11' });
  });
});

describe('sending a safe entry', () => {
  const recordVoucher = vi.fn();
  const approvalRequests = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const voucher: SafeVoucherInput = {
    requestId: 'q', transactionId: 't', kind: 'ExpenseVoucher', amount: 1_250, date: '2026-10-12', source: null, revenueDate: null,
    supplierId: null, expenseItemId: 'power', notes: null, reason: null, includesTax: false,
  };

  function service(manager: boolean): Treasury {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: CostingApi,
          useValue: { recordVoucher, approvalRequests, businessToday: () => of({ today: '2026-10-12' }), financialSettings: () => of({ taxPercent: 14 }) },
        },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: AuthService, useValue: { can: (p: string) => p !== Permissions.ApprovalDecide || manager } },
      ],
    });
    return TestBed.inject(Treasury);
  }

  beforeEach(() => {
    recordVoucher.mockReset();
    approvalRequests.mockReset().mockReturnValue(of({ pending: [], decided: [] }));
    open.mockReset().mockReturnValue({ afterClosed: () => of(voucher) });
    notify.info.mockReset();
  });

  it('is entered at once for a manager', async () => {
    recordVoucher.mockReturnValue(of({ id: 't', kind: 'ExpenseVoucher', amount: -1_250 }));

    expect(await service(true).enter('ExpenseVoucher')).toBe(true);

    // Dated the till's business day to begin with: after midnight, still the night before.
    expect(open.mock.lastCall![1].data).toEqual({ kind: 'ExpenseVoucher', canApprove: true, today: '2026-10-12' });
    expect(recordVoucher).toHaveBeenCalledWith(voucher);
    expect(notify.info).toHaveBeenCalledWith(expect.stringContaining('Expense voucher entered: 1,250.00'));
  });

  it('is a request for an accountant, said so', async () => {
    recordVoucher.mockReturnValue(of({ outcome: 'Requested', request: { action: 'Create', module: 'Treasury' } }));

    expect(await service(false).enter('ExpenseVoucher')).toBe(true);

    expect(notify.info).toHaveBeenCalledWith('Submitted to the manager for approval. Nothing is made until it is approved.');
  });

  it('offers a day’s takings with the till’s tax rate, and says the tax in them once entered', async () => {
    open.mockReturnValue({ afterClosed: () => of({ ...voucher, kind: 'RevenueTransfer', amount: 1_140, includesTax: true }) });
    recordVoucher.mockReturnValue(of({ id: 't', kind: 'RevenueTransfer', amount: 1_140, includesTax: true, taxPercent: 14, taxAmount: 140 }));

    expect(await service(true).enter('RevenueTransfer', { revenueDate: '2026-10-11', amount: 1_140, includesTax: true })).toBe(true);

    expect(open.mock.lastCall![1].data).toMatchObject({ kind: 'RevenueTransfer', taxPercent: 14, includesTax: true, revenueDate: '2026-10-11' });
    expect(notify.info).toHaveBeenCalledWith('Takings in entered: 1,140.00 L.E, tax in it 140.00 L.E.');
  });

  it('sends nothing when cancelled', async () => {
    open.mockReturnValue({ afterClosed: () => of(undefined) });

    expect(await service(true).enter('Funding')).toBe(false);
    expect(recordVoucher).not.toHaveBeenCalled();
  });
});
