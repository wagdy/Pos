import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, SupplierBalance, SupplierStatement } from '../../core/costing/costing.api';
import { SupplierDebtsPage } from './supplier-debts-page';
import { Treasury } from './financials';

const nile: SupplierBalance = {
  supplierId: 'nile', supplier: 'Nile Farms', isArchived: false, openingDebt: 500, creditPurchases: 2_500, payments: 1_000, balance: 2_000,
  lastPaidOn: '2026-10-10', openInvoices: 1,
};
const delta: SupplierBalance = { ...nile, supplierId: 'delta', supplier: 'Delta Oils', openingDebt: 0, creditPurchases: 300, payments: 300, balance: 0, openInvoices: 0 };

const statement: SupplierStatement = {
  supplierId: 'nile', supplier: 'Nile Farms', openingDebt: 500, creditPurchases: 2_500, payments: 1_000, balance: 2_000,
  lines: [
    { id: 'l1', date: '2026-10-01', kind: 'OpeningBalance', description: 'Owed before the treasury', owed: 500, paid: 0, balance: 500, sourceId: 'o', isReversal: false },
    { id: 'l2', date: '2026-10-03', kind: 'CreditInvoice', description: 'Invoice F-9 on credit', owed: 2_500, paid: 0, balance: 3_000, sourceId: 'i', isReversal: false },
    { id: 'l3', date: '2026-10-10', kind: 'Payment', description: 'Payment voucher', owed: 0, paid: 1_000, balance: 2_000, sourceId: 'p', isReversal: false },
  ],
  openInvoices: [{ invoiceId: 'i', supplierInvoiceNumber: 'F-9', date: '2026-10-03', total: 2_500, paid: 500, remaining: 2_000, status: 'PartlyPaid' }],
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('supplier debts', () => {
  const supplierBalances = vi.fn();
  const suppliers = vi.fn();
  const supplierStatement = vi.fn();
  const treasury = { enter: vi.fn(), supplierOpening: vi.fn() };

  async function render(supplier: string | null = null) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { supplierBalances, suppliers, supplierStatement } },
        { provide: Treasury, useValue: treasury },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(supplier ? { supplier } : {}) } } },
      ],
    });
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SupplierDebtsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    supplierBalances.mockReset().mockReturnValue(of([nile, delta]));
    suppliers.mockReset().mockReturnValue(of([]));
    supplierStatement.mockReset().mockReturnValue(of(statement));
    treasury.enter.mockReset().mockResolvedValue(true);
  });

  it('lists what each supplier is owed, and what is owed in all', async () => {
    const { el } = await render();

    expect(text(el.querySelector('.tile.key')!)).toContain('L.E 2,000.00');
    const rows = [...el.querySelectorAll('table')[0].querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 5).map(text));
    expect(rows).toEqual([
      ['Nile Farms', 'L.E 2,500.00', 'L.E 500.00', 'L.E 1,000.00', 'L.E 2,000.00'],
      ['Delta Oils', 'L.E 300.00', '—', 'L.E 300.00', 'L.E 0.00'],
    ]);
    expect(supplierStatement).not.toHaveBeenCalled();
  });

  it('shows a supplier’s statement: the invoices still owed and every line with the balance after it', async () => {
    const { el } = await render('nile');

    expect(supplierStatement).toHaveBeenCalledWith('nile');
    const tables = el.querySelectorAll('table');
    expect([...tables[1].querySelectorAll('tbody tr td')].map(text)).toEqual(
      ['3 Oct 2026', 'F-9', 'L.E 2,500.00', 'L.E 500.00', 'L.E 2,000.00', 'Partly paid · مسددة جزئيًا'],
    );
    expect([...tables[2].querySelectorAll('tbody tr')].map((r) => text(r.querySelectorAll('td')[4]))).toEqual(['L.E 500.00', 'L.E 3,000.00', 'L.E 2,000.00']);
  });

  it('pays the supplier what they are owed, from their statement', async () => {
    const { page } = await render('nile');

    await page['pay'](statement);

    expect(treasury.enter).toHaveBeenCalledWith('SupplierPayment', { supplierId: 'nile', amount: 2_000 });
    expect(supplierBalances).toHaveBeenCalledTimes(2);
  });
});
