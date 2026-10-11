import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, PurchaseInvoice, PurchaseInvoiceSummary } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { InvoicePage } from './invoice-page';
import { InvoicePaperDialog } from './invoice-paper';
import { InvoiceViewDialog } from './invoice-view-dialog';
import { InvoicesPage } from './invoices-page';

function summary(overrides: Partial<PurchaseInvoiceSummary>): PurchaseInvoiceSummary {
  return {
    id: 'inv',
    status: 'Posted',
    supplierId: null,
    supplierName: null,
    supplierInvoiceNumber: null,
    invoiceDate: null,
    createdAtUtc: '2026-10-07T08:00:00Z',
    createdBy: 'Omar',
    postedAtUtc: '2026-10-07T08:10:00Z',
    postedBy: 'Omar',
    total: 0,
    lines: 1,
    attachmentFileName: null,
    inventoryTotal: overrides.total ?? 0,
    expenseTotal: 0,
    ...overrides,
  };
}

const farms = summary({
  id: 'farms',
  supplierId: 'nile',
  supplierName: 'Nile Farms',
  supplierInvoiceNumber: 'F-1001',
  invoiceDate: '2026-10-05',
  total: 100,
  attachmentFileName: 'farm.jpg',
});
const oils = summary({ id: 'oils', supplierId: 'delta', supplierName: 'Delta Oils', supplierInvoiceNumber: 'D-77', total: 1900, createdBy: 'Sara' });

function stock(category: string) {
  return { itemType: 'Inventory' as const, expenseItemId: null, categoryId: category.toLowerCase(), category, categoryAr: null };
}

const posted: PurchaseInvoice = {
  id: 'farms',
  status: 'Posted',
  supplierId: 'nile',
  supplierName: 'Nile Farms',
  supplierInvoiceNumber: 'F-1001',
  invoiceDate: '2026-10-05',
  createdAtUtc: '2026-10-05T08:00:00Z',
  createdBy: 'Sara',
  postedAtUtc: '2026-10-05T08:30:00Z',
  postedBy: 'Omar',
  total: 109.5,
  attachment: { fileName: 'farm.jpg', contentType: 'image/jpeg', bytes: 245_760, attachedAtUtc: '2026-10-05T08:05:00Z', attachedBy: 'Sara' },
  lines: [
    { rawMaterialId: 'tomatoes', material: 'Tomatoes', unit: 'Gram', enteredQuantity: 5, unitLabel: 'kg', unitSize: 1000, quantity: 5000, cost: 100, ...stock('Vegetables') },
    { rawMaterialId: 'oil', material: 'Frying oil', unit: 'Millilitre', enteredQuantity: 0.1, unitLabel: 'L', unitSize: 1000, quantity: 100, cost: 9.5, ...stock('Oils') },
  ],
  inventoryTotal: 109.5,
  expenseTotal: 0,
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the invoices history', () => {
  const purchaseInvoices = vi.fn();
  const purchaseInvoice = vi.fn();
  const purchaseInvoiceFile = vi.fn();
  const open = vi.fn();

  function configure(extra: unknown[] = []) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { purchaseInvoices, purchaseInvoice, purchaseInvoiceFile } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: { info: vi.fn(), error: vi.fn() } },
        ...(extra as never[]),
      ],
    });
  }

  beforeEach(() => {
    purchaseInvoices.mockReset().mockReturnValue(of([farms, oils]));
    purchaseInvoice.mockReset().mockReturnValue(of(posted));
    purchaseInvoiceFile.mockReset().mockReturnValue(of(new Blob(['\xff\xd8\xff'], { type: 'image/jpeg' })));
    open.mockReset();
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:paper');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
  });

  afterEach(() => vi.restoreAllMocks());

  async function history() {
    configure();
    const fixture = TestBed.createComponent(InvoicesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  it('lists the posted invoices with their date, supplier, total, who entered them, the paper, and Show', async () => {
    const { el } = await history();

    expect(purchaseInvoices).toHaveBeenCalledWith('Posted');
    const rows = [...el.querySelectorAll('tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].slice(0, 5).map(text))).toEqual([
      ['5 Oct 2026', 'Nile Farms', 'F-1001', 'L.E 100.00', 'Omar'],
      // No date on the paper: the day it was posted.
      ['7 Oct 2026', 'Delta Oils', 'D-77', 'L.E 1,900.00', 'Sara'],
    ]);
    expect(rows.map((r) => !!r.querySelector('button[aria-label^="View"]'))).toEqual([true, false]);
    expect(rows.map((r) => text(r.querySelector('button.show')!))).toEqual(['visibility Show · عرض', 'visibility Show · عرض']);
  });

  it('finds an invoice by supplier or number', async () => {
    const { page } = await history();

    page['search'].set('d-7');
    expect(page['shown']()).toEqual([oils]);
    page['search'].set('nile');
    expect(page['shown']()).toEqual([farms]);
    expect(page['shownTotal']()).toBe(100);
  });

  it('shows an invoice read-only over the list, from Show or the row', async () => {
    const { el } = await history();
    const rows = [...el.querySelectorAll('tbody tr')];

    rows[1].querySelector<HTMLButtonElement>('button.show')!.click();
    expect(open).toHaveBeenCalledExactlyOnceWith(InvoiceViewDialog, expect.objectContaining({ data: { invoiceId: 'oils' }, autoFocus: 'dialog' }));

    (rows[0] as HTMLElement).click();
    expect(open).toHaveBeenLastCalledWith(InvoiceViewDialog, expect.objectContaining({ data: { invoiceId: 'farms' } }));
  });

  it('opens the paper from the paperclip, not the invoice', async () => {
    const { el } = await history();

    el.querySelector<HTMLButtonElement>('tbody tr button[aria-label^="View"]')!.click();

    expect(open).toHaveBeenCalledExactlyOnceWith(InvoicePaperDialog, expect.objectContaining({ data: { invoiceId: 'farms', fileName: 'farm.jpg' } }));
  });

  describe('Show', () => {
    async function show() {
      configure([{ provide: MAT_DIALOG_DATA, useValue: { invoiceId: 'farms' } }]);
      const fixture = TestBed.createComponent(InvoiceViewDialog);
      await fixture.whenStable();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('has the supplier, date, total, every line as billed, and the paper itself', async () => {
      const el = await show();

      expect(purchaseInvoice).toHaveBeenCalledWith('farms');
      expect(text(el.querySelector('h2')!)).toContain('Nile Farms · F-1001');
      expect([...el.querySelectorAll('.details div')].map((d) => [text(d.querySelector('dt')!), text(d.querySelector('dd')!)])).toEqual([
        ['Supplier · المورد', 'Nile Farms'],
        ['Invoice no. · رقم الفاتورة', 'F-1001'],
        ['Date · التاريخ', '5 Oct 2026'],
        ['Total · الإجمالي', 'L.E 109.50'],
      ]);
      expect([...el.querySelectorAll('thead th')].map((th) => th.firstChild!.textContent!.trim())).toEqual([
        'Item',
        'Category',
        'Quantity',
        'Unit',
        'Unit price',
        'Line total',
        'Into stock',
      ]);
      expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text))).toEqual([
        ['Tomatoes', 'Vegetables', '5', 'kg', 'L.E 20.00', 'L.E 100.00', '5,000 g'],
        ['Frying oil', 'Oils', '0.1', 'L', 'L.E 95.00', 'L.E 9.50', '100 ml'],
      ]);
      await vi.waitFor(() => expect(el.querySelector('app-paper-preview img')?.getAttribute('src')).toBe('blob:paper'));
      expect(purchaseInvoiceFile).toHaveBeenCalledWith('farms');
    });

    it('changes nothing: no field to type in, no save', async () => {
      const el = await show();

      expect(el.querySelectorAll('input, textarea, select')).toHaveLength(0);
      expect([...el.querySelectorAll('button, a')].map(text)).toEqual(['download Download', 'open_in_full Open as a page', 'Close']);
    });

    it('says so for an invoice that is not there', async () => {
      purchaseInvoice.mockReturnValue(of(null));
      const el = await show();

      expect(text(el)).toContain('No such invoice');
    });
  });

  describe('the invoice page', () => {
    async function render() {
      configure();
      const fixture = TestBed.createComponent(InvoicePage);
      fixture.componentRef.setInput('id', 'farms');
      await fixture.whenStable();
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('shows the same read-only invoice, for a link to it', async () => {
      const el = await render();

      expect(purchaseInvoice).toHaveBeenCalledWith('farms');
      expect(el.querySelectorAll('app-invoice-view tbody tr')).toHaveLength(2);
      expect(text(el.querySelector('tfoot')!)).toContain('L.E 109.50');
      expect(text(el)).toContain('Posted');
    });

    it('says so when there is no such invoice', async () => {
      purchaseInvoice.mockReturnValue(of(null));
      const el = await render();

      expect(text(el)).toContain('No such invoice');
    });
  });
});
