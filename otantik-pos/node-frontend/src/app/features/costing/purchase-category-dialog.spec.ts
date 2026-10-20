import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { CostingApi, PurchaseCategory } from '../../core/costing/costing.api';
import { PurchaseCategoryDialog, PurchaseCategoryDialogData } from './purchase-category-dialog';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the purchase category dialog', () => {
  const savePurchaseCategory = vi.fn();
  const close = vi.fn();

  async function render(data: PurchaseCategoryDialogData) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { savePurchaseCategory } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    });
    const fixture = TestBed.createComponent(PurchaseCategoryDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const page = fixture.componentInstance as unknown as Record<string, any>;
    const save = () => [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b) === 'Save')!;
    return { fixture, el, page, save };
  }

  beforeEach(() => {
    savePurchaseCategory.mockReset().mockImplementation((id: string, body: object) => of({ id, ...body }));
    close.mockReset();
  });

  it('adds one of the kind it is opened for without asking it: a general expenses one asks where its expenses count', async () => {
    const { fixture, el, page, save } = await render({ kind: 'Expenses' });

    expect(text(el.querySelector('h2')!)).toBe('Add a general expenses category إضافة قسم مصروفات');
    expect(el.querySelector('mat-radio-group')).toBeNull();
    expect(el.querySelector('mat-select[formcontrolname=expenseLine]')).not.toBeNull();

    page['form'].patchValue({ name: 'Cleaning', expenseLine: 'Controllable' });
    fixture.detectChanges();
    expect(save().disabled).toBe(false);
    await page['save']();
    expect(savePurchaseCategory).toHaveBeenCalledWith(expect.any(String), { name: 'Cleaning', nameAr: null, expenseLine: 'Controllable', kind: 'Expenses' });
  });

  it('adds a raw materials one with no expense line to ask', async () => {
    const { el, page } = await render({ kind: 'RawMaterials' });

    expect(text(el.querySelector('h2')!)).toBe('Add a raw materials category إضافة قسم خامات');
    expect(el.querySelector('mat-radio-group')).toBeNull();
    expect(el.querySelector('mat-select[formcontrolname=expenseLine]')).toBeNull();
    page['form'].patchValue({ name: 'Dairy' });
    await page['save']();
    expect(savePurchaseCategory.mock.lastCall![1]).toMatchObject({ name: 'Dairy', kind: 'RawMaterials' });
  });

  it('asks the kind when opened for none, and holds the save until it is chosen', async () => {
    const { fixture, el, page, save } = await render({});

    expect([...el.querySelectorAll('mat-radio-button')].map(text)).toEqual(['Raw materials · خامات ومشتريات', 'General expenses · مصروفات عمومية']);
    page['form'].patchValue({ name: 'Cleaning' });
    fixture.detectChanges();
    expect(save().disabled).toBe(true);
    page['form'].patchValue({ kind: 'Expenses' });
    fixture.detectChanges();
    expect(save().disabled).toBe(false);
  });

  it('shows the kind of one being changed, fixed for a General', async () => {
    const general = { id: 'g', name: 'General', nameAr: 'عام', expenseLine: 'Controllable', isGeneral: true, materials: 1, expenseItems: 0, kind: 'RawMaterials', misplaced: 0 } as PurchaseCategory;
    const { el, page } = await render({ category: general });

    expect(text(el.querySelector('h2')!)).toBe('Edit category تعديل الفئة');
    expect(el.querySelector('mat-radio-group')).not.toBeNull();
    expect(page['form'].controls.kind.disabled).toBe(true);
    expect(text(el.querySelector('.kind .hint')!)).toBe('Where anything not yet sorted goes: it stays raw materials.');
  });
});
