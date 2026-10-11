import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, RecipeCategory, RecipeCostCard } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { RecipesPage } from './recipes-page';

function card(overrides: Partial<RecipeCostCard>): RecipeCostCard {
  return {
    targetKind: 'MenuItem',
    catalogItemId: 6,
    variantId: null,
    itemCode: '6',
    recipe: 'Classic Cheeseburger',
    recipeAr: null,
    category: 'Mains',
    hasRecipe: true,
    portions: 1,
    menuPrice: 120,
    foodCostTargetPercent: 30,
    lines: [],
    costPerRecipe: 30,
    ingredientCostPerPortion: 30,
    sharedCostPerPortion: 0,
    costPerPortion: 30,
    marginPerPortion: 90,
    foodCostPercentActual: 25,
    idealSellingPrice: 100,
    missingPrices: [],
    status: 'WithinTarget',
    recipeId: 'r1',
    recipeName: null,
    linkedTo: 'Classic Cheeseburger',
    isStandalone: false,
    plannedPrice: null,
    usesBaseRecipe: false,
    isDeleted: false,
    deletedAtUtc: null,
    deletedBy: null,
    recipeCategoryId: 'mains',
    recipeCategory: 'Mains',
    recipeCategoryAr: null,
    ...overrides,
  };
}

const burger = card({});
const trial = card({
  recipeId: 'r2', recipe: 'Smash trial', recipeName: 'Smash trial', targetKind: null, catalogItemId: null, itemCode: '', linkedTo: null,
  isStandalone: true, menuPrice: 60, plannedPrice: 60, foodCostPercentActual: 55, status: 'AboveTarget', recipeCategoryId: null, recipeCategory: null,
});
const mains: RecipeCategory = { id: 'mains', name: 'Mains', nameAr: null, recipes: 1 };
const sauces: RecipeCategory = { id: 'sauces', name: 'Sauces', nameAr: 'صوصات', recipes: 1 };
const garlicDip = card({
  recipeId: 'r4', recipe: 'Garlic dip', recipeName: 'Garlic dip', targetKind: null, catalogItemId: null, itemCode: '', linkedTo: null,
  isStandalone: true, menuPrice: null, costPerPortion: 4, foodCostPercentActual: null, status: 'NoPrice',
  recipeCategoryId: 'sauces', recipeCategory: 'Sauces', recipeCategoryAr: 'صوصات',
});
const unsorted = card({ recipeId: 'r5', recipe: 'Tray bake', recipeName: 'Tray bake', recipeCategoryId: null, recipeCategory: null, costPerPortion: 50, foodCostPercentActual: 40 });
const old = card({ recipeId: 'r3', recipe: 'Old burger', recipeName: 'Old burger', isDeleted: true, deletedAtUtc: '2026-10-01T10:00:00Z', deletedBy: 'Omar' });
// On the menu: the burger sells by r1; garlic bread and the shawarma's large tray have no recipe.
const menu = [
  burger,
  card({ catalogItemId: 1, itemCode: '1', recipe: 'Garlic Bread', linkedTo: 'Garlic Bread', hasRecipe: false, recipeId: null, menuPrice: 25 }),
  card({ catalogItemId: 10, variantId: 101, itemCode: '10-101', recipe: 'Shawarma (Large tray)', linkedTo: 'Shawarma (Large tray)', hasRecipe: false, recipeId: null }),
];

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the recipes', () => {
  const recipes = vi.fn();
  const deleteRecipe = vi.fn();
  const restoreRecipe = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { recipes, menu: () => of(menu), deleteRecipe, restoreRecipe, recipeCategories: () => of([mains, sauces]) } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(RecipesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => {
      fixture.detectChanges();
      return [...el.querySelectorAll('tbody tr')];
    };
    const button = (row: Element, label: string) =>
      [...row.querySelectorAll('button')].find((b) => text(b).includes(label) || b.getAttribute('aria-label')?.startsWith(label))!;
    return { el, rows, button, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    recipes.mockReset().mockReturnValue(of([burger, trial, old]));
    deleteRecipe.mockReset().mockReturnValue(of(undefined));
    restoreRecipe.mockReset().mockReturnValue(of({ id: 'r3', catalogItemId: null }));
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('lists the recipes, each linked to the menu or standalone, with View, Edit and Delete', async () => {
    const { el, rows } = await render();

    expect(recipes).toHaveBeenCalledWith(true);
    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 7).map(text))).toEqual([
      ['Classic Cheeseburger', 'Mains', 'Linked to menu: Classic Cheeseburgerمرتبطة بالمنيو', '1', 'L.E 120.00', 'L.E 30.00', '25.0%'],
      ['Smash trial', 'Uncategorized', 'Standalone / Off-menuمستقلة', '1', 'L.E 60.00 planned', 'L.E 30.00', '55.0%'],
    ]);
    expect(rows().map((r) => [...r.querySelectorAll('td.actions button')].map((b) => b.getAttribute('aria-label') ?? text(b)))).toEqual([
      ['View Classic Cheeseburger', 'Edit Classic Cheeseburger', 'Delete Classic Cheeseburger'],
      ['View Smash trial', 'Edit Smash trial', 'Delete Smash trial'],
    ]);
    expect(text(el.querySelector('.counts')!)).toBe('2 recipes: 1 linked to the menu, 1 off it. 2 menu items have no recipe.');
    expect([...el.querySelectorAll('thead th button.sort')].map(text)).toEqual(['Recipe ↑', 'Category', 'Cost per portion', 'Food cost %']);
    expect(text(el.querySelector('a.create')!)).toBe('add Create New Recipe');
  });

  it('shows only those on the menu, or off it', async () => {
    const { page } = await render();

    page['view'].set('linked');
    expect(page['shown']()).toEqual([burger]);
    page['view'].set('standalone');
    expect(page['shown']()).toEqual([trial]);
  });

  it('views and edits a recipe', async () => {
    const { rows, button } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    button(rows()[1], 'View').click();
    expect(navigate).toHaveBeenLastCalledWith(['/costing/recipes/recipe', 'r2']);
    button(rows()[1], 'Edit').click();
    expect(navigate).toHaveBeenLastCalledWith(['/costing/recipes/recipe', 'r2', 'edit']);
  });

  it('asks before deleting, warning that a linked menu item stops taking stock, and keeps it', async () => {
    const { rows, button } = await render();

    button(rows()[0], 'Delete').click();
    const ask = rows()[1];
    expect(ask.classList).toContain('confirm-row');
    expect(text(ask)).toContain('Sales of Classic Cheeseburger stop taking stock');
    expect(text(ask)).toContain('It is kept, marked deleted, and can be restored');
    expect(deleteRecipe).not.toHaveBeenCalled();

    button(ask, 'Delete').click();
    await vi.waitFor(() => expect(deleteRecipe).toHaveBeenCalledWith('r1'));
    expect(notify.info).toHaveBeenCalledWith('Classic Cheeseburger deleted: Classic Cheeseburger no longer takes stock. It can be restored from Deleted.');
  });

  it('can be talked out of deleting', async () => {
    const { rows, button } = await render();

    button(rows()[1], 'Delete').click();
    expect(text(rows()[2])).not.toContain('stop taking stock');
    button(rows()[2], 'Cancel').click();

    expect(rows().some((r) => r.classList.contains('confirm-row'))).toBe(false);
    expect(deleteRecipe).not.toHaveBeenCalled();
  });

  it('shows the deleted ones apart, with who deleted them, to restore', async () => {
    const { page, rows, button } = await render();
    page['view'].set('deleted');

    expect(rows().map((r) => text(r.querySelectorAll('td')[7]))).toEqual(['Deleted 1 Oct 2026 by Omar']);
    button(rows()[0], 'Restore').click();

    await vi.waitFor(() => expect(restoreRecipe).toHaveBeenCalledWith('r3', 'Old burger'));
    expect(notify.info).toHaveBeenCalledWith('Old burger restored off the menu: Classic Cheeseburger has another recipe now.');
  });

  it('lists the menu items with no recipe, each a click from a new one linked to it', async () => {
    const { page, rows, button } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    page['view'].set('unrecipied');

    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 2).map(text))).toEqual([
      ['1', 'Garlic Bread'],
      ['10-101', 'Shawarma (Large tray)'],
    ]);
    button(rows()[1], 'Create recipe').click();
    expect(navigate).toHaveBeenCalledWith(['/costing/recipes/new'], { queryParams: { targetKind: 'MenuItem', catalogItemId: 10, variantId: 101 } });
  });

  describe('by category', () => {
    beforeEach(() => recipes.mockReturnValue(of([burger, trial, garlicDip, unsorted])));

    it('shows one category, or the uncategorized ones', async () => {
      const { page } = await render();

      page['category'].set('sauces');
      expect(page['shown']()).toEqual([garlicDip]);
      page['category'].set(page['uncategorized']);
      expect(page['shown']().map((c: RecipeCostCard) => c.recipe)).toEqual(['Smash trial', 'Tray bake']);
      page['category'].set(null);
      expect(page['shown']()).toHaveLength(4);
    });

    it('starts on the category a count was clicked for', async () => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: CostingApi, useValue: { recipes, menu: () => of(menu), deleteRecipe, restoreRecipe, recipeCategories: () => of([mains, sauces]) } },
          { provide: NotifyService, useValue: notify },
        ],
      });
      const fixture = TestBed.createComponent(RecipesPage);
      fixture.componentRef.setInput('category', 'sauces');
      await fixture.whenStable();

      expect((fixture.componentInstance as unknown as Record<string, any>)['shown']()).toEqual([garlicDip]);
    });

    it('sorts by category, the uncategorized last, and back the other way', async () => {
      const { page, el } = await render();
      const names = () => page['shown']().map((c: RecipeCostCard) => c.recipe);

      expect(names()).toEqual(['Classic Cheeseburger', 'Garlic dip', 'Smash trial', 'Tray bake']);
      page['sortBy']('category');
      expect(names()).toEqual(['Classic Cheeseburger', 'Garlic dip', 'Smash trial', 'Tray bake']);
      page['sortBy']('category');
      expect(names()).toEqual(['Smash trial', 'Tray bake', 'Garlic dip', 'Classic Cheeseburger']);
      expect(page['ariaSort']('category')).toBe('descending');
    });

    it('sorts by cost and by food cost %, those with none last', async () => {
      const { page } = await render();
      const names = () => page['shown']().map((c: RecipeCostCard) => c.recipe);

      page['sortBy']('cost');
      expect(names()).toEqual(['Garlic dip', 'Classic Cheeseburger', 'Smash trial', 'Tray bake']);
      page['sortBy']('foodCost');
      expect(names()).toEqual(['Classic Cheeseburger', 'Tray bake', 'Smash trial', 'Garlic dip']);
    });

    it('finds a recipe by its category too', async () => {
      const { page } = await render();

      page['search'].set('صوصات');
      expect(page['shown']()).toEqual([garlicDip]);
    });
  });
});
