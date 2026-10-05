import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Category, MenuItem, TillMenu } from '../api/models';
import { MenuService } from './menu.service';

describe('the menu at the till', () => {
  const drinks: Category = { id: 1, name: 'Drinks', nameAr: null, displayOrder: 0 };
  const mains: Category = { id: 2, name: 'Mains', nameAr: null, displayOrder: 1 };

  function item(id: number, name: string, category: string, subCategoryId: number | null = null): MenuItem {
    return {
      id,
      name,
      nameAr: null,
      description: null,
      price: 10,
      imageUrl: null,
      isAvailable: true,
      isPriceBasedOnAddons: false,
      priceNote: null,
      category,
      subCategoryId,
      menuItemAddOns: [],
      variants: [],
    };
  }

  async function load(menu: TillMenu): Promise<MenuService> {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const service = TestBed.inject(MenuService);
    const loading = service.load();
    TestBed.inject(HttpTestingController).expectOne('/api/menu').flush(menu);
    await loading;
    return service;
  }

  const names = (items: MenuItem[]) => items.map((i) => i.name);

  // The delivery system's sample menu is like this: categories, and no sub-categories at all.
  it('shows each item under its category, sub-category or not', async () => {
    const menu = await load({
      categories: [drinks, mains],
      subCategories: [],
      menuItems: [item(1, 'Iced Tea', 'Drinks'), item(2, 'Cheeseburger', 'Mains'), item(3, 'Soft Drink', 'Drinks')],
    });

    expect(menu.sectionsFor(drinks).map((s) => [s.subCategory, names(s.items)])).toEqual([[null, ['Iced Tea', 'Soft Drink']]]);
    expect(names(menu.sectionsFor(mains).flatMap((s) => s.items))).toEqual(['Cheeseburger']);
    expect(menu.unfiled()).toEqual([]);
  });

  it('groups a category by its sub-categories, after the items in none of them', async () => {
    const menu = await load({
      categories: [drinks],
      subCategories: [
        { id: 10, name: 'Hot', nameAr: null, displayOrder: 0, categoryId: 1 },
        { id: 11, name: 'Cold', nameAr: null, displayOrder: 1, categoryId: 1 },
        { id: 12, name: 'Burgers', nameAr: null, displayOrder: 0, categoryId: 2 },
      ],
      menuItems: [
        item(1, 'Iced Tea', 'Drinks', 11),
        item(2, 'Tea', 'Drinks', 10),
        item(3, 'Water', 'Drinks'),
        // Filed under another category's sub-category: still a drink, so still shown here.
        item(4, 'Ayran', 'Drinks', 12),
      ],
    });

    expect(menu.sectionsFor(drinks).map((s) => [s.subCategory?.name ?? null, names(s.items)])).toEqual([
      [null, ['Water', 'Ayran']],
      ['Hot', ['Tea']],
      ['Cold', ['Iced Tea']],
    ]);
  });

  it('keeps items of a category that is gone on their own tab', async () => {
    const menu = await load({
      categories: [drinks],
      subCategories: [],
      menuItems: [item(1, 'Iced Tea', 'Drinks'), item(2, 'Koshari', 'Egyptian')],
    });

    expect(names(menu.unfiled())).toEqual(['Koshari']);
    expect(names(menu.sectionsFor(drinks).flatMap((s) => s.items))).toEqual(['Iced Tea']);
  });
});
