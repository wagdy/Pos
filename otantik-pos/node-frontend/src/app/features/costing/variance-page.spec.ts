import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, VarianceReport, VarianceRow } from '../../core/costing/costing.api';
import { VariancePage } from './variance-page';

function row(overrides: Partial<VarianceRow>): VarianceRow {
  return {
    rawMaterialId: 'm',
    code: null,
    material: 'Material',
    category: null,
    unit: 'Gram',
    purchaseUnitSize: 1000,
    opening: 0,
    received: 0,
    rawWaste: 0,
    productWaste: 0,
    standardUsage: 0,
    expectedClosing: 0,
    closing: 0,
    actualUsage: 0,
    varianceQuantity: 0,
    variancePercent: null,
    unexplainedQuantity: 0,
    unitCost: null,
    varianceValue: null,
    unexplainedValue: null,
    evaluation: 'WithinLimit',
    ...overrides,
  };
}

// The template's examples: fries short beyond what anything explains, tomato short by what was
// recorded spoiled, mozzarella over, and frying oil, which no recipe uses.
const report: VarianceReport = {
  from: { id: 'a', postedAtUtc: '2026-10-01T22:00:00Z', postedBy: 'Omar' },
  to: { id: 'b', postedAtUtc: '2026-10-08T22:00:00Z', postedBy: 'Omar' },
  tolerancePercent: 5,
  rows: [
    row({ rawMaterialId: 'fries', material: 'Farm frites', opening: 10000, received: 5000, standardUsage: 1500, expectedClosing: 13500, closing: 12000, actualUsage: 3000, varianceQuantity: 1500, variancePercent: 100, unexplainedQuantity: 1500, unitCost: 0.06, varianceValue: 90, unexplainedValue: 90, evaluation: 'Unfavourable' }),
    row({ rawMaterialId: 'tomato', material: 'Tomato', opening: 4930, rawWaste: 500, standardUsage: 141, expectedClosing: 4289, closing: 4290, actualUsage: 640, varianceQuantity: 499, variancePercent: 353.3, unexplainedQuantity: -1, unitCost: 0.035, varianceValue: 17.46, unexplainedValue: -0.04, evaluation: 'Unfavourable' }),
    row({ rawMaterialId: 'cheese', material: 'Mozzarella', standardUsage: 300, varianceQuantity: -200, variancePercent: -66.7, unitCost: 0.3, varianceValue: -60, unexplainedValue: -120, evaluation: 'Favourable' }),
    row({ rawMaterialId: 'oil', material: 'Frying oil', unit: 'Millilitre', actualUsage: 1500, varianceQuantity: 1500, unitCost: 0.095, varianceValue: 142.5, unexplainedValue: 142.5, evaluation: 'SharedCost' }),
  ],
  unfavourableCount: 2,
  netVarianceValue: 47.46,
  recordedWasteValue: 17.5,
  unexplainedValue: -30.04,
  largestRelativeMaterial: 'Tomato',
  largestRelativePercent: 353.3,
  notInBothCounts: [],
  soldWithoutStock: [{ itemCode: '1', menuItem: 'Garlic Bread', quantity: 2 }],
  missingPrices: [],
};

describe('the variance report', () => {
  async function render() {
    const variance = vi.fn().mockReturnValue(of(report));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CostingApi, useValue: { variance, stockCounts: () => of([]) } }],
    });
    const fixture = TestBed.createComponent(VariancePage);
    await fixture.whenStable();
    fixture.detectChanges();
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')];
    const cells = (r: Element) => [...r.querySelectorAll('td')].map((td) => td.textContent?.replace(/\s+/g, ' ').trim());
    return { fixture, variance, rows, cells };
  }

  it('asks for the last two counts once, and marks shortfalls red and surpluses blue', async () => {
    const { variance, rows } = await render();

    expect(variance).toHaveBeenCalledTimes(1);
    expect(variance).toHaveBeenCalledWith(null, null);
    expect(rows.map((r) => (r.classList.contains('deficit') ? 'red' : r.classList.contains('surplus') ? 'blue' : '-'))).toEqual([
      'red',
      'red',
      'blue',
      '-',
    ]);
  });

  it('shows quantities as counted, in kg and litres', async () => {
    const { rows, cells } = await render();

    expect(cells(rows[0]).slice(2, 10)).toEqual(['10 kg', '5 kg', '0 kg', '1.5 kg', '13.5 kg', '12 kg', '3 kg', '+1.5 kg']);
    expect(cells(rows[0])[11]).toBe('L.E 60.00 / kg');
  });

  it('says when recorded waste covers a shortfall, so it is not read as theft', async () => {
    const { rows, cells } = await render();

    expect(cells(rows[1])[14]).toContain('covered by recorded waste');
    expect(cells(rows[0])[14]).not.toContain('covered');
  });

  it('shows a shared cost as what was used, with no variance to judge', async () => {
    const { rows, cells } = await render();

    expect(cells(rows[3]).slice(9, 15)).toEqual(['—', '—', 'L.E 95.00 / L', 'L.E 142.50 used', '—', 'Shared costتكلفة مشتركة']);
  });

  it('names the dishes sold without a recipe, whose ingredients look like a shortfall', async () => {
    const { fixture } = await render();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Garlic Bread × 2');
  });
});
