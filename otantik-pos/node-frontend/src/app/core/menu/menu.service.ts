import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MenuItem, SubCategory, TillMenu } from '../api/models';

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

  // Items filed under no sub-category, or under one that has since gone: shown on their own tab
  // rather than nowhere.
  readonly unfiled = computed(() => {
    const menu = this._menu();
    if (!menu) {
      return [];
    }
    const filed = new Set(menu.subCategories.map((s) => s.id));
    return menu.menuItems.filter((item) => item.subCategoryId === null || !filed.has(item.subCategoryId));
  });

  async load(): Promise<void> {
    const menu = await firstValueFrom(this.http.get<TillMenu>('/api/menu'));
    this._menu.set(menu);
  }

  // A category's items grouped under its sub-categories, in display order (the API sends them
  // sorted). Empty sub-categories are left out.
  sectionsFor(categoryId: number): MenuSection[] {
    const menu = this._menu();
    if (!menu) {
      return [];
    }
    return menu.subCategories
      .filter((s) => s.categoryId === categoryId)
      .map((subCategory) => ({
        subCategory,
        items: menu.menuItems.filter((item) => item.subCategoryId === subCategory.id),
      }))
      .filter((section) => section.items.length > 0);
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
