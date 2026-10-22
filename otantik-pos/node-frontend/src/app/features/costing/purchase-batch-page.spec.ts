import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi, MaterialCost, PurchaseBatch, PurchaseInvoice, Supplier, SupplierBalance } from '../../core/costing/costing.api';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { PurchaseBatchPage } from './purchase-batch-page';

const tomatoes = { id: 'tomatoes', name: 'Tomatoes', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000, kind: 'Purchased' } as MaterialCost;
const milk = { id: 'milk', name: 'Milk', unit: 'Millilitre', purchaseUnit: 'L', purchaseUnitSize: 1000, kind: 'Purchased' } as MaterialCost;
const nile = { id: 'nile', name: 'Nile Farms' } as Supplier;
const delta = { id: 'delta', name: 'Delta Dairy' } as Supplier;
// Nile Farms is owed 1,000 from before; Delta Dairy nothing.
const balances = [
  { supplierId: 'nile', supplier: 'Nile Farms', balance: 1_000 },
  { supplierId: 'delta', supplier: 'Delta Dairy', balance: 0 },
] as SupplierBalance[];

function posted(id: string, supplier: Supplier, total: number, paymentStatus: PurchaseInvoice['paymentStatus']): PurchaseInvoice {
  return { id, supplierId: supplier.id, supplierName: supplier.name, supplierInvoiceNumber: null, total, paymentStatus } as PurchaseInvoice;
}

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('a day’s purchases entered together', () => {
  const postPurchaseBatch = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { canApprove: vi.fn(() => true), refresh: vi.fn() };

  // hr: signed in as someone who manages HR, who may advance employees in the batch.
  async function render(hr = false) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { can: (p: string) => hr || p !== Permissions.HrManage } },
        {
          provide: HrApi,
          useValue: {
            employees: () => of([
              { id: 'hassan', name: 'Hassan', jobTitle: 'Chef', isActive: true },
              { id: 'old', name: 'Gone', jobTitle: 'Waiter', isActive: false },
            ] as Employee[]),
          },
        },
        {
          provide: CostingApi,
          useValue: {
            materials: () => of([tomatoes, milk]),
            suppliers: () => of([nile, delta]),
            expenseItems: () => of([]),
            businessToday: () => of({ today: '2026-10-13' }),
            supplierBalances: () => of(balances),
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

  it('never greys out Submit batch: clicked with something missing, it sends nothing, marks each field and says what', async () => {
    const { fixture, el, page } = await render();
    notify.error.mockReset();
    // Invoice 1 has its supplier but no quantity; invoice 2 nothing; a payment with no amount.
    fill(page['blocks']()[0], nile, tomatoes, 5, 100);
    page['blocks']()[0].editor.lines()[0].quantity = null;
    page['addBlock']();
    page['addPayment']();
    page['payments']()[0].supplierId = 'nile';
    page['changed']();
    fixture.detectChanges();
    const submit = [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).includes('Submit batch'))!;
    expect(submit.disabled).toBe(false);

    submit.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(postPurchaseBatch).not.toHaveBeenCalled();
    expect(notify.error).toHaveBeenCalledWith(
      null,
      'Cannot submit. Paid: choose how they were paid. Invoice 1, line 1: enter the quantity. Invoice 2, line 1: choose the item. ' +
        'And 2 more, marked in red.',
    );
    expect([...el.querySelectorAll('mat-error')].map(text)).toEqual([
      'Choose how they were paid', 'Enter the quantity', 'Choose the item', 'Enter the quantity', 'Enter the amount',
    ]);
    expect(document.activeElement?.getAttribute('aria-label')).toBe('How they are paid');
    expect(el.querySelector('[aria-label="Supplier payment 1, amount"]')!.closest('mat-form-field')!.classList).toContain('mat-form-field-invalid');

    // Put right, it goes.
    page['blocks']()[0].editor.lines()[0].quantity = 5;
    page['blocks']()[0].editor.touch();
    page['removeBlock'](1);
    page['payments']()[0].amount = 300;
    page['paymentMethod'] = 'Safe';
    page['changed']();
    fixture.detectChanges();
    expect(el.querySelectorAll('mat-error').length).toBe(0);
    postPurchaseBatch.mockReturnValue(of({ invoices: [posted('i1', nile, 100, 'Paid')], payments: [] }));
    submit.click();
    await fixture.whenStable();
    expect(postPurchaseBatch).toHaveBeenCalledTimes(1);
  });

  it('unmarks everything when the batch is started again', async () => {
    const { fixture, el, page } = await render();
    await page['submit']();
    fixture.detectChanges();
    expect(el.querySelectorAll('mat-error').length).toBeGreaterThan(0);

    page['startAgain']();
    fixture.detectChanges();
    expect(el.querySelectorAll('mat-error').length).toBe(0);
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
    postPurchaseBatch.mockReturnValue(of({ invoices: [posted(ids[0], nile, 100, 'Pending'), posted(ids[1], delta, 420, 'Pending')], payments: [] }));

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

  it('pays a supplier in the batch: no lines, only the supplier owed, the amount and notes; never more than owed', async () => {
    const { fixture, el, page } = await render();
    page['paymentMethod'] = 'DayRevenue';
    page['changed']();

    // The empty invoice the batch starts with goes: a batch of payments alone.
    page['addPayment']();
    fixture.detectChanges();
    expect(page['blocks']().length).toBe(0);
    const block = el.querySelector('.payment-block')!;
    expect(block.querySelector('app-invoice-lines')).toBeNull();
    expect([...block.querySelectorAll('mat-label')].map(text)).toEqual(['Supplier · المورد', 'Payment amount · قيمة السداد', 'Notes / receipt no. · ملاحظات']);
    // Only those owed something can be paid.
    expect(page['owedSuppliers']().map((b: SupplierBalance) => b.supplier)).toEqual(['Nile Farms']);

    const pay = page['payments']()[0];
    pay.supplierId = 'nile';
    pay.amount = 600;
    pay.notes = ' Receipt R-12 ';
    page['changed']();
    expect([page['owedBefore'](0), page['ready']()]).toEqual([1_000, true]);
    // Two together more than owed: the second is over.
    page['addPayment']();
    Object.assign(page['payments']()[1], { supplierId: 'nile', amount: 500 });
    page['changed']();
    fixture.detectChanges();
    expect([page['owedBefore'](1), page['ready']()]).toEqual([400, false]);
    expect(page['missing']()).toEqual([
      { where: 'Supplier payment 2', what: 'it is more than they are owed', field: 'Supplier payment 2, amount' },
    ]);
    expect(text(el.querySelectorAll('.payment-block')[1])).toContain('Owed L.E 400.00 after the payments above');
    page['payments']()[1].amount = 400;
    page['changed']();
    expect(page['ready']()).toBe(true);
    expect(page['countLabel']()).toBe('2 payments');
  });

  it('sends the payments with the batch, from the day’s takings, and shows what was paid', async () => {
    const { fixture, el, page } = await render();
    page['paymentMethod'] = 'DayRevenue';
    fill(page['blocks']()[0], nile, tomatoes, 5, 100);
    page['addPayment']();
    Object.assign(page['payments']()[0], { supplierId: 'nile', amount: 600, notes: ' Receipt R-12 ' });
    page['changed']();
    const id = page['payments']()[0].id;
    postPurchaseBatch.mockReturnValue(of({
      invoices: [posted(page['blocks']()[0].id, nile, 100, 'Paid')],
      payments: [{ transactionId: id, supplierId: 'nile', supplier: 'Nile Farms', amount: 600, notes: 'Receipt R-12', outcome: 'Applied' }],
    }));

    await page['submit']();
    fixture.detectChanges();

    const sent = postPurchaseBatch.mock.lastCall![0] as PurchaseBatch;
    expect(sent).toMatchObject({ paymentMethod: 'DayRevenue', revenueDate: '2026-10-13' });
    expect(sent.payments).toEqual([{ transactionId: id, supplierId: 'nile', amount: 600, notes: 'Receipt R-12' }]);
    expect(sent.invoices.length).toBe(1);
    expect(notify.info).toHaveBeenCalledWith('1 invoice posted, 1 supplier paid.');
    expect([...el.querySelectorAll('.payments-made tbody td')].map(text)).toEqual(['Nile Farms', 'Receipt R-12', 'L.E 600.00', 'Paidتم السداد']);
    expect(page['payments']()).toEqual([]);
  });

  it('pays no supplier on credit', async () => {
    const { fixture, el, page } = await render();
    // As choosing it in the list does: the page marked for a fresh look.
    page['paymentMethod'] = 'Credit';
    page['changed']();
    fixture.componentRef.changeDetectorRef.markForCheck();
    await fixture.whenStable();

    const add = [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).startsWith('payments Add supplier payment'))!;
    expect(add.disabled).toBe(true);
    // Once there are payments, the batch cannot be put on credit either.
    page['paymentMethod'] = 'Safe';
    page['addPayment']();
    page['paymentMethod'] = 'Credit';
    page['changed']();
    Object.assign(page['payments']()[0], { supplierId: 'nile', amount: 100 });
    expect(page['ready']()).toBe(false);
  });

  it('advances an employee in the batch, from the day’s takings, and shows it was paid', async () => {
    const { fixture, el, page } = await render(true);
    const add = () => [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).startsWith('request_quote Add employee advance'));
    expect(add()).toBeTruthy();

    page['addAdvance']();
    fixture.detectChanges();
    // The empty invoice the batch starts with goes: a batch of advances alone can be sent.
    expect(page['blocks']().length).toBe(0);
    expect(page['employees']().map((e: Employee) => e.name)).toEqual(['Hassan']);
    expect(page['missing']().map((p: { where: string; what: string }) => `${p.where}: ${p.what}`)).toEqual([
      'Paid: choose how they were paid',
      'Advance 1: enter the amount',
    ]);

    page['paymentMethod'] = 'DayRevenue';
    Object.assign(page['advances']()[0], { employeeId: 'hassan', amount: 300, notes: ' Rent ' });
    page['changed']();
    expect(page['countLabel']()).toBe('1 advance');
    const id = page['advances']()[0].id;
    postPurchaseBatch.mockReturnValue(of({
      invoices: [],
      payments: [],
      advances: [{ transactionId: id, employeeId: 'hassan', employee: 'Hassan', amount: 300, notes: 'Rent', outcome: 'Applied' }],
    }));

    await page['submit']();
    fixture.detectChanges();

    const sent = postPurchaseBatch.mock.lastCall![0] as PurchaseBatch;
    expect(sent).toMatchObject({ paymentMethod: 'DayRevenue', revenueDate: '2026-10-13', invoices: [], payments: [] });
    expect(sent.advances).toEqual([{ transactionId: id, employeeId: 'hassan', amount: 300, notes: 'Rent' }]);
    expect(notify.info).toHaveBeenCalledWith('1 advance paid.');
    expect([...el.querySelectorAll('.payments-made tbody td')].map(text)).toEqual(['Hassan', 'Rent', 'L.E 300.00', 'Paid, off their payصُرفت']);
    expect(page['advances']()).toEqual([]);
  });

  it('advances no one on credit, and offers no advance to whoever does not manage HR', async () => {
    const { page } = await render(true);
    page['addAdvance']();
    Object.assign(page['advances']()[0], { employeeId: 'hassan', amount: 100 });
    page['paymentMethod'] = 'Credit';
    page['changed']();
    expect(page['missing']()[0]).toMatchObject({ where: 'Paid', what: "advances are cash: choose the safe or a day's takings" });
    TestBed.resetTestingModule();

    const other = await render(false);
    expect([...other.el.querySelectorAll('button')].map(text).some((t) => t.includes('Add employee advance'))).toBe(false);
  });

  it('keeps the payments after a reload, until they are sent', async () => {
    const first = await render();
    first.page['paymentMethod'] = 'Safe';
    first.page['addPayment']();
    Object.assign(first.page['payments']()[0], { supplierId: 'nile', amount: 250, notes: 'R-7' });
    first.page['changed']();
    first.fixture.detectChanges();
    const id = first.page['payments']()[0].id;
    TestBed.resetTestingModule();

    const { page } = await render();
    expect(page['payments']()).toEqual([{ id, supplierId: 'nile', amount: 250, notes: 'R-7' }]);
    expect(page['blocks']().length).toBe(0);
  });
});
