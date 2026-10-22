import { TestBed } from '@angular/core/testing';
import { Route, provideRouter } from '@angular/router';
import { routes } from '../../app.routes';
import { Approvals } from './approvals';
import { CostingPage } from './costing-page';
import { CostingReportsShell } from './costing-reports-shell';
import { AuthService } from '../../core/auth/auth.service';
import { Treasury } from './financials';
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

  it('gathers the reports under Reports, the spending first, and sends their old addresses there', () => {
    const costing = find(routes, 'costing')!;
    const reports = costing.children!.find((r) => r.path === 'reports')!;
    expect(reports.loadComponent).toBeDefined();
    expect(reports.children!.map((r) => [r.path, r.redirectTo ?? (r.loadComponent ? 'page' : undefined)])).toEqual([
      ['', 'spending'],
      ['spending', 'page'],
      ['theoretical', 'page'],
      ['variance', 'page'],
      ['kpis', 'page'],
      ['break-even', 'page'],
    ]);

    for (const old of ['theoretical', 'variance', 'kpis', 'break-even']) {
      expect(costing.children!.find((r) => r.path === old)).toEqual({ path: old, redirectTo: `reports/${old}` });
    }
    const purchases = costing.children!.find((r) => r.path === 'purchases')!;
    expect(purchases.children!.find((r) => r.path === 'spending')!.redirectTo).toBe('/costing/reports/spending');
  });

  it('puts Expense items after Raw materials, before Purchases, and the reports on one tab after Recipes', async () => {
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
    expect(tabs.map((t) => t.path)).toEqual(['materials', 'expense-items', 'purchases', 'financials', 'waste', 'counts', 'recipes', 'reports', 'settings', 'approvals']);
    expect(tabs.find((t) => t.path === 'reports')).toMatchObject({ en: 'Reports', ar: 'التقارير' });
  });

  it('opens Reports on the spending analysis, then the food cost reports', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(CostingReportsShell);
    await fixture.whenStable();
    fixture.detectChanges();

    // Its address, icon, and name in English and Arabic.
    const link = (a: Element) => {
      const icon = a.querySelector('mat-icon')!.textContent!.trim();
      const ar = a.querySelector('.inline-ar')!.textContent!.trim();
      return [a.getAttribute('href'), icon, a.textContent!.replace(icon, '').replace(ar, '').trim(), ar];
    };
    expect([...(fixture.nativeElement as HTMLElement).querySelectorAll('nav a')].map(link)).toEqual([
      ['/spending', 'pie_chart', 'Spending analysis', 'تحليل الإنفاق'],
      ['/theoretical', 'calculate', 'Theoretical cost', 'التكلفة النظرية'],
      ['/variance', 'compare_arrows', 'Variance', 'الانحراف'],
      ['/kpis', 'insights', 'KPIs', 'المؤشرات'],
      ['/break-even', 'balance', 'Break-even', 'نقطة التعادل'],
    ]);
  });

  it('keeps Purchases to buying: no expense items or spending there, and the categories called simply Categories', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { can: () => true } }, { provide: Treasury, useValue: { enter: vi.fn() } }],
    });
    const fixture = TestBed.createComponent(PurchasesShell);
    await fixture.whenStable();
    const tabs = (fixture.componentInstance as unknown as Record<string, any>)['tabs'] as { path: string; en: string; ar: string }[];

    expect(tabs.map((t) => t.path)).toEqual(['new', 'batch', 'invoices', 'suppliers', 'categories']);
    expect(tabs.find((t) => t.path === 'categories')).toMatchObject({ en: 'Categories', ar: 'الأقسام' });
  });

  it('pays an employee advance from Purchases in the dialog Financials does, for whoever manages HR', async () => {
    const enter = vi.fn().mockResolvedValue(true);
    const can = vi.fn(() => true);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { can } }, { provide: Treasury, useValue: { enter } }],
    });
    const fixture = TestBed.createComponent(PurchasesShell);
    fixture.detectChanges();
    const button = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('nav button')].find((b) => b.textContent!.includes('New advance'));

    (button() as HTMLButtonElement).click();
    expect(enter).toHaveBeenCalledWith('EmployeeAdvance');
    TestBed.resetTestingModule();

    can.mockReturnValue(false);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { can } }, { provide: Treasury, useValue: { enter } }],
    });
    const viewer = TestBed.createComponent(PurchasesShell);
    viewer.detectChanges();
    expect([...(viewer.nativeElement as HTMLElement).querySelectorAll('nav button')].length).toBe(0);
  });
});
