import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, PurchaseCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { CategoryDeleteDialog } from './category-delete-dialog';
import { PurchaseCategoriesPage } from './purchase-categories-page';
import { PurchaseCategoryDialog } from './purchase-category-dialog';

const general: PurchaseCategory = {
  id: 'general', name: 'General', nameAr: 'عام', expenseLine: 'Controllable', isGeneral: true, materials: 2, expenseItems: 0, kind: 'RawMaterials', misplaced: 0,
};
const dairy: PurchaseCategory = {
  id: 'dairy', name: 'Dairy', nameAr: null, expenseLine: 'Controllable', isGeneral: false, materials: 3, expenseItems: 0, kind: 'RawMaterials', misplaced: 0,
};
const rent: PurchaseCategory = {
  id: 'rent', name: 'Rent', nameAr: 'إيجار', expenseLine: 'Occupation', isGeneral: false, materials: 0, expenseItems: 1, kind: 'Expenses', misplaced: 0,
};
const empty: PurchaseCategory = {
  id: 'empty', name: 'Packaging', nameAr: null, expenseLine: 'Controllable', isGeneral: false, materials: 0, expenseItems: 0, kind: 'RawMaterials', misplaced: 0,
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the purchase categories', () => {
  const purchaseCategories = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { refresh: vi.fn(), pendingFor: vi.fn() };

  // On a tab: none given is raw materials.
  async function render(tab?: string) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { purchaseCategories } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: Approvals, useValue: approvals },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(PurchaseCategoriesPage);
    if (tab) {
      fixture.componentRef.setInput('tab', tab);
    }
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => {
      fixture.detectChanges();
      return [...el.querySelectorAll('mat-tab-body.mat-mdc-tab-body-active tbody tr')];
    };
    const cells = () => rows().map((r) => [...r.querySelectorAll('td')].slice(0, -1).map(text));
    const headings = () => [...el.querySelectorAll('mat-tab-body.mat-mdc-tab-body-active thead th')].map(text);
    const button = (row: Element, label: string) =>
      [...row.querySelectorAll('button')].find((b) => text(b).includes(label) || b.getAttribute('aria-label')?.startsWith(label));
    const tabs = () => [...el.querySelectorAll('[role=tab]')].map(text);
    return { el, rows, cells, headings, button, tabs, navigate, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    purchaseCategories.mockReset().mockReturnValue(of([general, dairy, rent, empty]));
    open.mockReset();
    notify.info.mockReset();
    approvals.pendingFor.mockReset();
  });

  it('puts the raw materials categories on one tab, with what they hold; General is never deleted', async () => {
    const { cells, headings, tabs, rows, button } = await render();

    expect(tabs()).toEqual(['Purchases & raw materialsمشتريات وخامات3', 'General expensesمصروفات عمومية1']);
    expect(headings()).toEqual(['Categoryالفئة', 'Arabic nameالاسم بالعربية', 'Raw materialsالخامات', 'Actions']);
    expect(cells()).toEqual([
      ['General Default · افتراضي', 'عام', '2'],
      ['Dairy', '—', '3'],
      ['Packaging', '—', '0'],
    ]);
    expect(button(rows()[0], 'Delete')).toBeUndefined();
    expect(button(rows()[1], 'Delete')).toBeDefined();
  });

  it('puts the general expenses categories on the other, with where their expenses count', async () => {
    const { cells, headings } = await render('expenses');

    expect(headings()).toEqual([
      'Categoryالفئة', 'Arabic nameالاسم بالعربية', 'Expenses count asتُحتسب المصروفات ضمن', 'Expense itemsبنود المصروفات', 'Actions',
    ]);
    expect(cells()).toEqual([['Rent', 'إيجار', 'Occupation (rent)الإشغال (الإيجار)', '1']]);
  });

  it('keeps the tab in the address', async () => {
    const { page, navigate } = await render();

    page['selectTab'](1);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { tab: 'expenses' }, replaceUrl: true }));
    page['selectTab'](0);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { tab: null } }));
  });

  it('adds one of the tab’s kind, and edits in the category dialog', async () => {
    open.mockReturnValue({ afterClosed: () => of(rent) });
    const raw = await render();
    [...raw.el.querySelectorAll<HTMLButtonElement>('mat-tab-body.mat-mdc-tab-body-active button')].find((b) => text(b).includes('Add category'))!.click();
    expect(open).toHaveBeenLastCalledWith(PurchaseCategoryDialog, expect.objectContaining({ data: { kind: 'RawMaterials' } }));
    raw.button(raw.rows()[1], 'Edit')!.click();
    expect(open).toHaveBeenLastCalledWith(PurchaseCategoryDialog, expect.objectContaining({ data: { category: dairy } }));
    TestBed.resetTestingModule();

    const expenses = await render('expenses');
    [...expenses.el.querySelectorAll<HTMLButtonElement>('mat-tab-body.mat-mdc-tab-body-active button')].find((b) => text(b).includes('Add category'))!.click();
    expect(open).toHaveBeenLastCalledWith(PurchaseCategoryDialog, expect.objectContaining({ data: { kind: 'Expenses' } }));
  });

  it('deletes one in the delete dialog, with the rest of the list to move what is in it to, and reads the list again', async () => {
    open.mockReturnValue({ afterClosed: () => of(true) });
    const { rows, button } = await render();

    button(rows()[1], 'Delete')!.click();
    await new Promise((r) => setTimeout(r));

    expect(open).toHaveBeenCalledWith(CategoryDeleteDialog, {
      data: { category: dairy, categories: [general, dairy, rent, empty] },
      width: '720px',
      maxWidth: '95vw',
    });
    expect(purchaseCategories).toHaveBeenCalledTimes(2);
  });

  it('marks one whose deletion waits for a manager, and locks it', async () => {
    approvals.pendingFor.mockImplementation((id: string) =>
      id === 'dairy' ? { action: 'Delete', module: 'PurchaseCategory', requestedBy: 'Mona' } : undefined,
    );
    const { rows, button } = await render();

    expect(text(rows()[1].querySelector('td')!)).toBe('Dairy Deletion requestedطلب حذف by Mona');
    expect(button(rows()[1], 'Delete')).toBeUndefined();
    expect(button(rows()[1], 'Edit')).toBeUndefined();
    expect(rows()[1].querySelector('[aria-label="Locked: waiting for a manager"]')).not.toBeNull();
  });

  it('says when one holds items of the other kind, filed there before the kinds were kept apart', async () => {
    purchaseCategories.mockReturnValue(of([general, { ...dairy, expenseItems: 1, misplaced: 1 }, { ...rent, materials: 2, misplaced: 2 }]));
    const raw = await render();
    expect(text(raw.rows()[1].querySelector('.misplaced')!)).toBe('1 expense item does not belong here: move or convert it.');
    expect(raw.rows()[0].querySelector('.misplaced')).toBeNull();
    TestBed.resetTestingModule();

    const expenses = await render('expenses');
    expect(text(expenses.rows()[0].querySelector('.misplaced')!)).toBe('2 raw materials do not belong here: move or convert them.');
  });
});
