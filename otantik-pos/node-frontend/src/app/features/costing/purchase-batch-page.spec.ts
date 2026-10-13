import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { CostingApi, MaterialCost, PurchaseBatch, PurchaseInvoice, Supplier } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { PurchaseBatchPage } from './purchase-batch-page';

const tomatoes = { id: 'tomatoes', name: 'Tomatoes', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000, kind: 'Purchased' } as MaterialCost;
const milk = { id: 'milk', name: 'Milk', unit: 'Millilitre', purchaseUnit: 'L', purchaseUnitSize: 1000, kind: 'Purchased' } as MaterialCost;
const nile = { id: 'nile', name: 'Nile Farms' } as Supplier;
const delta = { id: 'delta', name: 'Delta Dairy' } as Supplier;

function posted(id: string, supplier: Supplier, total: number, paymentStatus: PurchaseInvoice['paymentStatus']): PurchaseInvoice {
  return { id, supplierId: supplier.id, supplierName: supplier.name, supplierInvoiceNumber: null, total, paymentStatus } as PurchaseInvoice;
}

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('a day’s purchases entered together', () => {
  const postPurchaseBatch = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { canApprove: vi.fn(() => true), refresh: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: CostingApi,
          useValue: {
            materials: () => of([tomatoes, milk]),
            suppliers: () => of([nile, delta]),
            expenseItems: () => of([]),
            businessToday: () => of({ today: '2026-10-13' }),
            postPurchaseBatch,
          },
        },
        { provide: Approvals, useValue: approvals },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(PurchaseBatchPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  // An invoice of the batch filled in: its supplier, and one line.
  function fill(block: Record<string, any>, supplier: Supplier | null, material: MaterialCost, quantity: number, cost: number) {
    block['supplier'].entered(supplier);
    block['editor'].lines.set([{ itemType: 'Inventory', rawMaterialId: material.id, expenseItemId: null, quantity, unitSize: 1000, unitLabel: '', cost }]);
    block['editor'].touch();
  }

  beforeEach(() => {
    localStorage.removeItem('otantik-till.purchase-batch');
    postPurchaseBatch.mockReset();
    notify.info.mockReset();
    approvals.canApprove.mockReturnValue(true);
  });

  it('is sent only once each invoice is ready and the payment chosen, every one with its supplier on credit', async () => {
    const { page } = await render();

    expect(page['invoiceDate']).toBe('2026-10-13');
    fill(page['blocks']()[0], nile, tomatoes, 5, 100);
    page['addBlock']();
    fill(page['blocks']()[1], null, milk, 10, 420);
    expect(page['ready']()).toBe(false);

    page['paymentMethod'] = 'Safe';
    page['changed']();
    expect(page['ready']()).toBe(true);

    // On credit, each is owed to someone: the second needs its supplier.
    page['paymentMethod'] = 'Credit';
    page['changed']();
    expect(page['ready']()).toBe(false);
    page['blocks']()[1]['supplier'].entered(delta);
    expect(page['ready']()).toBe(true);
    expect(page['total']()).toBe(520);
  });

  it('sends each invoice with its own id and supplier, shows what was posted, and starts again', async () => {
    const { fixture, el, page } = await render();
    approvals.canApprove.mockReturnValue(false);
    page['paymentMethod'] = 'Safe';
    fill(page['blocks']()[0], nile, tomatoes, 5, 100);
    page['blocks']()[0]['number'] = ' F-1 ';
    page['addBlock']();
    fill(page['blocks']()[1], delta, milk, 10, 420);
    page['changed']();
    const ids = page['blocks']().map((b: Record<string, any>) => b['id']);
    postPurchaseBatch.mockReturnValue(of([posted(ids[0], nile, 100, 'Pending'), posted(ids[1], delta, 420, 'Pending')]));

    await page['submit']();

    const sent = postPurchaseBatch.mock.lastCall![0] as PurchaseBatch;
    expect(sent).toMatchObject({ invoiceDate: '2026-10-13', paymentMethod: 'Safe', revenueDate: null });
    expect(sent.invoices.map((i) => [i.invoiceId, i.supplierId, i.supplierInvoiceNumber, i.lines.length])).toEqual([
      [ids[0], 'nile', 'F-1', 1],
      [ids[1], 'delta', null, 1],
    ]);
    expect(sent.invoices[1].lines[0]).toMatchObject({ itemType: 'Inventory', rawMaterialId: 'milk', enteredQuantity: 10, unitSize: 1000, unitLabel: 'L', cost: 420 });
    expect(notify.info).toHaveBeenCalledWith("2 invoices posted. 2 payments wait for a manager's approval.");

    fixture.detectChanges();
    expect([...el.querySelectorAll('.posted tbody tr')].map((r) => text(r.querySelector('td')!))).toEqual(['Nile Farms', 'Delta Dairy']);
    // A new batch: one empty invoice, a new id.
    expect(page['blocks']().length).toBe(1);
    expect(ids).not.toContain(page['blocks']()[0]['id']);
  });

  it('keeps the batch when it is refused, and says each problem by its invoice', async () => {
    const { fixture, el, page } = await render();
    page['paymentMethod'] = 'Credit';
    fill(page['blocks']()[0], nile, tomatoes, 5, 100);
    page['changed']();
    const refused = 'Invoice 1 · Nile Farms: Tomatoes has been deleted from the raw materials.\nInvoice 2: It has no lines.';
    postPurchaseBatch.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 422, error: { errors: [refused] } })));

    await page['submit']();
    fixture.detectChanges();

    expect(el.querySelector('.problems')!.textContent).toBe(refused);
    expect(page['blocks']()[0]['editor'].lines()[0].rawMaterialId).toBe('tomatoes');
  });

  it('comes back as it was after a reload, until it is sent', async () => {
    const first = await render();
    first.page['paymentMethod'] = 'DayRevenue';
    fill(first.page['blocks']()[0], nile, tomatoes, 5, 100);
    first.page['changed']();
    first.fixture.detectChanges();
    const id = first.page['blocks']()[0]['id'];
    TestBed.resetTestingModule();

    const { page } = await render();
    expect(page['paymentMethod']).toBe('DayRevenue');
    const block = page['blocks']()[0];
    expect([block['id'], block['supplier'].id, block['editor'].lines()[0].rawMaterialId, block['editor'].lines()[0].entry]).toEqual([
      id, 'nile', 'tomatoes', tomatoes,
    ]);
  });
});
