import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, MaterialCost, Recipe, RecipeCostCard } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { RecipeCardPage } from './recipe-card-page';

function material(overrides: Partial<MaterialCost>): MaterialCost {
  return {
    id: 'm',
    code: null,
    name: 'Material',
    category: null,
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
    ...overrides,
  };
}

// Beef bought at 400 then 450 a kg: an average of 416.666667 a gram-thousand, shown as 416.67.
const beef = material({ id: 'beef', name: 'Beef mince', costPerPurchaseUnit: 416.67, costPerUnit: 0.416667 });
const bun = material({ id: 'bun', name: 'Burger bun', unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1, costPerPurchaseUnit: 6, costPerUnit: 6 });
const tomato = material({ id: 'tomato', name: 'Tomato', costPerPurchaseUnit: 35, costPerUnit: 0.035, defaultYieldPercent: 85 });
const saffron = material({ id: 'saffron', name: 'Saffron', purchaseUnit: 'g', purchaseUnitSize: 1 });

const card: RecipeCostCard = {
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
};

describe('the recipe card', () => {
  const recipeCard = vi.fn();
  const recipe = vi.fn();
  const saveRecipe = vi.fn();
  const can = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { recipeCard, recipe, saveRecipe, materials: () => of([beef, bun, tomato, saffron]) } },
        { provide: AuthService, useValue: { can } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(RecipeCardPage);
    fixture.componentRef.setInput('kind', 'MenuItem');
    fixture.componentRef.setInput('id', '6');
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
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any>, figures };
  }

  beforeEach(() => {
    recipeCard.mockReset().mockReturnValue(of(card));
    recipe.mockReset().mockReturnValue(of(null));
    saveRecipe.mockReset().mockReturnValue(of({}));
    can.mockReset().mockReturnValue(true);
  });

  it('works a recipe out as the template does, as it is typed', async () => {
    const { fixture, page, figures } = await render();

    page['portions'].set(4);
    for (const [id, quantity] of [['beef', 480], ['bun', 4], ['tomato', 120]] as const) {
      page['add']();
      const i = page['lines']().length - 1;
      page['choose'](i, id);
      page['change'](i, { quantity });
    }
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
    const { page } = await render();

    page['add']();
    page['choose'](0, 'beef');
    page['change'](0, { quantity: 10000 });

    // 10 kg at 416.666… is 4,166.67; from the rounded 416.67 it would be 4,166.70.
    expect(page['costPerRecipe']()).toBe(4166.67);
  });

  it('says so when an ingredient has no price, rather than costing it at nothing', async () => {
    const { fixture, page } = await render();

    page['add']();
    page['choose'](0, 'saffron');
    page['change'](0, { quantity: 1 });
    fixture.detectChanges();

    expect(page['status']()).toBe('MissingPrices');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No price yet for Saffron');
  });

  it('will not save an ingredient on two lines', async () => {
    const { page } = await render();

    for (const quantity of [100, 50]) {
      page['add']();
      const i = page['lines']().length - 1;
      page['choose'](i, 'beef');
      page['change'](i, { quantity });
    }

    expect(page['duplicate']()).toBe(true);
    expect(page['valid']()).toBe(false);
    await page['save']();
    expect(saveRecipe).not.toHaveBeenCalled();
  });

  it('saves the recipe as written: EP quantities, yields and portions', async () => {
    const { page } = await render();

    page['portions'].set(4);
    page['add']();
    page['choose'](0, 'tomato');
    page['change'](0, { quantity: 120 });
    await page['save']();

    expect(saveRecipe).toHaveBeenCalledWith({
      targetKind: 'MenuItem',
      catalogItemId: 6,
      variantId: null,
      ingredients: [{ rawMaterialId: 'tomato', quantity: 120, yieldPercent: 85 }],
      portions: 4,
    });
  });

  it('only shows the recipe to someone who may not change it', async () => {
    can.mockReturnValue(false);
    recipeCard.mockReturnValue(
      of({ ...card, hasRecipe: true, lines: [{ rawMaterialId: 'bun', code: null, ingredient: 'Burger bun', quantity: 1, unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1, apsPerUnit: 6, yieldPercent: 100, epsPerUnit: 6, recipeCost: 6 }] }),
    );
    const recipeOf: Recipe = { targetKind: 'MenuItem', catalogItemId: 6, variantId: null, portions: 1, ingredients: [{ rawMaterialId: 'bun', quantity: 1, yieldPercent: 100 }] };
    recipe.mockReturnValue(of(recipeOf));
    const { fixture } = await render();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('input, mat-select')).toBeNull();
    expect(element.textContent).not.toContain('Save recipe');
    expect(element.textContent).toContain('Burger bun');
  });
});
