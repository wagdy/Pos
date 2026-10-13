import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest, CostingApi, PrepItem, ProductionOrder, ProductionPreview } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { ProductionPage } from './production-page';

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

const flourLine = { rawMaterialId: 'flour', code: null, name: 'Flour', isPrepItem: false, unit: 'Gram' as const, purchaseUnit: 'kg', purchaseUnitSize: 1000, quantity: 6_000, yieldPercent: 100, asPurchasedQuantity: 6_000, costPerPurchaseUnit: 20, batchCost: 120, sharePercent: 100 };
const dough = prep({ id: 'dough', name: 'Pizza dough', ingredients: [flourLine] });
// A prep item with no recipe yet cannot be made.
const sauce = prep({ id: 'sauce', name: 'House sauce' });

function preview(overrides: Partial<ProductionPreview> = {}): ProductionPreview {
  return {
    prepItemId: 'dough',
    prepItem: 'Pizza dough',
    unit: 'Gram',
    displayUnit: 'kg',
    displayUnitSize: 1000,
    plannedQuantity: 10_000,
    producedQuantity: 9_600,
    lines: [
      { rawMaterialId: 'flour', code: null, material: 'Flour', isPrepItem: false, unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000, quantity: 6_000, onHand: 4_000, short: 2_000, costPerPurchaseUnit: 20, cost: 120 },
    ],
    totalCost: 120,
    unitCostPerDisplayUnit: 12.5,
    missingPrices: [],
    shortages: ['Flour'],
    ...overrides,
  };
}

const made: ProductionOrder = {
  id: 'x',
  prepItemId: 'dough',
  prepItem: 'Pizza dough',
  unit: 'Gram',
  displayUnit: 'kg',
  displayUnitSize: 1000,
  plannedQuantity: 10_000,
  producedQuantity: 9_600,
  yieldPercent: 96,
  totalCost: 120,
  unitCostPerDisplayUnit: 12.5,
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

// Mona's batch, asked for and waiting for a manager: nothing made yet.
const asked: ApprovalRequest = {
  id: 'q1', module: 'ProductionOrder', action: 'Create', recordId: 'b1', summary: 'New batch of Pizza dough · 10 kg planned, 9.6 kg came out',
  changes: [], reason: null, requestedBy: 'Mona', requestedAtUtc: '2026-10-12T09:00:00Z', status: 'Pending', decidedBy: null, decidedAtUtc: null,
  decisionNote: null,
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('making a batch', () => {
  const prepItems = vi.fn();
  const productionOrders = vi.fn();
  const previewProduction = vi.fn();
  const makeProductionOrder = vi.fn();
  const reviseProductionOrder = vi.fn();
  const approvalRequests = vi.fn();
  const requestDeletion = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render(prepItemId?: string, manager = true) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: { prepItems, productionOrders, previewProduction, makeProductionOrder, reviseProductionOrder, approvalRequests, requestDeletion },
        },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: AuthService, useValue: { can: (p: string) => p !== Permissions.ApprovalDecide || manager } },
      ],
    });
    const fixture = TestBed.createComponent(ProductionPage);
    if (prepItemId) {
      fixture.componentRef.setInput('prepItemId', prepItemId);
    }
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  function choose(page: Record<string, any>, planned: number, produced: number) {
    page['prepItemId'] = 'dough';
    page['planned'] = planned;
    page['plannedChanged']();
    page['produced'] = produced;
    page['changed']();
  }

  beforeEach(() => {
    prepItems.mockReset().mockReturnValue(of([dough, sauce]));
    productionOrders.mockReset().mockReturnValue(of([]));
    previewProduction.mockReset().mockReturnValue(of(preview()));
    makeProductionOrder.mockReset().mockReturnValue(of(made));
    reviseProductionOrder.mockReset();
    approvalRequests.mockReset().mockReturnValue(of({ pending: [], decided: [] }));
    requestDeletion.mockReset();
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('offers the prep items that have a recipe, and starts from the one asked for with one batch', async () => {
    const { page } = await render('dough');

    expect(page['items']().map((p: PrepItem) => p.id)).toEqual(['dough']);
    expect([page['prepItemId'], page['planned'], page['produced']]).toEqual(['dough', 10, 10]);
  });

  it('reviews what the batch takes and costs, in grams, before anything moves', async () => {
    const { fixture, page } = await render();
    choose(page, 10, 9.6);
    expect(page['yieldPercent']()).toBe(96);

    await page['review']();
    fixture.detectChanges();

    expect(previewProduction).toHaveBeenCalledWith('dough', 10_000, 9_600);
    expect(makeProductionOrder).not.toHaveBeenCalled();
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el.querySelector('section tbody tr')!)).toContain('2,000 g short');
    expect(text(el.querySelector('section tfoot')!)).toContain('L.E 12.50');
    // Short on record: said, and it can still be posted.
    expect(text(el.querySelector('.caution')!)).toContain('The records show too little Flour');
    expect([...el.querySelectorAll('button')].find((b) => text(b).includes('Confirm'))!.disabled).toBe(false);
  });

  it('asks for a new review when anything changes after one', async () => {
    const { page } = await render();
    choose(page, 10, 9.6);
    await page['review']();

    page['produced'] = 9;
    page['changed']();

    expect(page['preview']()).toBeNull();
  });

  it('cannot post a batch with an ingredient that has no cost', async () => {
    previewProduction.mockReturnValue(of(preview({ missingPrices: ['Salt'], totalCost: null, unitCostPerDisplayUnit: null })));
    const { fixture, page } = await render();
    choose(page, 10, 10);

    await page['review']();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Salt has no cost yet, so the batch cannot be made');
    expect([...el.querySelectorAll('button')].find((b) => text(b).includes('Confirm'))!.disabled).toBe(true);
  });

  it('posts on Confirm, under one id however often it is tried, and a new id for the next batch', async () => {
    const { page } = await render();
    choose(page, 10, 9.6);
    page['notes'] = ' Morning ';
    await page['review']();

    makeProductionOrder.mockReturnValueOnce(throwError(() => new Error('lost')));
    await page['confirm']();
    expect(notify.error).toHaveBeenCalled();
    expect(page['preview']()).not.toBeNull();

    await page['confirm']();
    const [first, retry] = makeProductionOrder.mock.calls;
    expect(retry[0]).toBe(first[0]);
    expect(first[1]).toEqual({ prepItemId: 'dough', plannedQuantity: 10_000, producedQuantity: 9_600, notes: 'Morning', requestId: expect.any(String) });
    expect(retry[1].requestId).toBe(first[1].requestId);
    expect(notify.info).toHaveBeenCalledWith('Made 9.6 kg of Pizza dough, at 12.50 L.E a kg.');
    expect([page['planned'], page['produced'], page['preview']()]).toEqual([null, null, null]);
    expect(productionOrders).toHaveBeenCalledTimes(2);

    choose(page, 5, 5);
    await page['review']();
    await page['confirm']();
    expect(makeProductionOrder.mock.calls[2][0]).not.toBe(first[0]);
  });

  it("sends an accountant's batch for approval, moving nothing, and lists it as waiting", async () => {
    makeProductionOrder.mockReturnValue(of({ outcome: 'Requested', request: asked }));
    const { fixture, page } = await render(undefined, false);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('A manager approves batches: nothing moves in stock until then');
    choose(page, 10, 9.6);
    await page['review']();
    fixture.detectChanges();
    expect([...el.querySelectorAll('button')].map(text)).toContain('send Send for approval · إرسال للموافقة');

    approvalRequests.mockReturnValue(of({ pending: [asked], decided: [] }));
    await page['confirm']();
    fixture.detectChanges();

    expect(notify.info).toHaveBeenCalledWith('Submitted to the manager for approval. Nothing is made until it is approved.');
    expect([page['planned'], page['preview']()]).toEqual([null, null]);
    const waiting = el.querySelector('tbody tr.pending-deletion')!;
    expect(text(waiting)).toContain('New batch of Pizza dough');
    expect(text(waiting)).toContain('Waiting for approval');
    expect(text(waiting)).toContain('asked by Mona');
  });

  it('corrects a batch: the form holds it as it was, and the correction goes under a new batch id', async () => {
    productionOrders.mockReturnValue(of([made]));
    reviseProductionOrder.mockReturnValue(of({ outcome: 'Applied', request: { ...asked, action: 'Update', status: 'Approved' } }));
    const { fixture, page } = await render();

    page['startEditing'](made);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect([page['prepItemId'], page['planned'], page['produced']]).toEqual(['dough', 10, 9.6]);
    expect(text(el.querySelector('section')!)).toContain('Correct a batch');
    expect(text(el.querySelector('.pending-banner')!)).toContain('reverses the batch and makes it again');

    page['produced'] = 9.2;
    page['changed']();
    page['reason'] = 'Weighed again';
    await page['review']();
    await page['confirm']();

    const [orderId, body] = reviseProductionOrder.mock.lastCall!;
    expect(orderId).toBe('x');
    expect(body).toEqual({
      plannedQuantity: 10_000, producedQuantity: 9_200, notes: null, requestId: expect.any(String), newOrderId: expect.any(String), reason: 'Weighed again',
    });
    expect(makeProductionOrder).not.toHaveBeenCalled();
    expect(notify.info).toHaveBeenCalledWith('Corrected: the batch of Pizza dough is reversed and made again.');
    expect(page['editing']()).toBeNull();
  });

  it('locks a batch waiting for a decision, and marks one voided or corrected', async () => {
    productionOrders.mockReturnValue(of([
      made,
      { ...made, id: 'old', status: 'Voided', replacedBy: 'x', voidedBy: 'Omar', voidedAtUtc: '2026-10-08T08:00:00Z' },
    ]));
    approvalRequests.mockReturnValue(of({ pending: [{ ...asked, action: 'Delete', recordId: 'x' }], decided: [] }));
    const { fixture } = await render();
    await fixture.whenStable();
    fixture.detectChanges();
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')];

    expect(text(rows[0])).toContain('Void requested');
    expect(rows[0].querySelector('button[aria-label^="Correct"]')).toBeNull();
    expect(rows[0].querySelector('button[aria-label^="Delete"]')).toBeNull();
    expect(text(rows[1])).toContain('Corrected');
    expect(rows[1].querySelector('button[aria-label^="Correct"]')).toBeNull();
  });

  it('will not review without a prep item and quantities', async () => {
    const { page } = await render();

    expect(page['ready']()).toBe(false);
    await page['review']();
    expect(previewProduction).not.toHaveBeenCalled();
  });
});
