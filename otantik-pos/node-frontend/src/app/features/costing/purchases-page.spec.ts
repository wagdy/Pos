import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import {
  CostingApi,
  ExpenseItem,
  MaterialCost,
  PurchaseInvoice,
  PurchaseInvoiceSummary,
  SavePurchaseInvoice,
  SafeVoucherInput,
  Supplier,
  SupplierBalance,
  purchaseUnits,
} from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { ExpenseItemDialog } from './expense-item-dialog';
import { PurchasesPage } from './purchases-page';

const oil = { id: 'oil', name: 'Frying oil', unit: 'Millilitre', purchaseUnit: 'L', purchaseUnitSize: 1000 } as MaterialCost;
const bun = { id: 'bun', name: 'Burger bun', unit: 'Piece', purchaseUnit: 'piece', purchaseUnitSize: 1 } as MaterialCost;
const tomatoes = { id: 'tomatoes', name: 'Tomatoes', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000 } as MaterialCost;
const all = [oil, bun, tomatoes];

function expenseItem(id: string, name: string, unit: string | null = null): ExpenseItem {
  return {
    id, name, nameAr: null, categoryId: 'utilities', category: 'Utilities', categoryAr: null, unit,
    isArchived: false, invoices: 0, postedTotal: 0, lastPostedAtUtc: null,
  };
}
const power = expenseItem('power', 'Electricity');
const spray = expenseItem('spray', 'Cleaning spray', 'bottle');

function supplier(id: string, name: string): Supplier {
  return { id, name, contactPerson: null, phone: null, email: null, notes: null, isArchived: false, invoices: 0, postedTotal: 0, lastPostedAtUtc: null };
}
const nileFarms = supplier('nile', 'Nile Farms');
const deltaOils = supplier('delta', 'Delta Oils');

// What the till answers with: the invoice as saved, each line also in the material's own unit.
function asInvoice(
  id: string,
  body: Omit<SavePurchaseInvoice, 'paymentMethod' | 'revenueDate'> & Partial<SavePurchaseInvoice>,
  status: PurchaseInvoice['status'] = 'Draft',
): PurchaseInvoice {
  return {
    id,
    status,
    paymentMethod: null,
    revenueDate: null,
    paymentStatus: status === 'Posted' ? 'Paid' : 'NotPosted',
    ...body,
    supplierName: [nileFarms, deltaOils].find((s) => s.id === body.supplierId)?.name ?? null,
    createdAtUtc: '2026-10-07T09:00:00Z',
    createdBy: 'Omar',
    postedAtUtc: status === 'Posted' ? '2026-10-07T09:05:00Z' : null,
    postedBy: status === 'Posted' ? 'Omar' : null,
    total: body.lines.reduce((sum, l) => sum + (l.cost ?? 0), 0),
    inventoryTotal: body.lines.filter((l) => l.itemType === 'Inventory').reduce((sum, l) => sum + (l.cost ?? 0), 0),
    expenseTotal: body.lines.filter((l) => l.itemType === 'Expense').reduce((sum, l) => sum + (l.cost ?? 0), 0),
    attachment: null,
    lines: body.lines.map((l) => {
      const filed = { categoryId: null, category: null, categoryAr: null };
      if (l.itemType === 'Expense') {
        const item = [power, spray].find((e) => e.id === l.expenseItemId)!;
        return { ...l, ...filed, unitLabel: l.unitLabel ?? 'unit', material: item.name, unit: null, quantity: l.enteredQuantity };
      }
      const material = all.find((m) => m.id === l.rawMaterialId)!;
      return { ...l, ...filed, unitLabel: l.unitLabel ?? '', material: material.name, unit: material.unit, quantity: Math.round(l.enteredQuantity * l.unitSize * 1000) / 1000 };
    }),
  };
}

describe('entering a supplier invoice', () => {
  const materials = vi.fn();
  const purchaseInvoices = vi.fn();
  const purchaseInvoice = vi.fn();
  const suppliers = vi.fn();
  const expenseItems = vi.fn();
  const savePurchaseInvoice = vi.fn();
  const postPurchaseInvoice = vi.fn();
  const discardPurchaseInvoice = vi.fn();
  const attachPurchaseInvoiceFile = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            materials,
            purchaseInvoices,
            purchaseInvoice,
            suppliers,
            expenseItems,
            savePurchaseInvoice,
            postPurchaseInvoice,
            discardPurchaseInvoice,
            attachPurchaseInvoiceFile,
            supplierBalances: () => of([]),
          },
        },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PurchasesPage);
    await fixture.whenStable();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  function fill(page: Record<string, any>, lines: { rawMaterialId: string | null; quantity: number; cost: number | null; unitSize?: number }[]) {
    // Paid from the safe, unless the test says otherwise.
    page['paymentMethod'] ??= 'Safe';
    page['lines'].set(
      lines.map((l) => ({ itemType: l.rawMaterialId ? 'Inventory' : null, expenseItemId: null, unitSize: null, unitLabel: '', ...l })),
    );
    page['touch']();
  }

  const blank = { itemType: null, rawMaterialId: null, expenseItemId: null, quantity: null, unitSize: null, unitLabel: '', cost: null };

  // The lines of the last draft saved.
  const savedLines = () => (savePurchaseInvoice.mock.lastCall![1] as SavePurchaseInvoice).lines;

  beforeEach(() => {
    materials.mockReset().mockReturnValue(of(all));
    purchaseInvoices.mockReset().mockReturnValue(of([]));
    purchaseInvoice.mockReset();
    suppliers.mockReset().mockReturnValue(of([deltaOils, nileFarms]));
    expenseItems.mockReset().mockReturnValue(of([power, spray]));
    savePurchaseInvoice.mockReset().mockImplementation((id: string, body: SavePurchaseInvoice) => of(asInvoice(id, body)));
    postPurchaseInvoice
      .mockReset()
      .mockImplementation((id: string) => of(asInvoice(id, savePurchaseInvoice.mock.lastCall![1], 'Posted')));
    discardPurchaseInvoice.mockReset().mockReturnValue(of(undefined));
    attachPurchaseInvoiceFile.mockReset();
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('needs to know how the invoice is paid: on credit from a supplier, from a day’s takings on a day', async () => {
    const page = await render();
    fill(page, [{ rawMaterialId: 'oil', quantity: 20, cost: 1900 }]);
    page['paymentMethod'] = null;
    page['touch']();
    expect(page['ready']()).toBe(false);

    page['paymentMethod'] = 'Credit';
    page['touch']();
    expect(page['ready']()).toBe(false);
    page['supplierEntered'](nileFarms);
    expect(page['ready']()).toBe(true);

    page['paymentMethod'] = 'DayRevenue';
    page['revenueDate'] = '2026-10-06';
    page['touch']();
    await page['save']();
    expect(savePurchaseInvoice.mock.lastCall![1]).toMatchObject({ paymentMethod: 'DayRevenue', revenueDate: '2026-10-06', supplierId: 'nile' });
  });

  it('says when its cash payment waits for a manager', async () => {
    postPurchaseInvoice.mockImplementation((id: string) =>
      of({ ...asInvoice(id, { supplierId: null, supplierInvoiceNumber: null, invoiceDate: null, lines: [] }, 'Posted'), paymentMethod: 'Safe', paymentStatus: 'Pending', total: 95 }),
    );
    const page = await render();
    fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);
    await page['save']();

    await page['confirm']();

    expect(notify.info).toHaveBeenCalledWith(expect.stringContaining('Its payment waits for a manager’s approval'));
  });

  it('saves a draft and shows it for review; only Confirm & post takes the stock in', async () => {
    const page = await render();
    page['supplierEntered'](deltaOils);
    page['supplierInvoiceNumber'] = 'D-77';
    page['invoiceDate'] = '2026-10-06';
    fill(page, [
      { rawMaterialId: 'oil', quantity: 20, cost: 1900 },
      { rawMaterialId: 'bun', quantity: 50, cost: null },
    ]);
    const id = page['invoiceId'];
    expect(page['total']()).toBe(1900);

    await page['save']();

    expect(savePurchaseInvoice).toHaveBeenCalledWith(id, {
      supplierId: 'delta',
      supplierInvoiceNumber: 'D-77',
      invoiceDate: '2026-10-06',
      paymentMethod: 'Safe',
      revenueDate: null,
      lines: [
        { itemType: 'Inventory', rawMaterialId: 'oil', expenseItemId: null, enteredQuantity: 20, unitLabel: 'L', unitSize: 1000, cost: 1900 },
        { itemType: 'Inventory', rawMaterialId: 'bun', expenseItemId: null, enteredQuantity: 50, unitLabel: 'piece', unitSize: 1, cost: null },
      ],
    });
    expect(postPurchaseInvoice).not.toHaveBeenCalled();
    expect(page['review']()).toMatchObject({ id, status: 'Draft', total: 1900, supplierName: 'Delta Oils' });
    expect(page['review']().lines.map((l: { quantity: number }) => l.quantity)).toEqual([20000, 50]);

    await page['confirm']();

    expect(postPurchaseInvoice).toHaveBeenCalledExactlyOnceWith(id);
    expect(notify.info).toHaveBeenCalledWith(expect.stringContaining('Posted: 2 lines'));
    // A clean, new invoice for the next delivery.
    expect(page['review']()).toBeNull();
    expect(page['invoiceId']).not.toBe(id);
    expect([page['supplierEntry'], page['supplierId']]).toEqual(['', null]);
    expect(page['lines']()).toEqual([blank]);
  });

  it('goes back to edit with nothing lost, and saves the same draft again', async () => {
    const page = await render();
    fill(page, [{ rawMaterialId: 'tomatoes', quantity: 5, cost: 100 }]);
    const id = page['invoiceId'];

    await page['save']();
    page['backToEdit']();
    expect(page['review']()).toBeNull();
    expect(page['lines']()[0]).toMatchObject({ rawMaterialId: 'tomatoes', quantity: 5, cost: 100 });

    page['lines']()[0].cost = 110;
    page['touch']();
    await page['save']();

    expect(savePurchaseInvoice.mock.calls.map((call) => call[0])).toEqual([id, id]);
    expect(savedLines()[0].cost).toBe(110);
    expect(postPurchaseInvoice).not.toHaveBeenCalled();
  });

  it('stays on the review when posting fails, and posts the same invoice on the retry', async () => {
    const page = await render();
    fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);
    await page['save']();
    const id = page['invoiceId'];

    postPurchaseInvoice.mockReturnValueOnce(throwError(() => new Error('lost')));
    await page['confirm']();
    expect(notify.error).toHaveBeenCalled();
    expect(page['review']()).not.toBeNull();

    await page['confirm']();
    expect(postPurchaseInvoice.mock.calls.map((call) => call[0])).toEqual([id, id]);
    expect(page['invoiceId']).not.toBe(id);
  });

  it('keeps the review on screen when the draft cannot be saved', async () => {
    const page = await render();
    fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);
    savePurchaseInvoice.mockReturnValueOnce(throwError(() => new Error('refused')));

    await page['save']();

    expect(page['review']()).toBeNull();
    expect(notify.error).toHaveBeenCalled();
    expect(page['lines']()[0]).toMatchObject({ rawMaterialId: 'oil', quantity: 1 });
  });

  it('takes the invoice as written: 5 kg for L.E 100 is 5,000 g at L.E 20 a kg', async () => {
    const page = await render();
    fill(page, [{ rawMaterialId: 'tomatoes', quantity: 5, cost: 100 }]);

    expect(page['stockAdded'](page['lines']()[0])).toBe('5,000 g');
    expect(page['unitPrice'](page['lines']()[0])).toBe(20);
    await page['save']();

    expect(savedLines()).toEqual([{ itemType: 'Inventory', rawMaterialId: 'tomatoes', expenseItemId: null, enteredQuantity: 5, unitLabel: 'kg', unitSize: 1000, cost: 100 }]);
    expect(page['review']().lines[0].quantity).toBe(5000);
  });

  it('takes a line billed in grams as it is, and prices it per kg beside the average', async () => {
    const page = await render();
    fill(page, [
      { rawMaterialId: 'tomatoes', quantity: 2500, cost: 62.5, unitSize: 1 },
      { rawMaterialId: 'oil', quantity: 0.1, cost: 9.5 },
    ]);

    expect(page['unitPrice'](page['lines']()[0])).toBe(25);
    // 0.1 L is 100 ml exactly, not 100.00000000000001.
    expect(page['baseQuantity'](page['lines']()[1])).toBe(100);
    await page['save']();

    expect(savedLines()).toEqual([
      { itemType: 'Inventory', rawMaterialId: 'tomatoes', expenseItemId: null, enteredQuantity: 2500, unitLabel: 'g', unitSize: 1, cost: 62.5 },
      { itemType: 'Inventory', rawMaterialId: 'oil', expenseItemId: null, enteredQuantity: 0.1, unitLabel: 'L', unitSize: 1000, cost: 9.5 },
    ]);
  });

  it('does not offer prep items: they are made in the kitchen, not bought', async () => {
    const dough = { id: 'dough', name: 'Pizza dough', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000, kind: 'Prep' } as MaterialCost;
    materials.mockReturnValue(of([...all, dough]));
    const page = await render();

    expect(page['materials']().map((m: MaterialCost) => m.id)).toEqual(['oil', 'bun', 'tomatoes']);
  });

  it('starts a line in what the material is bought by, and offers its own unit too', async () => {
    const page = await render();
    page['lines'].set([{ ...blank }]);
    page['entered'](page['lines']()[0], tomatoes);

    expect(page['lines']()[0].unitSize).toBe(1000);
    const labels = (m: Partial<MaterialCost>) => purchaseUnits(m as MaterialCost).map((u) => u.label);
    expect(labels(tomatoes)).toEqual(['kg', 'g']);
    expect(labels({ unit: 'Gram', purchaseUnit: 'g', purchaseUnitSize: 1 })).toEqual(['g', 'kg']);
    expect(labels({ unit: 'Gram', purchaseUnit: 'sack', purchaseUnitSize: 25000 })).toEqual(['sack', 'kg', 'g']);
    expect(labels({ unit: 'Piece', purchaseUnit: 'pack of 20', purchaseUnitSize: 20 })).toEqual(['pack of 20', 'pc']);
    expect(labels(oil)).toEqual(['L', 'ml']);
  });

  it('will not save a line without a material or a quantity, or a material twice', async () => {
    const page = await render();

    fill(page, [{ rawMaterialId: 'oil', quantity: 0, cost: 10 }]);
    expect(page['ready']()).toBe(false);
    fill(page, [
      { rawMaterialId: 'oil', quantity: 1, cost: 10 },
      { rawMaterialId: 'oil', quantity: 2, cost: 20 },
    ]);
    expect(page['ready']()).toBe(false);

    await page['save']();
    expect(savePurchaseInvoice).not.toHaveBeenCalled();
  });

  describe('the supplier', () => {
    it('is chosen from the list, which narrows as a name is typed', async () => {
      const page = await render();

      expect(page['supplierMatches']()).toEqual([deltaOils, nileFarms]);
      page['supplierEntry'] = 'nil';
      page['supplierEntered']('nil');
      expect(page['supplierMatches']()).toEqual([nileFarms]);
      expect(page['typedNewSupplier']()).toBe('nil');

      page['supplierEntry'] = nileFarms;
      page['supplierEntered'](nileFarms);
      expect(page['supplierId']).toBe('nile');
    });

    it('is the one named when its name is typed in full, capitals and spaces aside', async () => {
      const page = await render();

      page['supplierEntry'] = '  nile   FARMS ';
      page['supplierEntered']('  nile   FARMS ');

      expect(page['supplierId']).toBe('nile');
      expect(page['typedNewSupplier']()).toBeNull();
    });

    it('holds the save while a typed name is neither chosen nor added', async () => {
      const page = await render();
      fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);
      expect(page['ready']()).toBe(true);

      page['supplierEntry'] = 'Sinai Dairy';
      page['supplierEntered']('Sinai Dairy');

      expect(page['supplierUnresolved']()).toBe(true);
      expect(page['ready']()).toBe(false);
      await page['save']();
      expect(savePurchaseInvoice).not.toHaveBeenCalled();
    });

    it('is added from the invoice in a quick dialog, chosen, and the lines kept', async () => {
      const sinai = supplier('sinai', 'Sinai Dairy');
      open.mockReturnValue({ afterClosed: () => of(sinai) });
      const page = await render();
      fill(page, [
        { rawMaterialId: 'oil', quantity: 20, cost: 1900 },
        { rawMaterialId: 'bun', quantity: 50, cost: null },
      ]);
      const id = page['invoiceId'];
      suppliers.mockReturnValue(of([deltaOils, nileFarms, sinai]));

      page['supplierEntered']({ addNew: 'Sinai Dairy' });
      await vi.waitFor(() => expect(page['supplierId']).toBe('sinai'));

      expect(open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { name: 'Sinai Dairy', fromInvoice: true } }));
      expect(page['supplierEntry']).toEqual(sinai);
      expect(page['lines']().map((l: { rawMaterialId: string; quantity: number }) => [l.rawMaterialId, l.quantity])).toEqual([
        ['oil', 20],
        ['bun', 50],
      ]);
      expect(page['invoiceId']).toBe(id);
      expect(page['supplierMatches']()).toContainEqual(sinai);

      await page['save']();
      expect(savePurchaseInvoice).toHaveBeenCalledWith(id, expect.objectContaining({ supplierId: 'sinai' }));
    });

    it('keeps the typed name when adding it is cancelled', async () => {
      open.mockReturnValue({ afterClosed: () => of(undefined) });
      const page = await render();

      await page['addSupplier']('Sinai Dairy');

      expect([page['supplierEntry'], page['supplierId']]).toEqual(['Sinai Dairy', null]);
      expect(page['supplierUnresolved']()).toBe(true);
    });
  });

  describe('the paper', () => {
    const photo = () => new File(['\xff\xd8\xff'], 'invoice.jpg', { type: 'image/jpeg' });
    const picker = (file: unknown) => ({ files: [file], value: 'C:\\fakepath\\invoice.jpg' }) as unknown as HTMLInputElement;

    it('saves the invoice as a draft first, so the photo has one to belong to', async () => {
      attachPurchaseInvoiceFile.mockImplementation((id: string) =>
        of({ ...asInvoice(id, { supplierId: null, supplierInvoiceNumber: null, invoiceDate: null, lines: [] }), attachment: { fileName: 'invoice.jpg' } }),
      );
      const page = await render();
      // One line done, one still being entered: the draft holds the finished one.
      fill(page, [
        { rawMaterialId: 'oil', quantity: 20, cost: 1900 },
        { rawMaterialId: null, quantity: 2, cost: null },
      ]);
      const id = page['invoiceId'];
      const input = picker(photo());

      await page['attach'](input);

      expect(savePurchaseInvoice).toHaveBeenCalledWith(id, expect.objectContaining({ lines: [expect.objectContaining({ rawMaterialId: 'oil' })] }));
      expect(attachPurchaseInvoiceFile).toHaveBeenCalledWith(id, expect.any(File));
      expect(savePurchaseInvoice.mock.invocationCallOrder[0]).toBeLessThan(attachPurchaseInvoiceFile.mock.invocationCallOrder[0]);
      expect(page['paper']()).toBe('invoice.jpg');
      // Picked again, the same file still fires.
      expect(input.value).toBe('');
      expect(postPurchaseInvoice).not.toHaveBeenCalled();
    });

    it('refuses a file over 10 MB before sending anything', async () => {
      const page = await render();

      await page['attach'](picker({ name: 'scan.pdf', size: 11 * 1024 * 1024 }));

      expect(notify.error).toHaveBeenCalledWith(null, 'The file must be under 10 MB.');
      expect(savePurchaseInvoice).not.toHaveBeenCalled();
      expect(attachPurchaseInvoiceFile).not.toHaveBeenCalled();
    });

    it('says so when the till refuses the file, and keeps the invoice as it was', async () => {
      attachPurchaseInvoiceFile.mockReturnValue(throwError(() => new Error('not an image')));
      const page = await render();
      fill(page, [{ rawMaterialId: 'oil', quantity: 1, cost: 95 }]);

      await page['attach'](picker(photo()));

      expect(notify.error).toHaveBeenCalled();
      expect(page['paper']()).toBeNull();
      expect(page['lines']()[0]).toMatchObject({ rawMaterialId: 'oil', quantity: 1 });
    });
  });

  describe('unfinished drafts', () => {
    const draft: PurchaseInvoiceSummary = {
      id: 'draft-1',
      status: 'Draft',
      supplierId: 'nile',
      supplierName: 'Nile Farms',
      supplierInvoiceNumber: 'F-1001',
      invoiceDate: '2026-10-05',
      createdAtUtc: '2026-10-05T08:00:00Z',
      createdBy: 'Sara',
      postedAtUtc: null,
      postedBy: null,
      total: 100,
      lines: 1,
      attachmentFileName: 'farm.jpg',
      inventoryTotal: 100,
      expenseTotal: 0,
      paymentMethod: null,
      revenueDate: null,
      paymentStatus: 'NotPosted',
    };

    it('are listed, and one continued comes back as it was entered', async () => {
      purchaseInvoices.mockReturnValue(of([draft]));
      purchaseInvoice.mockReturnValue(
        of({
          ...asInvoice('draft-1', {
            supplierId: 'nile',
            supplierInvoiceNumber: 'F-1001',
            invoiceDate: '2026-10-05',
            paymentMethod: 'Credit',
            lines: [{ itemType: 'Inventory', rawMaterialId: 'tomatoes', expenseItemId: null, enteredQuantity: 5, unitLabel: 'kg', unitSize: 1000, cost: 100 }],
          }),
          attachment: { fileName: 'farm.jpg' },
        }),
      );
      const page = await render();
      expect(purchaseInvoices).toHaveBeenCalledWith('Draft');
      expect(page['drafts']()).toEqual([draft]);

      await page['continueDraft']('draft-1');

      expect(page['invoiceId']).toBe('draft-1');
      // Open, it is no longer offered; left, it is again.
      expect(page['drafts']()).toEqual([]);
      expect([page['supplierEntry'], page['supplierId'], page['supplierInvoiceNumber'], page['invoiceDate'], page['paper']()]).toEqual([
        nileFarms,
        'nile',
        'F-1001',
        '2026-10-05',
        'farm.jpg',
      ]);
      expect(page['lines']()).toEqual([
        { entry: tomatoes, itemType: 'Inventory', rawMaterialId: 'tomatoes', expenseItemId: null, quantity: 5, unitSize: 1000, unitLabel: '', cost: 100 },
      ]);

      // How it is paid comes back too.
      expect(page['paymentMethod']).toBe('Credit');
      await page['save']();
      expect(savePurchaseInvoice).toHaveBeenCalledWith('draft-1', expect.objectContaining({ supplierId: 'nile', paymentMethod: 'Credit' }));
      page['reset']();
      expect(page['drafts']()).toEqual([draft]);
    });

    it('can be discarded', async () => {
      purchaseInvoices.mockReturnValueOnce(of([draft])).mockReturnValue(of([]));
      const page = await render();

      await page['discardDraft']('draft-1');

      expect(discardPurchaseInvoice).toHaveBeenCalledWith('draft-1');
      expect(page['drafts']()).toEqual([]);
    });
  });

  describe('a material not on the list yet', () => {
    it('is found as it is typed, or offered to be added', async () => {
      const page = await render();
      const line = { entry: 'tom', rawMaterialId: null, quantity: null, unitSize: null, cost: null };

      expect(page['matches'](line).materials.map((m: MaterialCost) => m.name)).toEqual(['Tomatoes']);
      expect(page['typedNew'](line)).toBe('tom');
      expect(page['typedNew']({ ...line, entry: 'TOMATOES ' })).toBeNull();
      expect(page['typedNew']({ ...line, entry: '  ' })).toBeNull();
    });

    it('is added from the invoice and put on that line, the rest untouched', async () => {
      const sumac = { id: 'sumac', name: 'Sumac', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000 } as MaterialCost;
      open.mockReturnValue({ afterClosed: () => of({ id: 'sumac', name: 'Sumac' }) });
      const page = await render();
      fill(page, [
        { rawMaterialId: 'oil', quantity: 20, cost: 1900 },
        { rawMaterialId: null, quantity: 2, cost: 300 },
      ]);
      const id = page['invoiceId'];
      materials.mockReturnValue(of([...all, sumac]));

      page['entered'](page['lines']()[1], { addNew: 'Sumac' });
      await vi.waitFor(() => expect(page['lines']()[1].rawMaterialId).toBe('sumac'));

      expect(open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { name: 'Sumac' } }));
      expect(page['lines']()[1]).toMatchObject({ quantity: 2, cost: 300, unitSize: 1000, entry: sumac });
      expect(page['lines']()[0]).toMatchObject({ rawMaterialId: 'oil', quantity: 20, cost: 1900 });
      expect(page['invoiceId']).toBe(id);
      expect(page['ready']()).toBe(true);
      expect(postPurchaseInvoice).not.toHaveBeenCalled();
    });

    it('keeps what was typed when adding is cancelled', async () => {
      open.mockReturnValue({ afterClosed: () => of(undefined) });
      const page = await render();
      fill(page, [{ rawMaterialId: null, quantity: 1, cost: 10 }]);

      await page['addMaterial'](page['lines']()[0], 'Sumac');

      expect(page['lines']()[0]).toMatchObject({ entry: 'Sumac', rawMaterialId: null, quantity: 1, cost: 10 });
    });
  });
  describe('expense lines', () => {
    it('go on the invoice beside the stock, need their amount, and are saved as expenses', async () => {
      const page = await render();
      fill(page, [{ rawMaterialId: 'oil', quantity: 20, cost: 1900 }]);
      page['add']();
      const line = page['lines']()[1];
      expect(page['matches']({ ...line, entry: 'elec' })).toEqual({ materials: [], expenses: [power] });
      expect(page['matches']({ ...line, entry: 'utilities' }).expenses).toEqual([power, spray]);

      page['entered'](line, power);
      expect(line).toMatchObject({ itemType: 'Expense', expenseItemId: 'power', rawMaterialId: null, quantity: 1, unitSize: 1, unitLabel: '' });
      // An expense is its amount, or nothing.
      expect(page['ready']()).toBe(false);
      line.cost = 1500;
      page['touch']();
      expect(page['ready']()).toBe(true);
      expect(page['totals']()).toEqual({ stock: 1900, expenses: 1500 });
      expect(page['total']()).toBe(3400);

      await page['save']();
      expect(savedLines()).toEqual([
        { itemType: 'Inventory', rawMaterialId: 'oil', expenseItemId: null, enteredQuantity: 20, unitLabel: 'L', unitSize: 1000, cost: 1900 },
        { itemType: 'Expense', rawMaterialId: null, expenseItemId: 'power', enteredQuantity: 1, unitLabel: null, unitSize: 1, cost: 1500 },
      ]);
      await page['confirm']();
      expect(notify.info).toHaveBeenCalledWith(expect.stringContaining('1 line into stock, 1 expense recorded'));
    });

    it('start in the unit the expense is usually counted in, as many as were typed', async () => {
      const page = await render();
      fill(page, [{ rawMaterialId: null, quantity: 4, cost: 200 }]);
      const line = page['lines']()[0];

      page['entered'](line, spray);
      await page['save']();

      expect(line).toMatchObject({ quantity: 4, unitLabel: 'bottle' });
      expect(page['unitPrice'](line)).toBe(50);
      expect(savedLines()[0]).toMatchObject({ itemType: 'Expense', expenseItemId: 'spray', enteredQuantity: 4, unitLabel: 'bottle' });
    });

    it('are offered for a new name beside a new material, and added from the invoice', async () => {
      const gas = expenseItem('gas', 'Gas', 'cylinder');
      open.mockReturnValue({ afterClosed: () => of(gas) });
      const page = await render();
      const line = page['lines']()[0];
      expect(page['typedNew']({ ...line, entry: 'Gas' })).toBe('Gas');
      expect(page['typedNew']({ ...line, entry: 'electricity' })).toBeNull();
      expenseItems.mockReturnValue(of([power, spray, gas]));

      page['entered'](line, { addNew: 'Gas', as: 'Expense' });
      await vi.waitFor(() => expect(line.expenseItemId).toBe('gas'));

      expect(open).toHaveBeenCalledWith(ExpenseItemDialog, expect.objectContaining({ data: { name: 'Gas', fromInvoice: true } }));
      expect(line).toMatchObject({ itemType: 'Expense', unitLabel: 'cylinder', quantity: 1, entry: gas });
    });

    it('come back as they were when a draft is continued', async () => {
      purchaseInvoice.mockReturnValue(
        of(
          asInvoice('draft-2', {
            supplierId: null,
            supplierInvoiceNumber: null,
            invoiceDate: '2026-10-05',
            lines: [{ itemType: 'Expense', rawMaterialId: null, expenseItemId: 'spray', enteredQuantity: 4, unitLabel: 'bottle', unitSize: 1, cost: 200 }],
          }),
        ),
      );
      const page = await render();

      await page['continueDraft']('draft-2');

      expect(page['lines']()[0]).toMatchObject({ entry: spray, itemType: 'Expense', expenseItemId: 'spray', quantity: 4, unitLabel: 'bottle', cost: 200 });
    });
  });
});

// A supplier paid with no invoice (سداد مورد), switched to at the top of the new invoice screen:
// entered as in the daily batch, from the safe or a day's takings, never on credit.
describe('paying a supplier from the new invoice screen', () => {
  // Nile Farms is owed 1,000 from before; Delta Oils nothing.
  const balances = [
    { supplierId: 'nile', supplier: 'Nile Farms', balance: 1_000 },
    { supplierId: 'delta', supplier: 'Delta Oils', balance: 0 },
  ] as SupplierBalance[];
  const supplierBalances = vi.fn();
  const recordVoucher = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { canApprove: vi.fn(() => true), requested: vi.fn(), refresh: vi.fn() };
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            materials: () => of([tomatoes]),
            purchaseInvoices: () => of([]),
            suppliers: () => of([nileFarms, deltaOils]),
            expenseItems: () => of([]),
            businessToday: () => of({ today: '2026-10-15' }),
            supplierBalances,
            recordVoucher,
          },
        },
        { provide: Approvals, useValue: approvals },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PurchasesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  async function switchToPayment(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }, page: Record<string, any>) {
    page['mode'].set('payment');
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    supplierBalances.mockReset().mockReturnValue(of(balances));
    recordVoucher.mockReset();
    notify.info.mockReset();
    approvals.canApprove.mockReturnValue(true);
    approvals.requested.mockReset();
  });

  it('shows only the supplier owed, the amount, how it is paid and the notes: no lines, never on credit', async () => {
    const { fixture, el, page } = await render();
    await switchToPayment(fixture, page);

    expect(el.querySelector('app-invoice-lines')).toBeNull();
    expect([...el.querySelectorAll('mat-label')].map(text)).toEqual([
      'Supplier · المورد', 'Payment amount · قيمة السداد', 'Notes / receipt no. · ملاحظات', 'Paid · طريقة الدفع',
    ]);
    expect(page['owedSuppliers']().map((b: SupplierBalance) => b.supplier)).toEqual(['Nile Farms']);
    expect(page['cashMethods']).toEqual(['Safe', 'DayRevenue']);

    const pay = page['payment']();
    pay.supplierId = 'nile';
    pay.amount = 1_200;
    page['paymentEdited']();
    page['payMethod'] = 'Safe';
    page['paymentEdited']();
    // More than owed.
    expect([page['owedNow'](), page['supplierPaymentReady']()]).toEqual([1_000, false]);
    pay.amount = 600;
    page['paymentEdited']();
    expect(page['supplierPaymentReady']()).toBe(true);
  });

  it('pays from a day’s takings, once, and starts a new payment', async () => {
    const { fixture, page } = await render();
    await switchToPayment(fixture, page);
    const pay = page['payment']();
    Object.assign(pay, { supplierId: 'nile', amount: 600, notes: ' Receipt R-12 ' });
    page['payMethod'] = 'DayRevenue';
    page['paymentEdited']();
    recordVoucher.mockReturnValue(of({ id: pay.id, kind: 'SupplierPayment', amount: -600 }));

    await page['payNow']();

    const sent = recordVoucher.mock.lastCall![0] as SafeVoucherInput;
    expect(sent).toMatchObject({
      requestId: pay.id, transactionId: pay.id, kind: 'SupplierPayment', amount: 600, supplierId: 'nile', notes: 'Receipt R-12',
      revenueDate: '2026-10-15', date: '2026-10-15',
    });
    expect(notify.info).toHaveBeenCalledWith('Nile Farms paid L.E 600.00.');
    // A new payment, with a new id; what suppliers are owed read again.
    expect(page['payment']().id).not.toBe(pay.id);
    expect([page['payMethod'], supplierBalances.mock.calls.length]).toEqual([null, 2]);
  });

  it('sends an accountant’s payment to a manager', async () => {
    approvals.canApprove.mockReturnValue(false);
    const { fixture, el, page } = await render();
    await switchToPayment(fixture, page);
    expect(text(el)).toContain('A manager approves supplier payments: no cash moves until then.');
    Object.assign(page['payment'](), { supplierId: 'nile', amount: 100 });
    page['payMethod'] = 'Safe';
    page['paymentEdited']();
    const asked = { outcome: 'Requested', request: { id: 'r' } };
    recordVoucher.mockReturnValue(of(asked));

    await page['payNow']();

    expect(recordVoucher.mock.lastCall![0]).toMatchObject({ revenueDate: null });
    expect(approvals.requested).toHaveBeenCalledWith(asked);
  });

  it('keeps the invoice being entered while a payment is on screen', async () => {
    const { fixture, page } = await render();
    page['lines'].set([{ itemType: 'Inventory', rawMaterialId: 'tomatoes', expenseItemId: null, quantity: 5, unitSize: 1000, unitLabel: 'kg', cost: 100 }]);
    await switchToPayment(fixture, page);
    page['mode'].set('invoice');
    await fixture.whenStable();

    expect(page['lines']()[0]).toMatchObject({ rawMaterialId: 'tomatoes', cost: 100 });
  });
});

// A posted invoice corrected (تعديل فاتورة): opened on the new invoice screen as entered, its
// changes reviewed as the server sees them, then saved for a manager or sent for approval.
describe('correcting a posted invoice', () => {
  const posted = asInvoice(
    'inv-1',
    {
      supplierId: 'nile', supplierInvoiceNumber: 'F-1', invoiceDate: '2026-10-12', paymentMethod: 'Safe',
      lines: [{ itemType: 'Inventory', rawMaterialId: 'tomatoes', expenseItemId: null, enteredQuantity: 5, unitLabel: 'kg', unitSize: 1000, cost: 100 }],
    },
    'Posted',
  );
  const purchaseInvoice = vi.fn();
  const previewInvoiceCorrection = vi.fn();
  const correctPurchaseInvoice = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { canApprove: vi.fn(() => true), requested: vi.fn(), refresh: vi.fn() };
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
  const preview = {
    summary: 'Invoice F-1 · Nile Farms corrected · L.E 100.00 → L.E 120.00',
    changes: [
      { label: 'Tomatoes', before: '5 kg · L.E 100.00', after: '6 kg · L.E 120.00' },
      { label: 'Total', before: 'L.E 100.00', after: 'L.E 120.00' },
    ],
  };

  async function render(invoice: PurchaseInvoice = posted) {
    purchaseInvoice.mockReturnValue(of(invoice));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            materials: () => of(all),
            purchaseInvoices: () => of([]),
            purchaseInvoice,
            suppliers: () => of([nileFarms, deltaOils]),
            expenseItems: () => of([]),
            businessToday: () => of({ today: '2026-10-15' }),
            supplierBalances: () => of([]),
            previewInvoiceCorrection,
            correctPurchaseInvoice,
          },
        },
        { provide: Approvals, useValue: approvals },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PurchasesPage);
    fixture.componentRef.setInput('correct', invoice.id);
    await fixture.whenStable();
    fixture.detectChanges();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any>, navigate };
  }

  beforeEach(() => {
    previewInvoiceCorrection.mockReset().mockReturnValue(of(preview));
    correctPurchaseInvoice.mockReset();
    notify.info.mockReset();
    approvals.canApprove.mockReturnValue(true);
    approvals.requested.mockReset();
  });

  it('opens the invoice as it was entered, as a correction: no drafts, no paper, no switching to a payment', async () => {
    const { el, page } = await render();

    expect(purchaseInvoice).toHaveBeenCalledWith('inv-1');
    expect([page['supplierId'], page['supplierInvoiceNumber'], page['invoiceDate'], page['paymentMethod']]).toEqual(['nile', 'F-1', '2026-10-12', 'Safe']);
    expect(page['lines']()[0]).toMatchObject({ rawMaterialId: 'tomatoes', quantity: 5, cost: 100 });
    // A new invoice replaces it: a new id.
    expect(page['invoiceId']).not.toBe('inv-1');
    expect(text(el.querySelector('.correcting')!)).toContain('Correcting Invoice F-1 · Nile Farms of 12 Oct, L.E 100.00.');
    expect(el.querySelector('mat-button-toggle-group')).toBeNull();
    expect((el.querySelector('.paper') as HTMLElement).hidden).toBe(true);
  });

  it('reviews the changes as the server sees them, then saves the correction and goes to the new invoice', async () => {
    const { fixture, el, page, navigate } = await render();
    page['lines'].update((lines: Record<string, unknown>[]) => [{ ...lines[0], quantity: 6, cost: 120 }]);
    page['touch']();

    await page['reviewCorrection']();
    fixture.detectChanges();
    const sent = previewInvoiceCorrection.mock.lastCall!;
    expect(sent[0]).toBe('inv-1');
    expect(sent[1]).toMatchObject({ newInvoiceId: page['invoiceId'], supplierId: 'nile', paymentMethod: 'Safe', reason: null });
    expect(sent[1].lines[0]).toMatchObject({ rawMaterialId: 'tomatoes', enteredQuantity: 6, cost: 120 });
    expect([...el.querySelectorAll('.changes tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text))).toEqual([
      ['Tomatoes', '5 kg · L.E 100.00', '6 kg · L.E 120.00'],
      ['Total', 'L.E 100.00', 'L.E 120.00'],
    ]);

    correctPurchaseInvoice.mockReturnValue(of({ outcome: 'Applied', request: { summary: preview.summary } }));
    await page['sendCorrection']();

    expect(correctPurchaseInvoice.mock.lastCall![1].requestId).toBe(sent[1].requestId);
    expect(notify.info).toHaveBeenCalledWith(`Corrected: ${preview.summary}.`);
    expect(navigate).toHaveBeenCalledWith(['/costing/purchases/invoices', page['invoiceId']]);
  });

  it('never greys out Review changes: with a line put wrong, it checks nothing with the server and says what', async () => {
    const { fixture, el, page } = await render();
    page['lines'].update((lines: Record<string, unknown>[]) => [{ ...lines[0], quantity: null }]);
    page['touch']();
    fixture.detectChanges();
    const review = [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).includes('Review changes'))!;
    expect(review.disabled).toBe(false);

    review.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(previewInvoiceCorrection).not.toHaveBeenCalled();
    expect(notify.error).toHaveBeenCalledWith(null, 'Cannot submit. Line 1: enter the quantity.');
    expect([...el.querySelectorAll('mat-error')].map(text)).toEqual(['Enter the quantity']);
  });

  it('sends an accountant’s correction for approval, with the reason, and goes back to the invoice as it is', async () => {
    approvals.canApprove.mockReturnValue(false);
    const { fixture, el, page, navigate } = await render();
    page['invoiceDate'] = '2026-10-11';
    page['touch']();
    await page['reviewCorrection']();
    fixture.detectChanges();
    expect([...el.querySelectorAll('button')].map(text)).toContain('send Send for approval إرسال للموافقة');
    page['reason'] = ' Wrong day ';
    const asked = { outcome: 'Requested', request: { id: 'r' } };
    correctPurchaseInvoice.mockReturnValue(of(asked));

    await page['sendCorrection']();

    expect(correctPurchaseInvoice.mock.lastCall![1]).toMatchObject({ invoiceDate: '2026-10-11', reason: 'Wrong day' });
    expect(approvals.requested).toHaveBeenCalledWith(asked);
    expect(navigate).toHaveBeenCalledWith(['/costing/purchases/invoices', 'inv-1']);
  });

  it('keeps an invoice from before the treasury so', async () => {
    const { el, page } = await render({ ...posted, id: 'old', paymentMethod: 'BeforeTreasury' });

    expect(page['paymentMethod']).toBe('BeforeTreasury');
    expect(text(el.querySelector('.before-treasury')!)).toContain('Before the treasury');
    expect(page['ready']()).toBe(true);
  });
});

describe('saying what is missing before an invoice or a payment is sent', () => {
  const savePurchaseInvoice = vi.fn();
  const recordVoucher = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { canApprove: vi.fn(() => true), requested: vi.fn(), refresh: vi.fn() };
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            materials: () => of(all),
            purchaseInvoices: () => of([]),
            suppliers: () => of([nileFarms, deltaOils]),
            expenseItems: () => of([]),
            businessToday: () => of({ today: '2026-10-15' }),
            supplierBalances: () => of([{ supplierId: 'nile', supplier: 'Nile Farms', balance: 1_000 }]),
            savePurchaseInvoice,
            recordVoucher,
          },
        },
        { provide: Approvals, useValue: approvals },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PurchasesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const button = (label: string) => [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).includes(label))!;
    // Clicked, and the screen brought up to date.
    async function click(label: string) {
      button(label).click();
      await fixture.whenStable();
      fixture.detectChanges();
    }
    const errors = () => [...el.querySelectorAll('mat-error')].map(text);
    return { fixture, el, page: fixture.componentInstance as unknown as Record<string, any>, button, click, errors };
  }

  beforeEach(() => {
    savePurchaseInvoice.mockReset().mockImplementation((id: string, body: SavePurchaseInvoice) => of(asInvoice(id, body)));
    recordVoucher.mockReset();
    notify.error.mockReset();
  });

  it('never greys out Save & review: clicked with something missing, it saves nothing, marks each field and says what', async () => {
    const { fixture, page, button, click, errors } = await render();
    expect(button('Save & review').disabled).toBe(false);
    expect(errors()).toEqual([]);

    await click('Save & review');

    expect(savePurchaseInvoice).not.toHaveBeenCalled();
    expect(notify.error).toHaveBeenCalledWith(null, 'Cannot submit. Paid: choose how it is paid. Line 1: choose the item. Line 1: enter the quantity.');
    expect(errors()).toEqual(['Choose how it is paid', 'Choose the item', 'Enter the quantity']);
    // Taken to the first.
    expect(document.activeElement?.getAttribute('aria-label')).toBe('How it is paid');

    // Each mark goes as its field is put right.
    page['lines'].set([{ itemType: 'Inventory', rawMaterialId: 'oil', expenseItemId: null, quantity: 20, unitSize: null, unitLabel: '', cost: 1900 }]);
    page['touch']();
    fixture.detectChanges();
    expect(errors()).toEqual(['Choose how it is paid']);
    page['paymentMethod'] = 'Safe';
    page['touch']();
    fixture.detectChanges();
    expect(errors()).toEqual([]);

    await click('Save & review');
    expect(savePurchaseInvoice).toHaveBeenCalledTimes(1);
  });

  it('names a supplier typed but not chosen, an expense with no amount, and the rest beyond the first three', async () => {
    const { fixture, page, click, errors } = await render();
    page['supplierEntry'] = 'Nile';
    page['supplierEntered']('Nile');
    page['paymentMethod'] = 'Credit';
    page['lines'].set([
      { itemType: 'Expense', rawMaterialId: null, expenseItemId: 'power', quantity: 1, unitSize: 1, unitLabel: '', cost: null },
      { itemType: 'Inventory', rawMaterialId: 'oil', expenseItemId: null, quantity: 0, unitSize: null, unitLabel: '', cost: 10 },
      { itemType: 'Inventory', rawMaterialId: 'oil', expenseItemId: null, quantity: 1, unitSize: null, unitLabel: '', cost: 10 },
    ]);
    page['touch']();
    fixture.detectChanges();

    await click('Save & review');

    expect(notify.error).toHaveBeenCalledWith(
      null,
      'Cannot submit. Supplier: choose it from the list, or add it. Line 1: enter what the expense cost. Line 2: enter the quantity. ' +
        'And 1 more, marked in red.',
    );
    expect(errors()).toEqual([
      'Choose it from the list, or add it',
      'Enter what the expense cost',
      'Enter the quantity',
      'It is on the invoice twice: put it on one line',
    ]);
  });

  it('never greys out Pay the supplier: clicked with nothing entered, it pays no one and says what is missing', async () => {
    const { fixture, page, button, click, errors } = await render();
    page['mode'].set('payment');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(button('Pay the supplier').disabled).toBe(false);

    await click('Pay the supplier');

    expect(recordVoucher).not.toHaveBeenCalled();
    expect(notify.error).toHaveBeenCalledWith(
      null,
      'Cannot submit. Supplier payment: choose the supplier. Supplier payment: enter the amount. Paid: choose how the supplier is paid.',
    );
    expect(errors()).toEqual(['Choose the supplier', 'Enter the amount', 'Choose how the supplier is paid']);
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Supplier payment, supplier');

    // More than they are owed is said under the amount.
    Object.assign(page['payment'](), { supplierId: 'nile', amount: 1_200 });
    page['payMethod'] = 'Safe';
    page['paymentEdited']();
    fixture.detectChanges();
    expect(errors()).toEqual(['It is more than they are owed']);

    // Started again: nothing marked.
    page['newPayment']();
    fixture.detectChanges();
    expect(errors()).toEqual([]);
  });
});
