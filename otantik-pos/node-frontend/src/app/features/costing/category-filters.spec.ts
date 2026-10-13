import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, MaterialCost, PrepItem, RecipeCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { MaterialsPage } from './materials-page';
import { PrepItemsPage } from './prep-items-page';
import { RecipeCategoriesPage } from './recipe-categories-page';

// The long lists, one category at a time: raw materials by their stock category, prep items by
// the kitchen's recipe category, as the dishes are.

function material(overrides: Partial<MaterialCost>): MaterialCost {
  return {
    id: 'm', code: null, name: 'Material', category: 'General', categoryId: 'general', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000,
    costPerPurchaseUnit: null, defaultYieldPercent: 100, quantityOnHand: 0, reorderLevel: 0, stockValue: null, lastPurchaseCostPerPurchaseUnit: null,
    lastPurchasedAtUtc: null, costPerUnit: null, kind: 'Purchased', ...overrides,
  };
}

function prep(overrides: Partial<PrepItem>): PrepItem {
  return {
    id: 'p', code: null, name: 'Prep', category: 'مصنعات', categoryId: 'prep', unit: 'Gram', displayUnit: 'kg', displayUnitSize: 1000, reorderLevel: 0,
    batchQuantity: 1000, ingredients: [], standardBatchCost: null, standardCostPerDisplayUnit: null, missingPrices: [], quantityOnHand: 0,
    averageCostPerDisplayUnit: null, stockValue: null, lastProducedAtUtc: null, lastBatchCostPerDisplayUnit: null, recent: [], recipeCategoryId: null,
    ...overrides,
  };
}

const sauces: RecipeCategory = { id: 'sauces', name: 'Sauces', nameAr: 'صوصات', recipes: 3, prepItems: 2 };
const doughs: RecipeCategory = { id: 'doughs', name: 'Doughs', nameAr: null, recipes: 0, prepItems: 1 };

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
const approvals = { refresh: vi.fn(), pendingFor: vi.fn(), waitingToBeMade: vi.fn(() => []), delete: vi.fn() };

describe('raw materials by category', () => {
  async function render() {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: CostingApi,
          useValue: {
            materials: () =>
              of([
                material({ id: 'milk', name: 'Milk', category: 'Dairy', categoryId: 'dairy' }),
                material({ id: 'beef', name: 'Beef mince', category: 'Meat', categoryId: 'meat' }),
                material({ id: 'cheese', name: 'Cheddar', category: 'Dairy', categoryId: 'dairy' }),
              ]),
          },
        },
        { provide: Approvals, useValue: approvals },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(MaterialsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  it('offers the stock categories the materials are in, with how many, and shows one at a time', async () => {
    const { fixture, el, page } = await render();

    expect(page['categories']()).toEqual([
      { id: 'dairy', name: 'Dairy', count: 2 },
      { id: 'meat', name: 'Meat', count: 1 },
    ]);
    expect(page['category']()).toBeNull();
    expect(page['shown']().length).toBe(3);

    page['category'].set('dairy');
    fixture.detectChanges();
    expect([...el.querySelectorAll('tbody tr')].map((r) => text(r.querySelectorAll('td')[1]))).toEqual(['Milk', 'Cheddar']);

    // With the search as well.
    page['search'].set('ched');
    expect(page['shown']().map((m: MaterialCost) => m.name)).toEqual(['Cheddar']);
  });
});

describe('prep items by recipe category', () => {
  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            prepItems: () =>
              of([
                prep({ id: 'bechamel', name: 'Béchamel', recipeCategoryId: 'sauces' }),
                prep({ id: 'dough', name: 'Pizza dough', recipeCategoryId: 'doughs' }),
                prep({ id: 'garlic', name: 'Garlic sauce', recipeCategoryId: 'sauces' }),
                prep({ id: 'mix', name: 'Dolma mix' }),
              ]),
            recipeCategories: () => of([doughs, sauces]),
            approvalRequests: () => of({ pending: [], decided: [] }),
          },
        },
        { provide: AuthService, useValue: { can: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(PrepItemsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  const names = (page: Record<string, any>) => page['shown']().map((p: PrepItem) => p.name);

  it('shows each with its recipe category, and one category at a time, the unsorted too', async () => {
    const { fixture, el, page } = await render();

    expect(names(page)).toEqual(['Béchamel', 'Pizza dough', 'Garlic sauce', 'Dolma mix']);
    expect([...el.querySelectorAll('tbody tr')].map((r) => r.querySelector('td .muted.small')?.textContent?.trim() ?? null)).toEqual(
      ['Sauces', 'Doughs', 'Sauces', null],
    );

    page['category'].set('sauces');
    fixture.detectChanges();
    expect(names(page)).toEqual(['Béchamel', 'Garlic sauce']);
    expect(el.querySelectorAll('tbody tr').length).toBe(2);

    page['category'].set(page['uncategorized']);
    expect(names(page)).toEqual(['Dolma mix']);
  });

  it('opens on the category a link names', async () => {
    const { fixture, page } = await render();

    fixture.componentRef.setInput('category', 'doughs');
    fixture.detectChanges();
    expect(names(page)).toEqual(['Pizza dough']);
  });
});

describe('recipe categories and their prep items', () => {
  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { recipeCategories: () => of([doughs, sauces]) } },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(RecipeCategoriesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  it('counts the prep items beside the recipes, a count opening them, and moves both when one is deleted', async () => {
    const { fixture, el, page } = await render();

    const rows = [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(text));
    expect(rows).toEqual([
      ['Doughs', '—', '0', '1'],
      ['Sauces', 'صوصات', '3', '2'],
    ]);
    expect(el.querySelectorAll('tbody tr')[0].querySelectorAll('td')[3].querySelector('a')!.getAttribute('href')).toBe('/costing/recipes/prep?category=doughs');

    // Doughs holds only a prep item: it still needs somewhere for it to go.
    expect(page['heldText'](doughs)).toBe('1 prep item moves');
    expect(page['heldText'](sauces)).toBe('3 recipes and 2 prep items move');
    page['ask'](doughs);
    fixture.detectChanges();
    expect(text(el.querySelector('.confirm')!)).toContain('Delete Doughs? Its 1 prep item moves to');
  });
});
