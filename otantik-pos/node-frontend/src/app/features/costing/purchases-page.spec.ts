import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { CostingApi, MaterialCost } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { PurchasesPage } from './purchases-page';

const oil = { id: 'oil', name: 'Frying oil', unit: 'Millilitre', purchaseUnit: 'L', purchaseUnitSize: 1000 } as MaterialCost;
const bun = { id: 'bun', name: 'Burger bun', unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1 } as MaterialCost;

describe('receiving a delivery', () => {
  const receivePurchase = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { receivePurchase, materials: () => of([oil, bun]) } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(PurchasesPage);
    await fixture.whenStable();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  function fill(page: Record<string, any>, lines: { rawMaterialId: string; quantity: number; cost: number | null }[]) {
    page['lines'].set(lines.map((l) => ({ ...l })));
    page['touch']();
  }

  beforeEach(() => receivePurchase.mockReset().mockReturnValue(of([])));

  it('books it in the materials own units, as bought: 20 L is 20,000 ml', async () => {
    const page = await render();
    fill(page, [
      { rawMaterialId: 'oil', quantity: 20, cost: 1900 },
      { rawMaterialId: 'bun', quantity: 50, cost: null },
    ]);

    expect(page['total']()).toBe(1900);
    await page['save']();

    expect(receivePurchase).toHaveBeenCalledWith(expect.any(String), [
      { rawMaterialId: 'oil', quantity: 20000, cost: 1900 },
      { rawMaterialId: 'bun', quantity: 50, cost: null },
    ]);
  });

  it('sends a retry under the same delivery id, and the next delivery under a new one', async () => {
    const page = await render();
    fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);

    receivePurchase.mockReturnValueOnce(throwError(() => new Error('lost')));
    await page['save']();
    await page['save']();
    const [first, retry] = receivePurchase.mock.calls.map((call) => call[0]);
    expect(retry).toBe(first);

    fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);
    await page['save']();
    expect(receivePurchase.mock.calls[2][0]).not.toBe(first);
  });

  it('will not send a line without a material or a quantity, or a material twice', async () => {
    const page = await render();

    fill(page, [{ rawMaterialId: 'oil', quantity: 0, cost: 10 }]);
    expect(page['ready']()).toBe(false);
    fill(page, [
      { rawMaterialId: 'oil', quantity: 1, cost: 10 },
      { rawMaterialId: 'oil', quantity: 2, cost: 20 },
    ]);
    expect(page['ready']()).toBe(false);

    await page['save']();
    expect(receivePurchase).not.toHaveBeenCalled();
  });
});
