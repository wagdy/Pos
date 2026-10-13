import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { ApprovalRequest, CostingApi, PrepItem } from '../../core/costing/costing.api';
import { PrepItemsPage } from './prep-items-page';

function prep(overrides: Partial<PrepItem>): PrepItem {
  return {
    id: 'p',
    code: null,
    name: 'Prep',
    category: null,
    categoryId: 'general',
    unit: 'Gram',
    displayUnit: 'kg',
    displayUnitSize: 1000,
    reorderLevel: 0,
    batchQuantity: 10_000,
    ingredients: [],
    standardBatchCost: null,
    standardCostPerDisplayUnit: null,
    missingPrices: [],
    quantityOnHand: 0,
    averageCostPerDisplayUnit: null,
    stockValue: null,
    lastProducedAtUtc: null,
    lastBatchCostPerDisplayUnit: null,
    recent: [],
    recipeCategoryId: null,
    ...overrides,
  };
}

const line = { rawMaterialId: 'flour', code: null, name: 'Flour', isPrepItem: false, unit: 'Gram' as const, purchaseUnit: 'kg', purchaseUnitSize: 1000, quantity: 6_000, yieldPercent: 100, asPurchasedQuantity: 6_000, costPerPurchaseUnit: 20, batchCost: 120, sharePercent: 100 };
const dough = prep({
  id: 'dough', code: 'PREP-1', name: 'Pizza dough', ingredients: [line], standardCostPerDisplayUnit: 18.2, averageCostPerDisplayUnit: 18.96,
  quantityOnHand: 9_600, stockValue: 182, lastProducedAtUtc: '2026-10-07T08:00:00Z',
});
const sauce = prep({ id: 'sauce', name: 'House sauce', unit: 'Millilitre', displayUnit: 'L', batchQuantity: 5_000, ingredients: [line], missingPrices: ['Basil'], quantityOnHand: -500 });
const glaze = prep({ id: 'glaze', name: 'Glaze' });

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

// Mona's requests: a new prep item, and a change to the dough.
function asked(overrides: Partial<ApprovalRequest>): ApprovalRequest {
  return {
    id: 'q', module: 'PrepItem', action: 'Create', recordId: 'new', summary: 'New prep item Garlic oil · 1 L batch · 2 ingredients', changes: [],
    reason: null, requestedBy: 'Mona', requestedAtUtc: '2026-10-12T09:00:00Z', status: 'Pending', decidedBy: null, decidedAtUtc: null,
    decisionNote: null, ...overrides,
  };
}

describe('the prep items', () => {
  async function render(pending: ApprovalRequest[] = []) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: { prepItems: () => of([dough, sauce, glaze]), approvalRequests: () => of({ pending, decided: [] }), recipeCategories: () => of([]) },
        },
        { provide: AuthService, useValue: { can: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(PrepItemsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  it('lists each with its batch, its cost by recipe and in stock, and what is on the shelf', async () => {
    const { el } = await render();

    expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 6).map(text))).toEqual([
      ['PREP-1 · Pizza dough', '10 kg', 'L.E 18.20 / kg', 'L.E 18.96 / kg', '9.6 kg', 'L.E 182.00'],
      ['House sauce No cost yet: Basil', '5 L', '—', '—', '-0.5 L', '—'],
      ['Glaze No recipe yet', '10 kg', '—', '—', '0 kg', '—'],
    ]);
  });

  it('lists the new ones waiting for a manager first, and marks one with a change asked for', async () => {
    const { el } = await render([asked({}), asked({ id: 'q2', action: 'Update', recordId: 'dough', summary: 'Prep item PREP-1 · Pizza dough' })]);
    const rows = [...el.querySelectorAll('tbody tr')];

    expect(rows.length).toBe(4);
    expect(text(rows[0])).toContain('New prep item Garlic oil');
    expect(text(rows[0])).toContain('Waiting for approval');
    expect(rows[0].classList).not.toContain('clickable');
    expect(text(rows[1])).toContain('Change requested');
    expect(rows[1].classList).toContain('pending-deletion');
  });

  it('finds one by name or code, and opens it', async () => {
    const { page } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    page['search'].set('prep-1');
    expect(page['shown']()).toEqual([dough]);
    page['open'](dough);
    expect(navigate).toHaveBeenCalledWith(['/costing/recipes/prep', 'dough']);
  });
});
