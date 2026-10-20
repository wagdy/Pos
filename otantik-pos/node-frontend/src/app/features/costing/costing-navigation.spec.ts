import { TestBed } from '@angular/core/testing';
import { Route, provideRouter } from '@angular/router';
import { routes } from '../../app.routes';
import { Approvals } from './approvals';
import { CostingPage } from './costing-page';
import { PurchasesShell } from './purchases-shell';

// The route of that path, anywhere under these.
function find(list: Route[] | undefined, path: string): Route | undefined {
  for (const route of list ?? []) {
    if (route.path === path) {
      return route;
    }
    const inside = find(route.children, path);
    if (inside) {
      return inside;
    }
  }
  return undefined;
}

describe('the costing navigation', () => {
  it('gives the expense items a tab of their own, and sends their old address under Purchases there', () => {
    const costing = find(routes, 'costing')!;
    const own = costing.children!.find((r) => r.path === 'expense-items')!;
    expect(own.loadComponent).toBeDefined();

    const purchases = costing.children!.find((r) => r.path === 'purchases')!;
    expect(purchases.children!.find((r) => r.path === 'expense-items')!.redirectTo).toBe('/costing/expense-items');
  });

  it('puts Expense items after Raw materials, before Purchases', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: Approvals, useValue: { canApprove: () => true, pending: () => [], refresh: vi.fn() } }],
    });
    const fixture = TestBed.createComponent(CostingPage);
    await fixture.whenStable();
    const tabs = (fixture.componentInstance as unknown as Record<string, any>)['tabs']() as { path: string; en: string; ar: string }[];

    expect(tabs.slice(0, 3).map((t) => [t.path, t.en, t.ar])).toEqual([
      ['materials', 'Raw materials', 'الخامات'],
      ['expense-items', 'Expense items', 'المصروفات العمومية'],
      ['purchases', 'Purchases', 'المشتريات'],
    ]);
  });

  it('keeps Purchases to buying: no expense items there, and the categories called simply Categories', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(PurchasesShell);
    await fixture.whenStable();
    const tabs = (fixture.componentInstance as unknown as Record<string, any>)['tabs'] as { path: string; en: string; ar: string }[];

    expect(tabs.map((t) => t.path)).not.toContain('expense-items');
    expect(tabs.find((t) => t.path === 'categories')).toMatchObject({ en: 'Categories', ar: 'الأقسام' });
  });
});
