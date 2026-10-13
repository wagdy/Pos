import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { CostingApi, ReconciliationDay, ReconciliationReport } from '../../core/costing/costing.api';
import { ReconciliationPage } from './reconciliation-page';

function day(overrides: Partial<ReconciliationDay>): ReconciliationDay {
  return {
    date: '2026-10-12', takingsEntered: 0, paidOut: 0, tax: 0, netCash: 0, itemSales: 0, discounts: 0, points: 0, deliveryFees: 0, refunds: 0,
    cardAndOnline: 0, expectedCash: 0, difference: 0, ...overrides,
  };
}

// The 12th: 1,140 counted with the tax in it, for 1,200 sold of which 150 by card, 50 discounted: 10 short.
// The 11th: cash in for nothing rung up.
const short = day({
  takingsEntered: 1_140, tax: 140, netCash: 1_000, itemSales: 1_200, discounts: 50, cardAndOnline: 150, expectedCash: 1_010, difference: -10,
});
const over = day({ date: '2026-10-11', takingsEntered: 228, tax: 28, netCash: 200, difference: 200 });
const report: ReconciliationReport = {
  from: '2026-10-01', to: '2026-10-31', taxPercent: 14, days: [short, over],
  total: day({ date: null, takingsEntered: 1_368, tax: 168, netCash: 1_200, itemSales: 1_200, discounts: 50, cardAndOnline: 150, expectedCash: 1_010, difference: 190 }),
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the reconciliation', () => {
  const reconciliation = vi.fn();

  async function render() {
    TestBed.configureTestingModule({ providers: [{ provide: CostingApi, useValue: { reconciliation } }] });
    const fixture = TestBed.createComponent(ReconciliationPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => reconciliation.mockReset().mockReturnValue(of(report)));

  it('sets each day’s cash before tax against the cash its sales should have brought in', async () => {
    const { el } = await render();

    const now = new Date();
    const [from] = reconciliation.mock.lastCall!;
    expect(from).toBe(`${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-01`);
    expect([...el.querySelectorAll('.tile')].map((t) => [text(t.querySelector('.label')!), text(t.querySelector('strong')!)])).toEqual([
      ['Cash before tax (A) · صافي النقدية', 'L.E 1,200.00'],
      ['Items sold (B) · مبيعات الأصناف', 'L.E 1,200.00'],
      ['Cash expected · النقدية المتوقعة', 'L.E 1,010.00'],
      ['Difference · الفرق', 'L.E 190.00'],
      ['Tax collected · الضريبة المحصلة', 'L.E 168.00'],
    ]);
    expect(text(el.querySelector('.note')!)).toContain("less the tax in them (at the till's 14%). B:");

    const rows = [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text));
    expect(rows[0]).toEqual([
      'Mon 12 Oct', 'L.E 1,140.00', '—', 'L.E 140.00', 'L.E 1,000.00', 'L.E 1,200.00', 'L.E 50.00', '—', '—', 'L.E 150.00', 'L.E 1,010.00',
      'L.E -10.00 short · عجز',
    ]);
    expect(rows[1].at(-1)).toBe('L.E 200.00 not rung up · زيادة');
    expect(el.querySelectorAll('tbody tr')[0].querySelector('td:last-child')!.classList).toContain('negative');
    const total = [...el.querySelectorAll('tfoot td')].map(text);
    expect([total[0], total[1], total.at(-1)]).toEqual(['The month', 'L.E 1,368.00', 'L.E 190.00 not rung up · زيادة']);
  });

  it('moves a month back', async () => {
    const { page } = await render();

    page['move'](-1);
    const before = new Date(new Date().getFullYear(), new Date().getMonth() - 1, 1);
    expect(reconciliation.mock.lastCall![0]).toBe(`${before.getFullYear()}-${String(before.getMonth() + 1).padStart(2, '0')}-01`);
  });
});
