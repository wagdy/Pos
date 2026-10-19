import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CategoryKind, CostingApi, ExpenseItem, PurchaseCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { CategoryDeleteDialog } from './category-delete-dialog';

const category = (
  id: string, name: string, kind: CategoryKind, materials: number, expenseItems: number, extra: Partial<PurchaseCategory> = {},
): PurchaseCategory => ({ id, name, nameAr: null, expenseLine: 'Controllable', isGeneral: false, materials, expenseItems, kind, misplaced: 0, ...extra });
const general = category('general', 'General', 'RawMaterials', 2, 0, { isGeneral: true });
const generalExpenses = category('general-expenses', 'General expenses', 'Expenses', 0, 1, { isGeneral: true });
const cleaning = category('cleaning', 'Cleaning stock', 'RawMaterials', 2, 0);
const maintenance = category('maintenance', 'Maintenance', 'Expenses', 0, 3, { expenseLine: 'Occupation' });
const dairy = category('dairy', 'Dairy', 'RawMaterials', 4, 0);
const packing = category('packing', 'Packing', 'RawMaterials', 1, 2, { misplaced: 2 });
const empty = category('empty', 'Spare', 'RawMaterials', 0, 0);
const all = [general, generalExpenses, cleaning, maintenance, dairy, packing, empty];

function expense(id: string, name: string, categoryId: string, unit: string | null, isArchived = false): ExpenseItem {
  return { id, name, nameAr: null, categoryId, category: '', categoryAr: null, unit, isArchived, invoices: 0, postedTotal: 0, lastPostedAtUtc: null };
}

const preview = {
  summary: 'Category Cleaning stock deleted: 2 raw materials become expense items in Maintenance',
  changes: [
    { label: 'Bleach', before: 'Raw material, 12 pieces on hand', after: 'Expense item' },
    { label: 'Invoice lines', before: null, after: '3 lines (L.E 215.00) restated as expenses, each on its invoice’s date' },
  ],
  converts: true,
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('deleting a purchase category', () => {
  const deletePurchaseCategory = vi.fn();
  const previewCategoryDeletion = vi.fn();
  const expenseItems = vi.fn();
  const close = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);
  const approvals = { canApprove, requested: vi.fn() };

  async function render(deleting: PurchaseCategory) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { deletePurchaseCategory, previewCategoryDeletion, expenseItems } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: { category: deleting, categories: all } },
        { provide: NotifyService, useValue: notify },
        { provide: Approvals, useValue: approvals },
      ],
    });
    const fixture = TestBed.createComponent(CategoryDeleteDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const page = fixture.componentInstance as unknown as Record<string, any>;
    const refresh = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const button = (label: string) => [...el.querySelectorAll<HTMLButtonElement>('mat-dialog-actions button')].find((b) => text(b) === label);
    async function choose(materialsTo: string | null, expenseItemsTo: string | null = null) {
      page['materialsTo'] = materialsTo;
      page['expenseItemsTo'] = expenseItemsTo;
      await refresh();
    }
    const hints = () => [...el.querySelectorAll('.hint')].map(text);
    return { el, page, refresh, button, choose, hints };
  }

  beforeEach(() => {
    deletePurchaseCategory.mockReset().mockReturnValue(of({ outcome: 'Applied', request: null }));
    previewCategoryDeletion.mockReset().mockReturnValue(of(preview));
    expenseItems.mockReset().mockReturnValue(
      of([expense('boxes', 'Boxes', 'packing', 'box'), expense('cups', 'Cups', 'packing', null), expense('bags', 'Bags', 'packing', 'bag', true)]),
    );
    close.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
    approvals.requested.mockReset();
  });

  it('moves raw materials as they are to a raw materials category, offered apart from the expenses ones', async () => {
    const { el, page, button, refresh, choose, hints } = await render(dairy);

    expect(el.querySelectorAll('mat-select').length).toBe(1);
    expect(text(el.querySelector('mat-label')!)).toBe('Its 4 raw materials go to · تُنقل الخامات إلى');
    expect(page['targets']('RawMaterials').map((c: PurchaseCategory) => c.name)).toEqual(['General', 'Cleaning stock', 'Packing', 'Spare']);
    expect(page['targets']('Expenses').map((c: PurchaseCategory) => c.name)).toEqual(['General expenses', 'Maintenance']);

    // Nothing chosen: said, and nothing sent.
    button('Move and delete')!.click();
    await refresh();
    expect(text(el.querySelector('[role=alert]')!)).toBe('Choose where its raw materials go.');
    expect(deletePurchaseCategory).not.toHaveBeenCalled();

    await choose('general');
    expect(hints()).toEqual(['They move as they are.']);
    button('Move and delete')!.click();
    await refresh();
    expect(deletePurchaseCategory).toHaveBeenCalledWith('dairy', expect.objectContaining({ materialsTo: 'general', expenseItemsTo: null, units: [] }));
    expect(previewCategoryDeletion).not.toHaveBeenCalled();
    expect(close).toHaveBeenCalledWith(true);
  });

  it('makes raw materials expense items when sent to an expenses category, reviewed first, then done at once for a manager', async () => {
    const { el, button, refresh, choose, hints } = await render(cleaning);

    await choose('maintenance');
    expect(hints()).toEqual([
      'They become expense items: their past invoice lines are restated as expenses on their own dates, and their stock ledger is taken out.',
    ]);
    expect(text(el.querySelector('mat-hint')!)).toBe('Their expenses count as Occupation (rent)');

    button('Review the changes')!.click();
    await refresh();
    const sent = previewCategoryDeletion.mock.lastCall!;
    expect(sent[0]).toBe('cleaning');
    expect(sent[1]).toMatchObject({ materialsTo: 'maintenance', expenseItemsTo: null, units: [], reason: null });
    expect(text(el.querySelector('.summary')!)).toBe(`${preview.summary}.`);
    expect([...el.querySelectorAll('.changes tr')].map((r) => [...r.children].map(text))).toEqual([
      ['Bleach', 'Raw material, 12 pieces on hand', 'Expense item'],
      ['Invoice lines', '', '3 lines (L.E 215.00) restated as expenses, each on its invoice’s date'],
    ]);

    button('Delete and convert')!.click();
    await refresh();
    expect(deletePurchaseCategory.mock.lastCall![1].requestId).toBe(sent[1].requestId);
    expect(notify.info).toHaveBeenCalledWith(`${preview.summary}.`);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('says it of one raw material as of one', async () => {
    const { choose, hints } = await render(category('soap', 'Soap', 'RawMaterials', 1, 0));
    await choose('maintenance');

    expect(hints()).toEqual([
      'It becomes an expense item: its past invoice lines are restated as expenses on their own dates, and its stock ledger is taken out.',
    ]);
  });

  it('shows why the server refuses, and stays open', async () => {
    previewCategoryDeletion.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['Its raw materials cannot be made expenses: Lemons is in a recipe.'] } })),
    );
    const { el, button, refresh, choose } = await render(cleaning);
    await choose('maintenance');

    button('Review the changes')!.click();
    await refresh();

    expect(text(el.querySelector('[role=alert]')!)).toBe('Its raw materials cannot be made expenses: Lemons is in a recipe.');
    expect(close).not.toHaveBeenCalled();
  });

  it('sends an accountant’s for approval, with the reason', async () => {
    canApprove.set(false);
    const request = { id: 'r1', module: 'PurchaseCategory', action: 'Delete' };
    deletePurchaseCategory.mockReturnValue(of({ outcome: 'Requested', request }));
    const { el, page, button, refresh, choose } = await render(cleaning);
    await choose('maintenance');
    button('Review the changes')!.click();
    await refresh();

    expect(text(el.querySelector('.ask')!)).toContain('A manager approves this');
    page['reason'] = ' Cleaning is an expense ';
    button('Send for approval')!.click();
    await refresh();

    expect(deletePurchaseCategory.mock.lastCall![1].reason).toBe('Cleaning is an expense');
    expect(approvals.requested).toHaveBeenCalledWith({ outcome: 'Requested', request });
    expect(close).toHaveBeenCalledWith(true);
  });

  it('sends each kind of a mixed category to its own target, the expense items made raw materials as each is told', async () => {
    const { el, page, button, refresh, choose, hints } = await render(packing);

    expect([...el.querySelectorAll('mat-label')].map(text).slice(0, 2)).toEqual([
      'Its 1 raw material go to · تُنقل الخامات إلى',
      'Its 2 expense items go to · تُنقل بنود المصروفات إلى',
    ]);
    // Each kind to its own: nothing converts.
    await choose('dairy', 'maintenance');
    expect(page['converts']()).toBe(false);
    expect(hints()).toEqual(['They move as they are.', 'They move as they are.']);

    // The expense items to a raw materials category: the two in use convert, each with its unit.
    await choose('dairy', 'dairy');
    expect(page['converts']()).toBe(true);
    expect([...el.querySelectorAll('.units tbody tr')].map((r) => text(r.querySelector('td')!))).toEqual(['Boxes', 'Cups']);
    button('Review the changes')!.click();
    await refresh();
    expect(text(el.querySelector('[role=alert]')!)).toBe('Say what Boxes is counted in, what it is bought by, and how much one holds.');
    expect(previewCategoryDeletion).not.toHaveBeenCalled();

    page['rows']()[0].unit = 'Piece';
    page['rows']()[0].purchaseUnitSize = 50;
    page['rows']()[1].unit = 'Piece';
    page['rows']()[1].purchaseUnit = 'sleeve';
    page['rows']()[1].purchaseUnitSize = 25;
    button('Review the changes')!.click();
    await refresh();
    expect(previewCategoryDeletion.mock.lastCall![1]).toMatchObject({
      materialsTo: 'dairy',
      expenseItemsTo: 'dairy',
      units: [
        { expenseItemId: 'boxes', unit: 'Piece', purchaseUnit: 'box', purchaseUnitSize: 50 },
        { expenseItemId: 'cups', unit: 'Piece', purchaseUnit: 'sleeve', purchaseUnitSize: 25 },
      ],
    });
  });

  it('deletes an empty one straight away', async () => {
    const { el, button, refresh } = await render(empty);

    expect(text(el.querySelector('mat-dialog-content')!)).toBe('Nothing is in Spare. It is deleted straight away.');
    button('Delete')!.click();
    await refresh();

    expect(deletePurchaseCategory).toHaveBeenCalledWith('empty', expect.objectContaining({ materialsTo: null, expenseItemsTo: null }));
    expect(close).toHaveBeenCalledWith(true);
  });
});
