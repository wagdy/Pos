import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, PurchaseInvoice } from '../../core/costing/costing.api';
import { Approvals } from './approvals';
import { InvoicePage } from './invoice-page';

const posted = {
  id: 'inv-1', status: 'Posted', supplierId: 'nile', supplierName: 'Nile Farms', supplierInvoiceNumber: 'F-1', invoiceDate: '2026-10-12',
  createdAtUtc: '2026-10-12T09:00:00Z', createdBy: 'Mona', postedAtUtc: '2026-10-12T09:05:00Z', postedBy: 'Omar', total: 100, attachment: null,
  lines: [], inventoryTotal: 100, expenseTotal: 0, paymentMethod: 'Safe', revenueDate: null, paymentStatus: 'Paid',
} as PurchaseInvoice;

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('an invoice on its own page', () => {
  const pendingFor = vi.fn();

  async function render(invoice: PurchaseInvoice) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { purchaseInvoice: () => of(invoice) } },
        { provide: Approvals, useValue: { pendingFor } },
      ],
    });
    const fixture = TestBed.createComponent(InvoicePage);
    fixture.componentRef.setInput('id', invoice.id);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => pendingFor.mockReset().mockReturnValue(undefined));

  it('offers to edit a posted invoice, as a correction', async () => {
    const el = await render(posted);

    const edit = [...el.querySelectorAll('a')].find((a) => text(a) === 'edit Edit · تعديل')!;
    expect(edit.getAttribute('href')).toBe('/costing/purchases/new?correct=inv-1');
  });

  it('says a correction waits for a manager, and offers no edit meanwhile', async () => {
    pendingFor.mockReturnValue({ action: 'Update', requestedBy: 'Mona' });
    const el = await render(posted);

    expect(text(el.querySelector('.ask')!)).toBe('Mona has asked to correct it: it waits for a manager, and stays as it is until then.');
    expect([...el.querySelectorAll('a')].map(text)).not.toContain('edit Edit · تعديل');
  });

  it('links a voided invoice to its correction, and a correction to what it corrects', async () => {
    const voided = await render({ ...posted, status: 'Voided', replacedBy: 'inv-2' });
    expect(voided.querySelector('.link-note a')!.getAttribute('href')).toBe('/costing/purchases/invoices/inv-2');
    expect([...voided.querySelectorAll('a')].map(text)).not.toContain('edit Edit · تعديل');
    TestBed.resetTestingModule();

    const correction = await render({ ...posted, id: 'inv-2', revisionOf: 'inv-1' });
    expect(text(correction.querySelector('.link-note')!)).toContain('A correction of an earlier invoice, voided.');
  });
});
