import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, Kpi, KpiReport } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { KpisPage } from './kpis-page';

const kpi = (key: Kpi['key'], value: number | null, status: Kpi['status'], range: [number, number] | null, highIsBad: boolean, missing: string | null = null): Kpi => ({
  key,
  value,
  healthyFrom: range?.[0] ?? null,
  healthyTo: range?.[1] ?? null,
  highIsBad,
  status,
  missing,
});

const report: KpiReport = {
  year: 2026,
  month: 10,
  from: '2026-10-01',
  to: '2026-10-31',
  beverageCategories: ['Drinks'],
  statement: {
    foodSales: 360,
    beverageSales: 60,
    deliveryFees: 0,
    revenue: 420,
    paidOrders: 2,
    foodRecipeCost: 75,
    beverageRecipeCost: 12,
    kitchenWaste: 25,
    spoilage: 10,
    countDifferences: 0,
    costOfSales: 122,
    grossProfit: 298,
    wagesAndBenefits: null,
    otherControllableCosts: null,
    occupationCost: null,
    interest: null,
    depreciation: null,
    netProfit: null,
  },
  expenses: { wagesAndBenefits: null, labourHours: null, otherControllableCosts: null, occupationCost: null, interest: null, depreciation: null },
  kpis: [
    kpi('FoodCost', 30.6, 'Healthy', [28, 35], true),
    kpi('BeverageCost', 12, 'Below', [20, 25], true),
    kpi('LabourCost', null, 'Missing', [25, 35], true, "Enter the month's wages and benefits."),
    kpi('AverageCheck', 210, 'NoRange', null, false),
    kpi('GrossProfitMargin', 55, 'Below', [60, 70], false),
    kpi('NetProfitMargin', 20, 'Above', [5, 15], false),
  ],
  items: [],
  itemsWithoutRecipe: 0,
  salesWithoutRecipe: 0,
};

describe('the KPIs', () => {
  const kpis = vi.fn();
  const saveExpenses = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { kpis, saveExpenses } },
        { provide: AuthService, useValue: { can: () => true } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(KpisPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const cards = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.kpi')];
    return { fixture, cards, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    kpis.mockReset().mockReturnValue(of(report));
    saveExpenses.mockReset().mockReturnValue(of(report.expenses));
  });

  it('colours each KPI by the side of its range that hurts', async () => {
    const { cards } = await render();

    const tones = cards.map((c) => ['ok', 'bad', 'check', 'none'].find((t) => c.classList.contains(t)));
    // Food in range; drinks suspiciously cheap; labour not entered; the check has no range;
    // gross margin too thin; net margin better than the range.
    expect(tones).toEqual(['ok', 'check', 'none', 'none', 'bad', 'ok']);
  });

  it('says what is missing rather than showing 0', async () => {
    const { cards } = await render();

    expect(cards[2].querySelector('.value')?.textContent?.trim()).toBe('—');
    expect(cards[2].textContent).toContain("Enter the month's wages and benefits.");
    expect(cards[0].textContent).toContain('Recipes 20.8% + losses 9.7%');
  });

  it("saves the month's costs whole, empty figures as not known", async () => {
    const { page } = await render();

    page['set']('wagesAndBenefits', 120);
    page['set']('labourHours', '10');
    page['set']('interest', '');
    await page['saveExpenses']();

    const [year, month] = (page['month']() as string).split('-').map(Number);
    expect(saveExpenses).toHaveBeenCalledWith(year, month, {
      wagesAndBenefits: 120,
      labourHours: 10,
      occupationCost: null,
      otherControllableCosts: null,
      interest: null,
      depreciation: null,
    });
  });
});
