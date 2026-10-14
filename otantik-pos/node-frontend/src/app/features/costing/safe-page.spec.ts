import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, SafeEntry, SafeMonth } from '../../core/costing/costing.api';
import { Approvals } from './approvals';
import { SafePage } from './safe-page';
import { Treasury } from './financials';

function entry(overrides: Partial<SafeEntry>): SafeEntry {
  return {
    id: 'e', kind: 'Funding', fund: 'Safe', date: '2026-10-02', description: 'Funding · owners’ financing', in: 0, out: 0, balance: 0, source: null,
    revenueDate: null, supplierId: null, purchaseInvoiceId: null, expenseItemId: null, notes: null, recordedBy: 'Omar', recordedAtUtc: '2026-10-02T09:00:00Z',
    approvedBy: null, reversalOf: null, voidedAtUtc: null, voidedBy: null, includesTax: false, taxPercent: null, tax: 0, ...overrides,
  };
}

const month: SafeMonth = {
  year: 2026, month: 10, from: '2026-10-01', to: '2026-10-31', openingBalance: 12_000, openingKind: 'CarriedOver', openingSource: null,
  in: 5_000, out: 1_250, voidedIn: 0, voidedOut: 0, closingBalance: 15_750, balanceNow: 15_750, hasOpeningBalance: true,
  entries: [
    entry({ id: 'f', in: 5_000, balance: 17_000 }),
    entry({ id: 'v', kind: 'ExpenseVoucher', description: 'Electricity', out: 1_250, balance: 15_750, recordedBy: 'Mona', approvedBy: 'Omar', date: '2026-10-05' }),
  ],
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the safe', () => {
  const safe = vi.fn();
  const approvals = { pending: vi.fn(() => []), pendingFor: vi.fn(), waitingToBeMade: vi.fn(), refresh: vi.fn(), delete: vi.fn(), canApprove: () => true };
  const treasury = { enter: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { safe } },
        { provide: Approvals, useValue: approvals },
        { provide: Treasury, useValue: treasury },
      ],
    });
    const fixture = TestBed.createComponent(SafePage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    safe.mockReset().mockReturnValue(of(month));
    approvals.pendingFor.mockReset().mockReturnValue(undefined);
    approvals.waitingToBeMade.mockReset().mockReturnValue([]);
    approvals.delete.mockReset().mockResolvedValue('Applied');
    treasury.enter.mockReset().mockResolvedValue(true);
  });

  it('shows what is in it, what the month opened with and why, and every entry with the balance after it', async () => {
    const { el } = await render();

    const now = new Date();
    expect(safe).toHaveBeenCalledWith(now.getFullYear(), now.getMonth() + 1);
    expect(text(el.querySelector('.tile.key')!)).toContain('L.E 15,750.00');
    expect(text(el.querySelectorAll('.tile')[1])).toContain('carried over from the month before · رصيد مرحل');
    const rows = [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 6).map(text));
    expect(rows[0]).toEqual(['1 Oct', 'Opened with · carried over from the month before · رصيد مرحل', 'L.E 12,000.00', '']);
    expect(rows[1]).toEqual(['2 Oct', 'Funding Funding · owners’ financing', 'L.E 5,000.00', '', 'L.E 17,000.00', 'Omar']);
    expect(rows[2]).toEqual(['5 Oct', 'Expense voucher Electricity', '', 'L.E 1,250.00', 'L.E 15,750.00', 'Mona approved by Omar']);
    // No opening balance to enter: it has one.
    expect([...el.querySelectorAll('button')].map(text)).not.toContain('flag Opening balance');
    // Nothing voided this month: nothing to say about it.
    expect(el.querySelector('.voided-note')).toBeNull();
  });

  it('says what was voided this month, which In and Out leave out', async () => {
    safe.mockReturnValue(of({ ...month, voidedIn: 999, voidedOut: 10_000 }));
    const { el } = await render();

    expect(text(el.querySelector('.voided-note')!)).toBe(
      'Not in In or Out: L.E 999.00 in and L.E 10,000.00 out, voided this month along with their reversals. لا تشمل القيود الملغاة.',
    );
  });

  it('shows the tax in a day’s takings entered with it, and what they were before it', async () => {
    safe.mockReturnValue(of({
      ...month,
      entries: [entry({ id: 't', kind: 'RevenueTransfer', description: 'Takings of 12 Oct', in: 1_140, balance: 13_140, includesTax: true, taxPercent: 14, tax: 140 })],
    }));
    const { el } = await render();

    expect(text(el.querySelectorAll('tbody tr')[1].querySelectorAll('td')[1])).toBe(
      'Takings in Takings of 12 Oct Tax in it at 14%: L.E 140.00 · L.E 1,000.00 before tax',
    );
  });

  it('enters money in and out, and voids an entry through the approvals', async () => {
    const { page } = await render();

    await page['enter']('Funding');
    expect(treasury.enter).toHaveBeenCalledWith('Funding');
    expect(safe).toHaveBeenCalledTimes(2);

    await page['voidEntry'](month.entries[1]);
    expect(approvals.delete).toHaveBeenCalledWith('Treasury', 'v', 'the expense voucher of 5 Oct', 'Electricity');
    expect(safe).toHaveBeenCalledTimes(3);
  });

  it('lists what waits for a manager, and locks an entry being voided', async () => {
    approvals.waitingToBeMade.mockReturnValue([
      { id: 'q1', module: 'Treasury', action: 'Create', summary: 'Supplier payment · Nile Farms · L.E 500.00', requestedBy: 'Mona', requestedAtUtc: '2026-10-06T09:00:00Z' },
    ]);
    approvals.pendingFor.mockImplementation((id: string) => (id === 'v' ? { action: 'Delete', module: 'Treasury', requestedBy: 'Mona' } : undefined));
    const { el } = await render();

    expect(text(el.querySelector('.waiting')!)).toContain('Supplier payment · Nile Farms · L.E 500.00');
    expect(text(el.querySelector('.waiting')!)).toContain('no cash has moved');
    const voucher = el.querySelectorAll('tbody tr')[2];
    expect(text(voucher)).toContain('Void requested');
    expect(voucher.querySelector('button[aria-label^="Void"]')).toBeNull();
  });
});
