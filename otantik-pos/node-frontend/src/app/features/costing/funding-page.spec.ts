import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, DayRevenue, DayRevenueReport, FundingReport } from '../../core/costing/costing.api';
import { FundingPage } from './funding-page';
import { Treasury } from './financials';

function day(overrides: Partial<DayRevenue>): DayRevenue {
  return { date: '2026-10-13', cashSales: 0, entered: 0, paidOut: 0, transferred: 0, left: 0, ...overrides };
}

// The 13th: the till took 10,500; 10,495 entered, 1,454.07 paid out of it, the rest in the safe.
// The 14th: the till took 3,200, nothing entered yet.
const revenue: DayRevenueReport = {
  from: '2026-10-01', to: '2026-10-31', cashSales: 13_700, entered: 10_495, paidOut: 1_454.07, transferred: 9_040.93, left: 3_205,
  days: [
    day({ date: '2026-10-14', cashSales: 3_200, left: 3_200 }),
    day({ cashSales: 10_500, entered: 10_495, paidOut: 1_454.07, transferred: 9_040.93, left: 5 }),
  ],
};
const funding: FundingReport = { from: '2026-10-01', to: '2026-10-31', carriedOver: 0, ownerFinancing: 0, other: 0, revenue: 9_040.93, total: 9_040.93, entries: [] };

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('each day’s cash, on the funding sources', () => {
  const enter = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { funding: () => of(funding), dayRevenue: () => of(revenue) } },
        { provide: Treasury, useValue: { enter } },
      ],
    });
    const fixture = TestBed.createComponent(FundingPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement };
  }

  beforeEach(() => enter.mockReset().mockResolvedValue(false));

  it('shows the takings entered, gross, what was paid out of them and what reached the safe; and enters a day’s takings at what the till took', async () => {
    const { el } = await render();

    const table = [...el.querySelectorAll('table')].at(-1)!;
    const rows = [...table.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text));
    expect(rows).toEqual([
      ['Wed 14 Oct', 'L.E 3,200.00', '—', '—', '—', 'L.E 3,200.00', 'move_to_inbox Enter takings'],
      ['Tue 13 Oct', 'L.E 10,500.00', 'L.E 10,495.00', 'L.E 1,454.07', 'L.E 9,040.93', 'L.E 5.00', ''],
    ]);

    (table.querySelector('tbody button') as HTMLButtonElement).click();
    expect(enter).toHaveBeenCalledWith('RevenueTransfer', { revenueDate: '2026-10-14', amount: 3_200, includesTax: true });
  });
});
