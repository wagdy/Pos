import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi, SafeVoucherInput } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { SafeVoucherDialog, SafeVoucherDialogData, Treasury } from './financials';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('entering a safe entry', () => {
  const supplierBalances = vi.fn();
  const expenseItems = vi.fn();
  const close = vi.fn();

  async function dialog(data: SafeVoucherDialogData) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { supplierBalances, expenseItems } },
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
    supplierId: null, expenseItemId: 'power', notes: null, reason: null,
  };

  function service(manager: boolean): Treasury {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { recordVoucher, approvalRequests, businessToday: () => of({ today: '2026-10-12' }) } },
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

  it('sends nothing when cancelled', async () => {
    open.mockReturnValue({ afterClosed: () => of(undefined) });

    expect(await service(true).enter('Funding')).toBe(false);
    expect(recordVoucher).not.toHaveBeenCalled();
  });
});
