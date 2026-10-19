import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
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
  const deletePurchaseCategory = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { refresh: vi.fn(), pendingFor: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { purchaseCategories, deletePurchaseCategory } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: Approvals, useValue: approvals },
      ],
    });
    const fixture = TestBed.createComponent(PurchaseCategoriesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => {
      fixture.detectChanges();
      return [...el.querySelectorAll('tbody tr')];
    };
    const button = (row: Element, label: string) =>
      [...row.querySelectorAll('button')].find((b) => text(b).includes(label) || b.getAttribute('aria-label')?.startsWith(label));
    return { rows, button, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    purchaseCategories.mockReset().mockReturnValue(of([general, dairy, rent, empty]));
    deletePurchaseCategory.mockReset().mockReturnValue(of(undefined));
    open.mockReset();
    notify.info.mockReset();
    approvals.pendingFor.mockReset();
  });

  it('lists each with its kind, where its expenses count and what is filed under it; General is never deleted', async () => {
    const { rows, button } = await render();

    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 6).map(text))).toEqual([
      ['General Default · افتراضي', 'عام', 'Raw materialsخامات ومشتريات', '—', '2', '0'],
      ['Dairy', '—', 'Raw materialsخامات ومشتريات', '—', '3', '0'],
      ['Rent', 'إيجار', 'General expensesمصروفات عمومية', 'Occupation (rent)الإشغال (الإيجار)', '0', '1'],
      ['Packaging', '—', 'Raw materialsخامات ومشتريات', '—', '0', '0'],
    ]);
    expect(button(rows()[0], 'Delete')).toBeUndefined();
    expect(button(rows()[1], 'Delete')).toBeDefined();
  });

  it('adds and edits in the category dialog', async () => {
    open.mockReturnValue({ afterClosed: () => of(rent) });
    const { page, rows, button } = await render();

    await page['add']();
    expect(open).toHaveBeenLastCalledWith(PurchaseCategoryDialog, expect.objectContaining({ data: {} }));
    button(rows()[2], 'Edit')!.click();
    expect(open).toHaveBeenLastCalledWith(PurchaseCategoryDialog, expect.objectContaining({ data: { category: rent } }));
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
    const { rows } = await render();

    expect(text(rows()[1].querySelector('.misplaced')!)).toBe('1 expense item does not belong here: move or convert it.');
    expect(text(rows()[2].querySelector('.misplaced')!)).toBe('2 raw materials do not belong here: move or convert them.');
    expect(rows()[0].querySelector('.misplaced')).toBeNull();
  });
});
