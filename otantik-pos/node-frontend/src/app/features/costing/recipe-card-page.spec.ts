import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, MaterialCost, RecipeCategory, RecipeCostCard, RecipeCostLine } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { RecipeCardMode, RecipeCardPage } from './recipe-card-page';

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

// Beef bought at 400 then 450 a kg: an average of 416.666667 a gram-thousand, shown as 416.67.
const beef = material({ id: 'beef', name: 'Beef mince', costPerPurchaseUnit: 416.67, costPerUnit: 0.416667 });
const bun = material({ id: 'bun', name: 'Burger bun', unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1, costPerPurchaseUnit: 6, costPerUnit: 6 });
const tomato = material({ id: 'tomato', name: 'Tomato', costPerPurchaseUnit: 35, costPerUnit: 0.035, defaultYieldPercent: 85 });
const saffron = material({ id: 'saffron', name: 'Saffron', purchaseUnit: 'g', purchaseUnitSize: 1 });

function card(overrides: Partial<RecipeCostCard>): RecipeCostCard {
  return {
    targetKind: 'MenuItem',
    catalogItemId: 6,
    variantId: null,
    itemCode: '6',
    recipe: 'Classic Cheeseburger',
    recipeAr: null,
    category: 'Mains',
    hasRecipe: false,
    portions: 1,
    menuPrice: 11.99,
    foodCostTargetPercent: 30,
    lines: [],
    costPerRecipe: 0,
    ingredientCostPerPortion: 0,
    sharedCostPerPortion: 0,
    costPerPortion: 0,
    marginPerPortion: null,
    foodCostPercentActual: null,
    idealSellingPrice: 0,
    missingPrices: [],
    status: 'NoRecipe',
    recipeId: null,
    recipeName: null,
    linkedTo: 'Classic Cheeseburger',
    isStandalone: false,
    plannedPrice: null,
    usesBaseRecipe: false,
    isDeleted: false,
    deletedAtUtc: null,
    deletedBy: null,
    recipeCategoryId: null,
    recipeCategory: null,
    recipeCategoryAr: null,
    ...overrides,
  };
}

const bunLine: RecipeCostLine = {
  rawMaterialId: 'bun', code: null, ingredient: 'Burger bun', quantity: 1, unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1,
  apsPerUnit: 6, yieldPercent: 100, epsPerUnit: 6, recipeCost: 6,
};

// The cheeseburger sells by recipe r1; garlic bread has none yet.
const cheeseburgerMenu = card({ hasRecipe: true, recipeId: 'r1', status: 'WithinTarget' });
const garlicBreadMenu = card({ catalogItemId: 1, itemCode: '1', recipe: 'Garlic Bread', linkedTo: 'Garlic Bread', menuPrice: 11.99 });
const mains: RecipeCategory = { id: 'mains', name: 'Mains', nameAr: 'أطباق رئيسية', recipes: 2 };
const sauces: RecipeCategory = { id: 'sauces', name: 'Sauces', nameAr: null, recipes: 0 };
const r1 = card({
  hasRecipe: true, recipeId: 'r1', recipeName: 'House burger', recipe: 'House burger', portions: 1, lines: [bunLine], status: 'WithinTarget',
  recipeCategoryId: 'mains', recipeCategory: 'Mains', recipeCategoryAr: 'أطباق رئيسية',
});
const trial = card({
  targetKind: null, catalogItemId: null, itemCode: '', recipe: 'Smash trial', recipeName: 'Smash trial', linkedTo: null, isStandalone: true,
  hasRecipe: true, recipeId: 'r2', category: 'Mains', plannedPrice: 60, menuPrice: 60, lines: [bunLine], status: 'WithinTarget',
  recipeCategoryId: 'mains', recipeCategory: 'Mains',
});

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('a recipe', () => {
  const recipeCard = vi.fn();
  const recipeCardById = vi.fn();
  const writeRecipe = vi.fn();
  const deleteRecipe = vi.fn();
  const requestDeletion = vi.fn();
  const approvalRequests = vi.fn();
  const restoreRecipe = vi.fn();
  const recipeCategories = vi.fn();
  const open = vi.fn();
  const can = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  let navigate: ReturnType<typeof vi.fn>;

  async function render(mode: RecipeCardMode, inputs: Record<string, string> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            recipeCard,
            recipeCardById,
            writeRecipe,
            deleteRecipe,
            restoreRecipe,
            requestDeletion,
            approvalRequests,
            materials: () => of([beef, bun, tomato, saffron]),
            menu: () => of([cheeseburgerMenu, garlicBreadMenu]),
            recipeCategories,
          },
        },
        { provide: MatDialog, useValue: { open } },
        { provide: AuthService, useValue: { can } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true) as unknown as ReturnType<typeof vi.fn>;
    const fixture = TestBed.createComponent(RecipeCardPage);
    fixture.componentRef.setInput('mode', mode);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    await fixture.whenStable();
    fixture.detectChanges();
    // The card's figures, by their English label.
    const figures = () =>
      Object.fromEntries(
        [...(fixture.nativeElement as HTMLElement).querySelectorAll('.box')].map((box) => [
          box.querySelector('.label')?.firstChild?.textContent?.trim(),
          box.querySelector('strong')?.textContent?.trim(),
        ]),
      );
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any>, figures };
  }

  // Ingredient lines, typed in.
  function write(page: Record<string, any>, lines: [string, number][]) {
    page['lines'].set([]);
    for (const [id, quantity] of lines) {
      page['add']();
      const i = page['lines']().length - 1;
      page['choose'](i, id);
      page['change'](i, { quantity });
    }
  }

  beforeEach(() => {
    recipeCard.mockReset();
    recipeCardById.mockReset().mockImplementation((id: string) => of(id === 'r1' ? r1 : trial));
    writeRecipe.mockReset().mockImplementation((id: string | null) => of({ id: id ?? 'r9' }));
    deleteRecipe.mockReset().mockReturnValue(of(undefined));
    requestDeletion.mockReset().mockReturnValue(of({ outcome: 'Applied', request: {} }));
    approvalRequests.mockReset().mockReturnValue(of({ pending: [], decided: [] }));
    restoreRecipe.mockReset().mockReturnValue(of({ id: 'r1', catalogItemId: 6 }));
    recipeCategories.mockReset().mockReturnValue(of([mains, sauces]));
    open.mockReset();
    can.mockReset().mockReturnValue(true);
    notify.info.mockReset();
    notify.error.mockReset();
  });

  describe('written new', () => {
    it('is worked out as the template does, as it is typed', async () => {
      const { fixture, page, figures } = await render('new', { targetKind: 'MenuItem', catalogItemId: '1' });

      page['portions'].set(4);
      write(page, [['beef', 480], ['bun', 4], ['tomato', 120]]);
      fixture.detectChanges();

      // 480 g × 416.67 = 200.00; 4 × 6 = 24.00; tomato at 85% yield: 35 ÷ 0.85 = 41.18 a kg, × 0.12 = 4.94.
      expect(page['costed']().map((l: { cost: number }) => l.cost)).toEqual([200, 24, 4.94]);
      expect(figures()).toMatchObject({
        'Cost per recipe': 'L.E 228.94',
        'Cost per portion': 'L.E 57.24',
        'Margin per portion': 'L.E -45.25',
        'Food cost % actual': '477.4%',
        // From the unrounded 57.235, as the server and the template work it: not 57.24 ÷ 30%.
        'Ideal selling price': 'L.E 190.78',
      });
      expect(page['status']()).toBe('AboveTarget');
    });

    it('works from the exact average cost, so a big batch is not cents out', async () => {
      const { page } = await render('new');

      write(page, [['beef', 10_000]]);

      // 10 kg at 416.666… is 4,166.67; from the rounded 416.67 it would be 4,166.70.
      expect(page['costPerRecipe']()).toBe(4166.67);
    });

    it('says so when an ingredient has no price, rather than costing it at nothing', async () => {
      const { fixture, page } = await render('new');

      write(page, [['saffron', 1]]);
      fixture.detectChanges();

      expect(page['status']()).toBe('MissingPrices');
      expect(text(fixture.nativeElement)).toContain('No price yet for Saffron');
    });

    it('will not save an ingredient on two lines', async () => {
      const { page } = await render('new');
      page['name'].set('Double');

      write(page, [['beef', 100], ['beef', 50]]);

      expect(page['duplicate']()).toBe(true);
      expect(page['valid']()).toBe(false);
      await page['save']();
      expect(writeRecipe).not.toHaveBeenCalled();
    });

    it('is written off the menu, named, with a planned price, and opened once created', async () => {
      const { fixture, page, figures } = await render('new');
      expect(text(fixture.nativeElement.querySelector('.link')!)).toContain('Standalone / Off-menu');

      write(page, [['tomato', 120]]);
      page['portions'].set(4);
      expect(page['valid']()).toBe(false);
      page['name'].set(' Tomato soup trial ');
      page['plannedPrice'].set(10);
      // Named, but in no category yet.
      expect(page['valid']()).toBe(false);
      page['categoryChosen']('sauces');
      fixture.detectChanges();
      expect(page['valid']()).toBe(true);
      expect(figures()).toMatchObject({ 'Planned price': 'L.E 10.00' });

      await page['save']();

      expect(writeRecipe).toHaveBeenCalledWith(null, {
        name: 'Tomato soup trial',
        recipeCategoryId: 'sauces',
        plannedPrice: 10,
        targetKind: null,
        catalogItemId: null,
        variantId: null,
        portions: 4,
        ingredients: [{ rawMaterialId: 'tomato', quantity: 120, yieldPercent: 85 }],
      });
      expect(notify.info).toHaveBeenCalledWith('Recipe saved, off the menu.');
      expect(navigate).toHaveBeenCalledWith(['/costing/recipes/recipe', 'r9']);
    });

    it('is linked to the menu item it was started for, and costed against its price', async () => {
      const { fixture, page, figures } = await render('new', { targetKind: 'MenuItem', catalogItemId: '1' });
      fixture.detectChanges();

      expect(page['linked']().card).toBe(garlicBreadMenu);
      expect(text(fixture.nativeElement.querySelector('.link')!)).toContain('Linked to menu: Garlic Bread');
      expect(figures()).toMatchObject({ 'Menu price': 'L.E 11.99' });
      // A menu item that sells by another recipe cannot be chosen.
      expect(page['linkOptions']().map((o: { label: string; taken: boolean }) => [o.label, o.taken])).toEqual([
        ['6 · Classic Cheeseburger', true],
        ['1 · Garlic Bread', false],
      ]);

      write(page, [['bun', 1]]);
      page['categoryChosen']('mains');
      await page['save']();

      expect(writeRecipe).toHaveBeenCalledWith(null, expect.objectContaining({ name: null, recipeCategoryId: 'mains', targetKind: 'MenuItem', catalogItemId: 1, variantId: null }));
      expect(notify.info).toHaveBeenCalledWith('Recipe saved. Garlic Bread takes stock by it from the next sale.');
    });
  });

  describe('its category', () => {
    it('is chosen from the kitchen\'s own categories, not the menu\'s', async () => {
      const { page } = await render('new');

      expect(recipeCategories).toHaveBeenCalled();
      expect(page['categories']()).toEqual([mains, sauces]);
      expect(page['categoryId']()).toBeNull();
    });

    it('can be a new one, added over the recipe without losing it', async () => {
      const staff: RecipeCategory = { id: 'staff', name: 'Staff meals', nameAr: null, recipes: 0 };
      open.mockReturnValue({ afterClosed: () => of(staff) });
      const { page } = await render('new');
      write(page, [['bun', 2]]);
      recipeCategories.mockReturnValue(of([mains, sauces, staff]));

      await page['categoryChosen'](page['newCategory']);

      expect(open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { fromRecipe: true } }));
      expect(page['categoryId']()).toBe('staff');
      expect(page['categories']()).toContainEqual(staff);
      expect(page['lines']()).toEqual([{ rawMaterialId: 'bun', quantity: 2, yieldPercent: 100 }]);
    });

    it('stays as it was when adding one is cancelled', async () => {
      open.mockReturnValue({ afterClosed: () => of(undefined) });
      const { page } = await render('edit', { recipeId: 'r1' });
      const select = { value: '+new' };

      await page['categoryChosen'](page['newCategory'], select);

      expect([page['categoryId'](), select.value]).toEqual(['mains', 'mains']);
    });

    it('shows on the card, or that it has none yet', async () => {
      const { el } = await render('view', { recipeId: 'r1' });
      expect(text(el.querySelector('.subtitle')!)).toContain('Mainsأطباق رئيسية');

      TestBed.resetTestingModule();
      recipeCardById.mockReturnValue(of({ ...r1, recipeCategoryId: null, recipeCategory: null, recipeCategoryAr: null }));
      const unsorted = await render('view', { recipeId: 'r1' });
      expect(text(unsorted.el.querySelector('.subtitle .unsorted')!)).toBe('Uncategorized');
    });
  });

  describe('edited', () => {
    it('starts from the recipe as saved, its own link still open to it', async () => {
      const { page } = await render('edit', { recipeId: 'r1' });

      expect(recipeCardById).toHaveBeenCalledWith('r1');
      expect([page['name'](), page['categoryId'](), page['link'](), page['portions']()]).toEqual(['House burger', 'mains', 'MenuItem|6|', 1]);
      expect(page['lines']()).toEqual([{ rawMaterialId: 'bun', quantity: 1, yieldPercent: 100 }]);
      expect(page['linkOptions']().find((o: { key: string }) => o.key === 'MenuItem|6|').taken).toBe(false);
      expect(page['dirty']()).toBe(false);
    });

    it('saves its portions, ingredients and yields under its own id', async () => {
      const { page } = await render('edit', { recipeId: 'r1' });

      page['portions'].set(2);
      page['add']();
      page['choose'](1, 'tomato');
      page['change'](1, { quantity: 50, yieldPercent: 90 });
      expect(page['dirty']()).toBe(true);
      await page['save']();

      expect(writeRecipe).toHaveBeenCalledWith('r1', expect.objectContaining({
        name: 'House burger',
        targetKind: 'MenuItem',
        catalogItemId: 6,
        portions: 2,
        ingredients: [
          { rawMaterialId: 'bun', quantity: 1, yieldPercent: 100 },
          { rawMaterialId: 'tomato', quantity: 50, yieldPercent: 90 },
        ],
      }));
      expect(navigate).toHaveBeenCalledWith(['/costing/recipes/recipe', 'r1']);
    });

    it('comes off the menu, keeping its name and planned price', async () => {
      const { page } = await render('edit', { recipeId: 'r2' });
      expect([page['link'](), page['categoryId'](), page['plannedPrice']()]).toEqual([null, 'mains', 60]);

      page['link'].set('MenuItem|1|');
      await page['save']();

      expect(writeRecipe).toHaveBeenCalledWith('r2', expect.objectContaining({ catalogItemId: 1, recipeCategoryId: 'mains', plannedPrice: 60 }));
    });
  });

  describe('viewed', () => {
    it('is read-only, with Edit and Delete for whoever may change it', async () => {
      const { el } = await render('view', { recipeId: 'r1' });

      expect(el.querySelector('input, mat-select')).toBeNull();
      expect(text(el)).toContain('Burger bun');
      expect(text(el)).toContain('Linked to menu: Classic Cheeseburger');
      expect([...el.querySelectorAll('.toolbar a, .toolbar button')].map(text)).toEqual([
        'arrow_back All recipes',
        'edit Edit',
        'delete Delete',
        'print Print',
      ]);
    });

    it('is only shown to someone who may not change it, in any mode', async () => {
      can.mockReturnValue(false);
      const { el } = await render('edit', { recipeId: 'r1' });

      expect(el.querySelector('input, mat-select')).toBeNull();
      expect(text(el)).not.toContain('Save recipe');
      expect(text(el)).not.toContain('Delete');
    });

    it('is deleted after a warning that its menu item stops taking stock', async () => {
      open.mockReturnValue({ afterClosed: () => of({ reason: null }) });
      const { page } = await render('view', { recipeId: 'r1' });

      await page['removeRecipe']();

      expect(open.mock.lastCall![1].data).toMatchObject({
        module: 'Recipe',
        what: 'House burger',
        detail: 'Sales of Classic Cheeseburger stop taking stock until another recipe is linked to it.',
      });
      expect(requestDeletion).toHaveBeenCalledWith(expect.any(String), 'Recipe', 'r1', null);
      expect(notify.info).toHaveBeenCalledWith('House burger deleted.');
      expect(navigate).toHaveBeenCalledWith(['/costing/recipes']);
    });

    it('waiting for a manager to decide its deletion, says who asked and cannot be edited or deleted', async () => {
      approvalRequests.mockReturnValue(of({
        pending: [{ id: 'q1', module: 'Recipe', action: 'Delete', recordId: 'r1', summary: 'Recipe House burger', changes: [], reason: null, requestedBy: 'Mona',
          requestedAtUtc: '2026-10-08T09:00:00Z', status: 'Pending', decidedBy: null, decidedAtUtc: null, decisionNote: null }],
        decided: [],
      }));
      const { fixture, el } = await render('view', { recipeId: 'r1' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(text(el.querySelector('.banner.pending')!)).toContain('Mona asked to delete this recipe');
      expect([...el.querySelectorAll('button, a')].map(text).filter((t) => t.includes('Edit') || t.includes('Delete'))).toEqual([]);
    });

    it('when deleted, says so, and can be restored', async () => {
      recipeCardById.mockReturnValue(of({ ...r1, isDeleted: true, deletedAtUtc: '2026-10-07T10:00:00Z', deletedBy: 'Omar' }));
      const { el, page } = await render('view', { recipeId: 'r1' });

      expect(text(el.querySelector('.banner')!)).toContain('Deleted 7 Oct 2026');
      expect(text(el.querySelector('.banner')!)).toContain('by Omar');
      expect(text(el)).not.toContain('edit Edit');

      await page['restore']();
      expect(restoreRecipe).toHaveBeenCalledWith('r1', 'House burger');
    });
  });

  describe('from an old link to a menu item', () => {
    it('goes on to the recipe the item sells by', async () => {
      recipeCard.mockReturnValue(of(cheeseburgerMenu));
      await render('menu', { kind: 'MenuItem', id: '6' });

      expect(recipeCard).toHaveBeenCalledWith('MenuItem', 6, null);
      expect(navigate).toHaveBeenCalledWith(['/costing/recipes/recipe', 'r1'], { replaceUrl: true });
    });

    it('goes on to a new recipe for an item that has none', async () => {
      recipeCard.mockReturnValue(of(garlicBreadMenu));
      await render('menu', { kind: 'MenuItem', id: '1', variantId: '3' });

      expect(navigate).toHaveBeenCalledWith(['/costing/recipes/new'], {
        replaceUrl: true,
        queryParams: { targetKind: 'MenuItem', catalogItemId: 1, variantId: 3 },
      });
    });
  });
});
