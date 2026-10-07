import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, MaterialCost, StockCount, countUnit } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { CountSheetPage } from './count-sheet-page';

const material = (id: string, unit: MaterialCost['unit'], purchaseUnitSize: number, category: string | null = null) =>
  ({ id, name: id, code: null, category, unit, purchaseUnitSize }) as MaterialCost;

const beef = material('beef', 'Gram', 1000, 'Meat');
const saffron = material('saffron', 'Gram', 1, 'Spices');
const bun = material('bun', 'Piece', 1, 'Bakery');
const oil = material('oil', 'Millilitre', 1000);

describe('counting the stores', () => {
  const stockCount = vi.fn();
  const saveStockCount = vi.fn();
  const postStockCount = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { stockCount, saveStockCount, postStockCount, materials: () => of([beef, saffron, bun, oil]) } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(CountSheetPage);
    fixture.componentRef.setInput('id', 'count-1');
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    stockCount.mockReset().mockReturnValue(of(null));
    saveStockCount.mockReset().mockImplementation((id: string, lines: StockCount['lines']) =>
      of({ id, status: 'Draft', startedAtUtc: '', startedBy: 'Omar', postedAtUtc: null, postedBy: null, lines }),
    );
    postStockCount.mockReset().mockReturnValue(of({ id: 'count-1', status: 'Posted', lines: [] }));
  });

  afterEach(() => vi.useRealTimers());

  it('counts weights in kg, but what is bought by the gram in grams', () => {
    expect(countUnit('Gram', 1000)).toEqual({ label: 'kg', size: 1000 });
    expect(countUnit('Gram', 1)).toEqual({ label: 'g', size: 1 });
    expect(countUnit('Millilitre', 1000)).toEqual({ label: 'L', size: 1000 });
    expect(countUnit('Piece', 30)).toEqual({ label: 'pc', size: 1 });
  });

  it('saves itself a moment after the last entry, in the ledger units, leaving empty fields out', async () => {
    const { page } = await render();
    // From here on: the page has rendered, and the autosave waits on a timer.
    vi.useFakeTimers();

    page['enter']('beef', 14.76);
    page['enter']('saffron', 2.5);
    page['enter']('bun', 0);
    page['enter']('oil', '');
    expect(saveStockCount).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(1600);

    // One save for the burst. 0 buns is counted at none; the oil left empty is not counted.
    expect(saveStockCount).toHaveBeenCalledTimes(1);
    expect(saveStockCount).toHaveBeenCalledWith('count-1', [
      { rawMaterialId: 'beef', countedQuantity: 14760 },
      { rawMaterialId: 'saffron', countedQuantity: 2.5 },
      { rawMaterialId: 'bun', countedQuantity: 0 },
    ]);
    expect(page['countedCount']()).toBe(3);
  });

  it('saves what is not saved yet before posting, so nothing typed is lost', async () => {
    const { page } = await render();

    page['enter']('beef', 10);
    await page['post']();

    expect(saveStockCount).toHaveBeenCalledWith('count-1', [{ rawMaterialId: 'beef', countedQuantity: 10000 }]);
    expect(postStockCount).toHaveBeenCalledWith('count-1');
    expect(saveStockCount.mock.invocationCallOrder[0]).toBeLessThan(postStockCount.mock.invocationCallOrder[0]);
  });

  it('shows a posted count against what the records said, and what the difference was worth', async () => {
    stockCount.mockReturnValue(
      of({
        id: 'count-1',
        status: 'Posted',
        startedAtUtc: '2026-10-07T07:00:00Z',
        startedBy: 'Omar',
        postedAtUtc: '2026-10-07T07:30:00Z',
        postedBy: 'Omar',
        lines: [{ rawMaterialId: 'beef', countedQuantity: 18900, bookQuantity: 19280, unitCost: 0.42 }],
      } satisfies StockCount),
    );
    const { fixture } = await render();

    const row = (fixture.nativeElement as HTMLElement).querySelector('tbody tr')!;
    expect(row.classList).toContain('deficit');
    expect([...row.querySelectorAll('td')].map((td) => td.textContent?.trim())).toEqual([
      '',
      'beef',
      '18.9 kg',
      '19.28 kg',
      '-0.38 kg',
      'L.E -159.60',
    ]);
    expect((fixture.nativeElement as HTMLElement).querySelector('input')).toBeNull();
  });
});
