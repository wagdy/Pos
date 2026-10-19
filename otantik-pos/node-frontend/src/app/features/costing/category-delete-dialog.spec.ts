import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi, ExpenseItem, PurchaseCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { CategoryDeleteDialog } from './category-delete-dialog';

const category = (id: string, name: string, materials: number, expenseItems: number, extra: Partial<PurchaseCategory> = {}): PurchaseCategory => ({
  id, name, nameAr: null, expenseLine: 'Controllable', isGeneral: false, materials, expenseItems, ...extra,
});
const general = category('general', 'General', 2, 1, { isGeneral: true });
const cleaning = category('cleaning', 'Cleaning stock', 2, 0);
const maintenance = category('maintenance', 'Maintenance', 0, 3, { expenseLine: 'Occupation' });
const dairy = category('dairy', 'Dairy', 4, 0);
const packing = category('packing', 'Packing', 1, 2);
const empty = category('empty', 'Spare', 0, 0);
const all = [general, cleaning, maintenance, dairy, packing, empty];

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
  const deletePurchaseCategoryConverting = vi.fn();
  const previewCategoryDeletion = vi.fn();
  const expenseItems = vi.fn();
  const close = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);
  const approvals = { canApprove, requested: vi.fn() };

  async function render(deleting: PurchaseCategory) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { deletePurchaseCategory, deletePurchaseCategoryConverting, previewCategoryDeletion, expenseItems } },
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
    async function choose(domain: string, moveTo: string | null) {
      page['domain'] = domain;
      page['domainChanged']();
      page['moveTo'] = moveTo;
      await refresh();
    }
    return { el, page, refresh, button, choose };
  }

  beforeEach(() => {
    deletePurchaseCategory.mockReset().mockReturnValue(of(undefined));
    deletePurchaseCategoryConverting.mockReset();
    previewCategoryDeletion.mockReset().mockReturnValue(of(preview));
    expenseItems.mockReset().mockReturnValue(
      of([expense('boxes', 'Boxes', 'packing', 'box'), expense('cups', 'Cups', 'packing', null), expense('bags', 'Bags', 'packing', 'bag', true)]),
    );
    close.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
    approvals.requested.mockReset();
  });

  it('moves a category of raw materials as they are, to one that holds raw materials or nothing', async () => {
    const { el, page, button, refresh } = await render(dairy);

    expect(text(el.querySelector('mat-dialog-content p')!)).toBe('Dairy holds 4 raw materials. They move, with their purchases, to another category.');
    expect(page['convert']()).toBe('None');
    expect(page['targets']().map((c: PurchaseCategory) => c.name)).toEqual(['General', 'Cleaning stock', 'Packing', 'Spare']);
    expect(text(el.querySelector('.hint')!)).toBe('They move as they are.');

    // Nothing chosen: said, and nothing sent.
    button('Move and delete')!.click();
    await refresh();
    expect(text(el.querySelector('[role=alert]')!)).toBe('Choose the category they move to.');
    expect(deletePurchaseCategory).not.toHaveBeenCalled();

    page['moveTo'] = 'general';
    button('Move and delete')!.click();
    await refresh();
    expect(deletePurchaseCategory).toHaveBeenCalledWith('dairy', 'general');
    expect(close).toHaveBeenCalledWith(true);
  });

  it('makes raw materials expense items, reviewed first as the server sees it, then done at once for a manager', async () => {
    deletePurchaseCategoryConverting.mockReturnValue(of({ outcome: 'Applied', request: { id: 'r1' } }));
    const { el, page, button, refresh, choose } = await render(cleaning);

    await choose('expenses', 'maintenance');
    expect(page['convert']()).toBe('ToExpenses');
    expect(page['targets']().map((c: PurchaseCategory) => c.name)).toEqual(['General', 'Maintenance', 'Packing', 'Spare']);
    expect(text(el.querySelector('.hint')!)).toBe(
      'Its 2 raw materials become expense items: their past invoice lines are restated as expenses on their own dates, and their stock ledger is taken out.',
    );
    expect(text(el.querySelector('mat-hint')!)).toBe('Their expenses count as Occupation (rent)');

    button('Review the changes')!.click();
    await refresh();
    const sent = previewCategoryDeletion.mock.lastCall!;
    expect(sent[0]).toBe('cleaning');
    expect(sent[1]).toMatchObject({ moveToId: 'maintenance', convert: 'ToExpenses', units: [], reason: null });
    expect(text(el.querySelector('.summary')!)).toBe(`${preview.summary}.`);
    expect([...el.querySelectorAll('.changes tr')].map((r) => [...r.children].map(text))).toEqual([
      ['Bleach', 'Raw material, 12 pieces on hand', 'Expense item'],
      ['Invoice lines', '', '3 lines (L.E 215.00) restated as expenses, each on its invoice’s date'],
    ]);

    button('Delete and convert')!.click();
    await refresh();
    expect(deletePurchaseCategoryConverting.mock.lastCall![1].requestId).toBe(sent[1].requestId);
    expect(notify.info).toHaveBeenCalledWith(`${preview.summary}.`);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('says it of one raw material as of one', async () => {
    const { el, choose } = await render(category('soap', 'Soap', 1, 0));
    await choose('expenses', 'maintenance');

    expect(text(el.querySelector('.hint')!)).toBe(
      'Its raw material becomes an expense item: its past invoice lines are restated as expenses on their own dates, and its stock ledger is taken out.',
    );
  });

  it('shows why the server refuses, and stays open', async () => {
    previewCategoryDeletion.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['Its raw materials cannot be made expenses: Lemons is in a recipe.'] } })),
    );
    const { el, button, refresh, choose } = await render(cleaning);
    await choose('expenses', 'maintenance');

    button('Review the changes')!.click();
    await refresh();

    expect(text(el.querySelector('[role=alert]')!)).toBe('Its raw materials cannot be made expenses: Lemons is in a recipe.');
    expect(close).not.toHaveBeenCalled();
  });

  it('sends an accountant’s for approval, with the reason', async () => {
    canApprove.set(false);
    const request = { id: 'r1', module: 'PurchaseCategory', action: 'Delete' };
    deletePurchaseCategoryConverting.mockReturnValue(of({ outcome: 'Requested', request }));
    const { el, page, button, refresh, choose } = await render(cleaning);
    await choose('expenses', 'maintenance');
    button('Review the changes')!.click();
    await refresh();

    expect(text(el.querySelector('.ask')!)).toContain('A manager approves this');
    page['reason'] = ' Cleaning is an expense ';
    button('Send for approval')!.click();
    await refresh();

    expect(deletePurchaseCategoryConverting.mock.lastCall![1].reason).toBe('Cleaning is an expense');
    expect(approvals.requested).toHaveBeenCalledWith({ outcome: 'Requested', request });
    expect(close).toHaveBeenCalledWith(true);
  });

  it('makes the expense items in use raw materials, each counted as it is told', async () => {
    const { el, page, button, refresh, choose } = await render(packing);

    // Holding both, it starts as each is; as raw materials, its two expense items in use convert.
    expect(page['domain']).toBe('keep');
    await choose('stock', 'dairy');
    expect(page['convert']()).toBe('ToStock');
    expect([...el.querySelectorAll('.units tbody tr')].map((r) => text(r.querySelector('td')!))).toEqual(['Boxes', 'Cups']);
    expect(page['rows']().map((r: { purchaseUnit: string }) => r.purchaseUnit)).toEqual(['box', 'piece']);

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
    expect(previewCategoryDeletion.mock.lastCall![1].units).toEqual([
      { expenseItemId: 'boxes', unit: 'Piece', purchaseUnit: 'box', purchaseUnitSize: 50 },
      { expenseItemId: 'cups', unit: 'Piece', purchaseUnit: 'sleeve', purchaseUnitSize: 25 },
    ]);
  });

  it('deletes an empty one straight away', async () => {
    const { el, button, refresh } = await render(empty);

    expect(text(el.querySelector('mat-dialog-content')!)).toBe('Nothing is in Spare. It is deleted straight away.');
    button('Delete')!.click();
    await refresh();

    expect(deletePurchaseCategory).toHaveBeenCalledWith('empty', null);
    expect(close).toHaveBeenCalledWith(true);
  });
});
