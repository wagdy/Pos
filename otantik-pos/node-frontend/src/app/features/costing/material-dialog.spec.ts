import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi, MaterialCost, MaterialUsage } from '../../core/costing/costing.api';
import { Approvals } from './approvals';
import { MaterialDialog } from './material-dialog';

// Tomatoes counted in grams, bought by the kg at 30, 2.4 kg on hand, reordered at 1 kg.
const tomatoes: MaterialCost = {
  id: 'tomatoes',
  code: null,
  name: 'Tomatoes',
  category: 'Vegetables',
  categoryId: 'veg',
  unit: 'Gram',
  purchaseUnit: 'kg',
  purchaseUnitSize: 1000,
  costPerPurchaseUnit: 30,
  defaultYieldPercent: 100,
  quantityOnHand: 2400,
  reorderLevel: 1000,
  stockValue: 72,
  lastPurchaseCostPerPurchaseUnit: 30,
  lastPurchasedAtUtc: null,
  costPerUnit: 0.03,
  kind: 'Purchased',
};

const recorded: MaterialUsage = { stockMovements: 3, invoiceLines: 1, recipes: 1, prepRecipes: 0, counts: 1, batches: 0, pendingRequests: 0, inUse: true };
const unused: MaterialUsage = { stockMovements: 0, invoiceLines: 0, recipes: 0, prepRecipes: 0, counts: 0, batches: 0, pendingRequests: 0, inUse: false };

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the material dialog', () => {
  const materialUsage = vi.fn();
  const close = vi.fn();
  const canApprove = signal(true);

  async function render(data: MaterialCost | null) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { materialUsage, purchaseCategories: () => of([]) } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: Approvals, useValue: { canApprove } },
      ],
    });
    const fixture = TestBed.createComponent(MaterialDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    const page = fixture.componentInstance as unknown as Record<string, any>;
    const el = fixture.nativeElement as HTMLElement;
    // A unit chosen, as the select does, and the screen brought up to date.
    async function choose(unit: string) {
      await page['unitChanged'](unit);
      page['form'].controls.unit.setValue(unit);
      fixture.detectChanges();
      await fixture.whenStable();
    }
    function type(value: number) {
      const input = el.querySelector<HTMLInputElement>('.conversion input')!;
      input.value = String(value);
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }
    const submit = () => el.querySelector<HTMLButtonElement>('button[type=submit]')!;
    return { fixture, page, el, choose, type, submit };
  }

  beforeEach(() => {
    materialUsage.mockReset().mockReturnValue(of(recorded));
    close.mockReset();
    canApprove.set(true);
  });

  it('saves a new material in the unit chosen, with no unit change', async () => {
    const { page } = await render(null);
    page['form'].patchValue({ name: 'Lemons' });
    await page['unitChanged']('Piece');
    page['form'].controls.unit.setValue('Piece');

    page['save']();

    expect(close).toHaveBeenCalledWith({
      material: expect.objectContaining({ name: 'Lemons', unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1 }),
      unitChange: null,
    });
    expect(materialUsage).not.toHaveBeenCalled();
  });

  it('lets a bought material change the unit it is counted in, but not a prep item', async () => {
    const bought = await render(tomatoes);
    expect(bought.page['form'].controls.unit.enabled).toBe(true);
    TestBed.resetTestingModule();

    const prep = await render({ ...tomatoes, kind: 'Prep' });
    expect(prep.page['form'].controls.unit.disabled).toBe(true);
    expect(text(prep.el)).toContain('A prep item keeps its unit');
  });

  it('starts a material nothing records again in the new unit, its price and reorder level given for it', async () => {
    materialUsage.mockReturnValue(of(unused));
    const { page, el, choose, submit } = await render(tomatoes);

    await choose('Piece');

    expect(text(el)).toContain('Nothing records Tomatoes yet: it is counted in pieces from now.');
    expect(el.querySelector('.conversion')).toBeNull();
    expect(page['form'].getRawValue()).toMatchObject({ purchaseUnit: 'piece', purchaseUnitSize: 1, cost: null, reorderAt: 0 });
    page['form'].patchValue({ cost: 3, reorderAt: 20 });
    expect(submit().disabled).toBe(false);

    page['save']();

    expect(close).toHaveBeenCalledWith({
      material: { name: 'Tomatoes', code: null, categoryId: 'veg', defaultYieldPercent: 100, purchaseUnit: 'kg', purchaseUnitSize: 1000, costPerPurchaseUnit: null, reorderLevel: 1000 },
      unitChange: expect.objectContaining({ unit: 'Piece', oneNewIsOld: null, purchaseUnit: 'piece', purchaseUnitSize: 1, costPerPurchaseUnit: 3, reorderLevel: 20, reason: null }),
    });
  });

  it('asks the conversion of a recorded material and shows what it restates', async () => {
    const { page, el, choose, type, submit } = await render(tomatoes);

    await choose('Piece');

    expect(text(el)).toContain('Tomatoes is already recorded in g: 3 stock movements, 1 invoice line, 1 recipe, 1 stock count.');
    expect(text(el.querySelector('.conversion mat-label')!)).toBe('1 piece is how many g? · التحويل');
    // Without it, refused: archive and create a new one.
    expect(text(el.querySelector('.unit-change .error')!)).toContain('archive Tomatoes and create a new material counted in pieces');
    expect(submit().disabled).toBe(true);

    page['form'].patchValue({ purchaseUnit: 'crate', purchaseUnitSize: 100 });
    type(60);

    expect(text(el.querySelector('.restated')!)).toBe('On hand 2,400 g → 40 pieces · average cost L.E 0.03 a g → L.E 1.80 a piece');
    // The cost and reorder level follow: a crate of 100 at 1.80 is 180; 1 kg is 16.667 pieces.
    expect(page['form'].controls.cost.disabled).toBe(true);
    expect(page['form'].getRawValue()).toMatchObject({ cost: 180, reorderAt: 0.167 });
    expect(text(submit())).toBe('Save');
    expect(submit().disabled).toBe(false);

    page['save']();

    expect(close).toHaveBeenCalledWith({
      material: expect.objectContaining({ purchaseUnit: 'kg', purchaseUnitSize: 1000, costPerPurchaseUnit: null, reorderLevel: 1000 }),
      unitChange: expect.objectContaining({ unit: 'Piece', oneNewIsOld: 60, purchaseUnit: 'crate', purchaseUnitSize: 100, costPerPurchaseUnit: null, reorderLevel: null }),
    });
  });

  it('says the conversion of the larger unit whichever way it goes', async () => {
    const { page, el, choose, type } = await render({ ...tomatoes, name: 'Eggs', unit: 'Piece', purchaseUnit: 'carton', purchaseUnitSize: 30, quantityOnHand: 30, costPerUnit: 5 });

    await choose('Gram');
    expect(text(el.querySelector('.conversion mat-label')!)).toBe('1 piece is how many g? · التحويل');
    type(60);

    expect(page['oneNewIsOld']()).toBeCloseTo(1 / 60, 12);
    expect(text(el.querySelector('.restated')!)).toBe('On hand 30 pieces → 1,800 g · average cost L.E 5.00 a piece → L.E 0.0833 a g');
  });

  it('sends an accountant’s change for approval, with a reason', async () => {
    canApprove.set(false);
    const { page, el, choose, type, submit } = await render(tomatoes);

    await choose('Piece');
    type(60);
    page['form'].patchValue({ reason: ' Counted by the piece ' });
    expect(el.querySelector('textarea[formcontrolname=reason]')).not.toBeNull();
    expect(text(submit())).toBe('Send for approval');

    page['save']();

    expect(close).toHaveBeenCalledWith(expect.objectContaining({ unitChange: expect.objectContaining({ oneNewIsOld: 60, reason: 'Counted by the piece' }) }));
  });

  it('puts everything back when the unit it has is chosen again', async () => {
    const { page, el, choose, type } = await render(tomatoes);
    await choose('Piece');
    type(60);

    await choose('Gram');

    expect(el.querySelector('.unit-change')).toBeNull();
    expect(page['form'].controls.cost.enabled).toBe(true);
    expect(page['form'].getRawValue()).toMatchObject({ purchaseUnit: 'kg', purchaseUnitSize: 1000, cost: 30, reorderAt: 1, conversion: null });
    page['save']();
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ unitChange: null }));
  });

  it('will not change it while a request waiting for a manager names it, or when what records it is not known', async () => {
    materialUsage.mockReturnValue(of({ ...recorded, pendingRequests: 1 }));
    const pending = await render(tomatoes);
    await pending.choose('Piece');
    expect(text(pending.el.querySelector('.unit-change')!)).toBe(
      'A request waiting for a manager names Tomatoes: its unit can be changed once that is decided.',
    );
    expect(pending.submit().disabled).toBe(true);
    TestBed.resetTestingModule();

    materialUsage.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    const failed = await render(tomatoes);
    await failed.choose('Piece');
    expect(text(failed.el.querySelector('.unit-change')!)).toContain('could not be read');
    expect(failed.submit().disabled).toBe(true);
  });
});
