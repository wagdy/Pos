import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { BreakEvenWorksheet, CostingApi, breakEvenColumn } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { BreakEvenPage } from './break-even-page';

// The template's own worksheet (Vivando, Incorporated): its income statement and fixed costs.
const template: BreakEvenWorksheet = {
  from: '2026-10-01',
  to: '2026-10-31',
  days: 31,
  weeklySales: 26543.43,
  grossSales: 1380258,
  costOfSales: 456458,
  costOfSalesRatio: 456458 / 1380258,
  grossProfit: 923800,
  controllableCosts: 686995,
  occupationCost: 71500,
  interest: 24926,
  depreciation: 53803,
  totalFixedCosts: 837224,
  restaurantProfit: 86576,
  breakEvenYearlySales: 1250904,
  breakEvenWeeklySales: 24056,
  monthsWithoutCosts: [],
  scenarios: [],
};

describe('the break-even worksheet', () => {
  it("works the template's four options out as it does", () => {
    const round = (v: number) => Math.round(v);
    const options = [32000, 40000, 20000, 15000].map((weekly) => breakEvenColumn(template, weekly));

    expect(options.map((o) => round(o.grossSales))).toEqual([1664000, 2080000, 1040000, 780000]);
    expect(options.map((o) => round(o.costOfSales))).toEqual([550293, 687866, 343933, 257950]);
    expect(options.map((o) => round(o.grossProfit))).toEqual([1113707, 1392134, 696067, 522050]);
    expect(options.map((o) => Math.round(o.profitMarginPercent!))).toEqual([67, 67, 67, 67]);
    // Its Restaurant Profit row, to the dollar it rounds to.
    expect(options.map((o) => round(o.restaurantProfit))).toEqual([276483, 554910, -141157, -315174]);
  });

  describe('the page', () => {
    const breakEven = vi.fn();
    const saveBreakEvenScenarios = vi.fn();

    async function render() {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: CostingApi, useValue: { breakEven, saveBreakEvenScenarios } },
          { provide: AuthService, useValue: { can: () => true } },
          { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
        ],
      });
      const fixture = TestBed.createComponent(BreakEvenPage);
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, element: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
    }

    beforeEach(() => {
      breakEven.mockReset().mockReturnValue(of(template));
      saveBreakEvenScenarios.mockReset().mockImplementation((s: number[]) => of(s));
    });

    it('offers round options either side of now until the manager keeps their own', async () => {
      const { page } = await render();

      // 26,543.43 × 1.2, 1.5, 0.75 and 0.6, to the nearest thousand.
      expect(page['options']()).toEqual([32000, 40000, 20000, 16000]);
      expect(page['optionsChanged']()).toBe(true);
    });

    it('shows profit green and loss red, and the break-even on the chart', async () => {
      breakEven.mockReturnValue(of({ ...template, scenarios: [32000, 40000, 20000, 15000] }));
      const { element } = await render();

      const profitRow = [...element.querySelectorAll('tbody tr')].at(-1)!;
      const cells = [...profitRow.querySelectorAll('td.num')];
      expect(cells.map((c) => (c.classList.contains('positive') ? '+' : c.classList.contains('negative') ? '-' : '0'))).toEqual(['+', '+', '+', '-', '-']);
      expect(element.textContent).toContain('Break even at L.E 1,250,904.00 yearly sales, which is L.E 24,056.00 weekly sales');
      expect(element.querySelector('svg rect.breakeven')).not.toBeNull();
      expect(element.querySelectorAll('svg circle')).toHaveLength(5);
    });

    it('keeps the options as typed', async () => {
      const { page } = await render();

      page['setOption'](3, '15000');
      await page['saveOptions']();

      expect(saveBreakEvenScenarios).toHaveBeenCalledWith([32000, 40000, 20000, 15000]);
      expect(page['optionsChanged']()).toBe(false);
    });
  });
});
