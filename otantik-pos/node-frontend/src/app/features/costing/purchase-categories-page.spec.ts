import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { CostingApi, PurchaseCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { PurchaseCategoriesPage } from './purchase-categories-page';
import { PurchaseCategoryDialog } from './purchase-category-dialog';

const general: PurchaseCategory = { id: 'general', name: 'General', nameAr: 'عام', expenseLine: 'Controllable', isGeneral: true, materials: 2, expenseItems: 0 };
const dairy: PurchaseCategory = { id: 'dairy', name: 'Dairy', nameAr: null, expenseLine: 'Controllable', isGeneral: false, materials: 3, expenseItems: 0 };
const rent: PurchaseCategory = { id: 'rent', name: 'Rent', nameAr: 'إيجار', expenseLine: 'Occupation', isGeneral: false, materials: 0, expenseItems: 1 };
const empty: PurchaseCategory = { id: 'empty', name: 'Packaging', nameAr: null, expenseLine: 'Controllable', isGeneral: false, materials: 0, expenseItems: 0 };

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the purchase categories', () => {
  const purchaseCategories = vi.fn();
  const deletePurchaseCategory = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { purchaseCategories, deletePurchaseCategory } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
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
  });

  it('lists each with where its expenses count and what is filed under it; General is never deleted', async () => {
    const { rows, button } = await render();

    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 5).map(text))).toEqual([
      ['General Default · افتراضي', 'عام', 'Controllable costsتكاليف قابلة للتحكم', '2', '0'],
      ['Dairy', '—', 'Controllable costsتكاليف قابلة للتحكم', '3', '0'],
      ['Rent', 'إيجار', 'Occupation (rent)الإشغال (الإيجار)', '0', '1'],
      ['Packaging', '—', 'Controllable costsتكاليف قابلة للتحكم', '0', '0'],
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

  it('moves what is in one before deleting it', async () => {
    const { page, rows, button } = await render();

    button(rows()[1], 'Delete')!.click();
    const ask = rows()[2];
    expect(text(ask)).toContain('Delete Dairy? Its 3 items move, with their purchases, to');
    expect(button(ask, 'Move and delete')!.disabled).toBe(true);
    expect(page['others'](dairy).map((c: PurchaseCategory) => c.name)).toEqual(['General', 'Rent', 'Packaging']);

    page['moveTo'] = 'general';
    await page['remove'](dairy);

    expect(deletePurchaseCategory).toHaveBeenCalledWith('dairy', 'general');
    expect(notify.info).toHaveBeenCalledWith('Dairy deleted; what was in it is in General now.');
  });

  it('deletes an empty one straight away after asking', async () => {
    const { page, rows, button } = await render();

    button(rows()[3], 'Delete')!.click();
    expect(text(rows()[4])).toContain('Delete Packaging? Nothing is in it.');
    await page['remove'](empty);

    expect(deletePurchaseCategory).toHaveBeenCalledWith('empty', null);
  });
});
