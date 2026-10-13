import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest, CostingApi, MaterialCost, PrepItem, ProductionOrder, RecipeCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { PrepItemPage } from './prep-item-page';
import { ProductionOrderDialog } from './production-order-dialog';

function material(overrides: Partial<MaterialCost>): MaterialCost {
  return {
    id: 'm',
    code: null,
    name: 'Material',
    category: null,
    categoryId: 'general',
    unit: 'Gram',
    purchaseUnit: 'kg',
    purchaseUnitSize: 1000,
    costPerPurchaseUnit: null,
    defaultYieldPercent: 100,
    quantityOnHand: 0,
    reorderLevel: 0,
    stockValue: null,
    lastPurchaseCostPerPurchaseUnit: null,
    lastPurchasedAtUtc: null,
    costPerUnit: null,
    kind: 'Purchased',
    ...overrides,
  };
}

// 20 a kg of flour, 500 a kg of yeast, 60 a litre of oil.
const flour = material({ id: 'flour', name: 'Flour', costPerPurchaseUnit: 20, costPerUnit: 0.02 });
const yeast = material({ id: 'yeast', name: 'Yeast', costPerPurchaseUnit: 500, costPerUnit: 0.5 });
const oil = material({ id: 'oil', name: 'Olive oil', unit: 'Millilitre', purchaseUnit: 'L', costPerPurchaseUnit: 60, costPerUnit: 0.06 });
const salt = material({ id: 'salt', name: 'Salt' });
const doughMaterial = material({ id: 'dough', name: 'Pizza dough', kind: 'Prep', costPerPurchaseUnit: 18.96, costPerUnit: 0.01896 });

const batch: ProductionOrder = {
  id: 'batch-1',
  prepItemId: 'dough',
  prepItem: 'Pizza dough',
  unit: 'Gram',
  displayUnit: 'kg',
  displayUnitSize: 1000,
  plannedQuantity: 10_000,
  producedQuantity: 9_600,
  yieldPercent: 96,
  totalCost: 182,
  unitCostPerDisplayUnit: 18.96,
  producedAtUtc: '2026-10-07T08:00:00Z',
  producedBy: 'Omar',
  notes: null,
  lines: [],
  status: 'Made',
  voidedAtUtc: null,
  voidedBy: null,
  revisionOf: null,
  replacedBy: null,
  approvedBy: null,
};

const dough: PrepItem = {
  recipeCategoryId: null,
  id: 'dough',
  code: 'PREP-1',
  name: 'Pizza dough',
  category: 'Doughs',
  categoryId: 'doughs',
  unit: 'Gram',
  displayUnit: 'kg',
  displayUnitSize: 1000,
  reorderLevel: 2_000,
  batchQuantity: 10_000,
  ingredients: [
    { rawMaterialId: 'flour', code: null, name: 'Flour', isPrepItem: false, unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000, quantity: 6_000, yieldPercent: 100, asPurchasedQuantity: 6_000, costPerPurchaseUnit: 20, batchCost: 120, sharePercent: 65.9 },
    { rawMaterialId: 'yeast', code: null, name: 'Yeast', isPrepItem: false, unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000, quantity: 100, yieldPercent: 100, asPurchasedQuantity: 100, costPerPurchaseUnit: 500, batchCost: 50, sharePercent: 27.5 },
    { rawMaterialId: 'oil', code: null, name: 'Olive oil', isPrepItem: false, unit: 'Millilitre', purchaseUnit: 'L', purchaseUnitSize: 1000, quantity: 200, yieldPercent: 100, asPurchasedQuantity: 200, costPerPurchaseUnit: 60, batchCost: 12, sharePercent: 6.6 },
  ],
  standardBatchCost: 182,
  standardCostPerDisplayUnit: 18.2,
  missingPrices: [],
  quantityOnHand: 9_600,
  averageCostPerDisplayUnit: 18.96,
  stockValue: 182,
  lastProducedAtUtc: '2026-10-07T08:00:00Z',
  lastBatchCostPerDisplayUnit: 18.96,
  recent: [batch],
};

const recipeCategories: RecipeCategory[] = [{ id: 'doughs', name: 'Doughs', nameAr: 'عجين', recipes: 0, prepItems: 1 }];

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('a prep item', () => {
  const materials = vi.fn();
  const prepItem = vi.fn();
  const savePrepItem = vi.fn();
  const approvalRequests = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render(id: string, manager = true) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { materials, prepItem, savePrepItem, approvalRequests, recipeCategories: () => of(recipeCategories) } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: AuthService, useValue: { can: (p: string) => p !== Permissions.ApprovalDecide || manager } },
      ],
    });
    const fixture = TestBed.createComponent(PrepItemPage);
    fixture.componentRef.setInput('id', id);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    materials.mockReset().mockReturnValue(of([flour, yeast, oil, salt, doughMaterial]));
    prepItem.mockReset().mockReturnValue(of(dough));
    savePrepItem.mockReset().mockImplementation((id: string | null) => of({ ...dough, id: id ?? 'new-dough' }));
    approvalRequests.mockReset().mockReturnValue(of({ pending: [], decided: [] }));
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('shows what it costs by its recipe, what the batches on the shelf cost, and how much is there', async () => {
    const { el } = await render('dough');

    expect([...el.querySelectorAll('.box')].map((b) => text(b.querySelector('strong')!))).toEqual([
      'L.E 18.20 / kg',
      'L.E 18.96 / kg',
      '9.6 kg',
      'L.E 18.96 / kg',
    ]);
    expect(text(el.querySelector('.box.key small')!)).toBe('One batch of 10 kg: L.E 182.00');
    expect(text(el)).toContain('Worth L.E 182.00');
  });

  it('breaks the batch down: each ingredient, what it takes from stock, its cost, and its share', async () => {
    const { el } = await render('dough');

    const rows = [...el.querySelectorAll('table.grid')][0].querySelectorAll('tbody tr');
    expect([...rows].map((r) => [...r.querySelectorAll('td')].slice(3, 7).map(text))).toEqual([
      ['6,000 g', 'L.E 20.00 / kg', 'L.E 120.00', '65.9%'],
      ['100 g', 'L.E 500.00 / kg', 'L.E 50.00', '27.5%'],
      ['200 ml', 'L.E 60.00 / L', 'L.E 12.00', '6.6%'],
    ]);
  });

  it('costs the recipe again as it is typed, trim included', async () => {
    const { page } = await render('dough');

    // 6.4 kg of flour, at 80% usable: 8 kg off the shelf, 160.
    page['lines']()[0].quantity = 6_400;
    page['lines']()[0].yieldPercent = 80;
    page['touch']();

    expect(page['asPurchased'](page['lines']()[0])).toBe(8_000);
    expect(page['batchCost']()).toBe(222);
    expect(page['standardPerUnit']()).toBeCloseTo(22.2, 10);
  });

  it('says which ingredient has no cost yet', async () => {
    const { fixture, page } = await render('dough');

    page['lines'].update((lines: unknown[]) => [...lines, { rawMaterialId: 'salt', quantity: 150, yieldPercent: 100 }]);
    page['touch']();
    fixture.detectChanges();

    expect(page['missing']()).toEqual(['Salt']);
    expect(text(fixture.nativeElement.querySelector('p.warn'))).toContain('No cost yet for Salt');
  });

  it('is not offered as its own ingredient', async () => {
    const { page } = await render('dough');

    expect(page['ingredientChoices']().map((m: MaterialCost) => m.id)).toEqual(['flour', 'yeast', 'oil', 'salt']);
  });

  it('is added with its batch, reorder level in grams and recipe category, and opened once saved', async () => {
    const { page } = await render('new');
    expect(page['recipeCategories']()).toEqual(recipeCategories);
    page['recipeCategoryId'] = 'doughs';
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    expect(prepItem).not.toHaveBeenCalled();
    page['name'] = ' Pizza dough ';
    page['batch'] = 10;
    page['reorder'] = 2.5;
    page['lines'].set([
      { rawMaterialId: 'flour', quantity: 6_000, yieldPercent: 100 },
      { rawMaterialId: 'yeast', quantity: 100, yieldPercent: null },
    ]);
    page['touch']();

    await page['save']();

    expect(savePrepItem).toHaveBeenCalledWith(null, {
      name: 'Pizza dough',
      unit: 'Gram',
      batchQuantity: 10_000,
      ingredients: [
        { rawMaterialId: 'flour', quantity: 6_000, yieldPercent: 100 },
        { rawMaterialId: 'yeast', quantity: 100, yieldPercent: null },
      ],
      code: null,
      categoryId: null,
      recipeCategoryId: 'doughs',
      displayUnit: 'kg',
      displayUnitSize: 1000,
      reorderLevel: 2_500,
      requestId: expect.any(String),
    });
    expect(navigate).toHaveBeenCalledWith(['/costing/recipes/prep', 'new-dough'], { replaceUrl: true });
  });

  it("sends an accountant's new prep item for approval: nothing is added, and the list shows it waiting", async () => {
    const asked: ApprovalRequest = {
      id: 'q1', module: 'PrepItem', action: 'Create', recordId: 'd2', summary: 'New prep item Pizza dough · 10 kg batch · 1 ingredient',
      changes: [], reason: null, requestedBy: 'Mona', requestedAtUtc: '2026-10-12T09:00:00Z', status: 'Pending', decidedBy: null,
      decidedAtUtc: null, decisionNote: null,
    };
    savePrepItem.mockReturnValue(of({ outcome: 'Requested', request: asked }));
    const { el, page } = await render('new', false);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    expect(text(el)).toContain('A manager approves new prep items: it is not added until then.');
    expect([...el.querySelectorAll('button')].map(text)).toContain('send Send for approval · إرسال للموافقة');

    page['name'] = 'Pizza dough';
    page['lines'].set([{ rawMaterialId: 'flour', quantity: 6_000, yieldPercent: 100 }]);
    page['touch']();
    await page['save']();

    expect(notify.info).toHaveBeenCalledWith('Submitted to the manager for approval. Nothing is made until it is approved.');
    expect(navigate).toHaveBeenCalledWith(['/costing/recipes/prep']);
  });

  it('waiting for a manager to decide a change, shows what was asked and cannot be saved', async () => {
    approvalRequests.mockReturnValue(of({
      pending: [{
        id: 'q2', module: 'PrepItem', action: 'Update', recordId: dough.id, summary: 'Prep item Pizza dough',
        changes: [{ label: 'Yeast', before: '100 g', after: '150 g' }], reason: null, requestedBy: 'Mona',
        requestedAtUtc: '2026-10-12T09:00:00Z', status: 'Pending', decidedBy: null, decidedAtUtc: null, decisionNote: null,
      }],
      decided: [],
    }));
    const { fixture, el } = await render(dough.id, false);
    await fixture.whenStable();
    fixture.detectChanges();

    const banner = text(el.querySelector('.pending-banner')!);
    expect(banner).toContain('Mona asked to change this prep item');
    expect(banner).toContain('Yeast: 100 g → 150 g');
    expect([...el.querySelectorAll('button')].map(text).filter((b) => b.includes('Save') || b.includes('Send') || b.includes('Delete'))).toEqual([]);
  });

  it('is kept in litres when made by volume', async () => {
    const { page } = await render('new');

    page['unitChanged']('Millilitre');

    expect([page['displayUnit'], page['displayUnitSize']]).toEqual(['L', 1000]);
  });

  it('saves its changes under its own id', async () => {
    const { page } = await render('dough');
    page['batch'] = 12;
    page['touch']();

    await page['save']();

    expect(savePrepItem).toHaveBeenCalledWith('dough', expect.objectContaining({ batchQuantity: 12_000, reorderLevel: 2_000 }));
  });

  it('will not save without a name, a batch, or with an ingredient twice', async () => {
    const { page } = await render('new');
    page['lines'].set([{ rawMaterialId: 'flour', quantity: 6_000, yieldPercent: 100 }]);
    page['touch']();
    expect(page['ready']()).toBe(false);

    page['name'] = 'Dough';
    page['touch']();
    expect(page['ready']()).toBe(true);

    page['lines'].set([
      { rawMaterialId: 'flour', quantity: 6_000, yieldPercent: 100 },
      { rawMaterialId: 'flour', quantity: 100, yieldPercent: 100 },
    ]);
    page['touch']();
    expect(page['ready']()).toBe(false);
  });

  it('lists the batches made, each shown read-only', async () => {
    const { el } = await render('dough');

    const history = [...el.querySelectorAll('table.grid')][1];
    expect([...history.querySelectorAll('tbody tr td')].slice(1, 7).map(text)).toEqual(['10 kg', '9.6 kg', '96%', 'L.E 182.00', 'L.E 18.96', 'Omar']);
    history.querySelector<HTMLButtonElement>('button')!.click();
    expect(open).toHaveBeenCalledWith(ProductionOrderDialog, expect.objectContaining({ data: batch }));
  });
});
