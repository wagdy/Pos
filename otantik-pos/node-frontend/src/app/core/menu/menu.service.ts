import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Category, MenuItem, SubCategory, TillMenu } from '../api/models';

// Null sub-category: the category's items filed under none of its sub-categories, shown first
// and without a heading, as the delivery app shows them.
export interface MenuSection {
  subCategory: SubCategory | null;
  items: MenuItem[];
}

// The menu, kept in memory once loaded: browsing it never waits on the network, and a dropped
// connection leaves the last copy on screen. Reloaded when the till reconnects, and every few
// minutes, since the delivery system's admin can change it during service.
@Injectable({ providedIn: 'root' })
export class MenuService {
  private readonly http = inject(HttpClient);

  private readonly _menu = signal<TillMenu | null>(null);
  readonly menu = this._menu.asReadonly();
  readonly categories = computed(() => this._menu()?.categories ?? []);

  // Items whose category is not on the menu any more (renamed or removed in the delivery
  // system's admin): shown on their own tab rather than nowhere.
  readonly unfiled = computed(() => {
    const menu = this._menu();
    if (!menu) {
      return [];
    }
    const names = new Set(menu.categories.map((c) => c.name));
    return menu.menuItems.filter((item) => !names.has(item.category));
  });

  async load(): Promise<void> {
    const menu = await firstValueFrom(this.http.get<TillMenu>('/api/menu'));
    this._menu.set(menu);
  }

  // A category's items, as the delivery app files them: an item belongs to the category named by
  // its Category, and its sub-category only groups it within that. Those in none of the
  // category's sub-categories come first, then each sub-category in display order (the API
  // sends them sorted). Empty sections are left out.
  sectionsFor(category: Category): MenuSection[] {
    const menu = this._menu();
    if (!menu) {
      return [];
    }
    const items = menu.menuItems.filter((item) => item.category === category.name);
    const subCategories = menu.subCategories.filter((s) => s.categoryId === category.id);
    const grouped = new Set(subCategories.map((s) => s.id));
    return [
      { subCategory: null, items: items.filter((item) => item.subCategoryId === null || !grouped.has(item.subCategoryId)) },
      ...subCategories.map((subCategory) => ({
        subCategory,
        items: items.filter((item) => item.subCategoryId === subCategory.id),
      })),
    ].filter((section) => section.items.length > 0);
  }

  search(text: string): MenuItem[] {
    const needle = text.trim().toLowerCase();
    if (!needle) {
      return [];
    }
    return (this._menu()?.menuItems ?? []).filter(
      (item) => item.name.toLowerCase().includes(needle) || (item.nameAr ?? '').includes(needle),
    );
  }

  find(menuItemId: number): MenuItem | undefined {
    return this._menu()?.menuItems.find((item) => item.id === menuItemId);
  }
}
