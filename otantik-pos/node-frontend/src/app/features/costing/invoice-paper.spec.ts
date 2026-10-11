import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi } from '../../core/costing/costing.api';
import { InvoicePaperDialog } from './invoice-paper';

describe('the invoice paper', () => {
  const purchaseInvoiceFile = vi.fn();

  async function render(fileName: string) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { purchaseInvoiceFile } },
        { provide: MAT_DIALOG_DATA, useValue: { invoiceId: 'farms', fileName } },
      ],
    });
    const fixture = TestBed.createComponent(InvoicePaperDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    purchaseInvoiceFile.mockReset();
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:paper');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
  });

  afterEach(() => vi.restoreAllMocks());

  it('shows a photo on the page, fetched with the sign-in', async () => {
    purchaseInvoiceFile.mockReturnValue(of(new Blob(['\xff\xd8\xff'], { type: 'image/jpeg' })));
    const el = await render('farm.jpg');

    expect(purchaseInvoiceFile).toHaveBeenCalledWith('farms');
    expect(el.querySelector('img')?.getAttribute('src')).toBe('blob:paper');
    expect(el.querySelector('iframe')).toBeNull();
  });

  it('shows a PDF in the browsers own viewer', async () => {
    purchaseInvoiceFile.mockReturnValue(of(new Blob(['%PDF-1.7'], { type: 'application/pdf' })));
    const el = await render('scan.pdf');

    expect(el.querySelector('iframe')?.getAttribute('src')).toBe('blob:paper');
    expect(el.querySelector('img')).toBeNull();
  });

  it('downloads it under its own name', async () => {
    purchaseInvoiceFile.mockReturnValue(of(new Blob(['%PDF-1.7'], { type: 'application/pdf' })));
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const el = await render('scan.pdf');

    [...el.querySelectorAll('button')].find((b) => b.textContent!.includes('Download'))!.click();

    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toBe('scan.pdf');
  });

  it('says so when it cannot be fetched', async () => {
    purchaseInvoiceFile.mockReturnValue(throwError(() => new Error('gone')));
    const el = await render('farm.jpg');

    expect(el.querySelector('.error')?.textContent).toContain('could not be loaded');
    expect(el.querySelector('img')).toBeNull();
  });
});
