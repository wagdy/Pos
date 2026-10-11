import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi, Supplier } from '../../core/costing/costing.api';
import { SupplierDialog, SupplierDialogData } from './supplier-dialog';

const nileFarms: Supplier = {
  id: 'nile',
  name: 'Nile Farms',
  contactPerson: 'Hassan',
  phone: '0100 123 4567',
  email: null,
  notes: null,
  isArchived: false,
  invoices: 2,
  postedTotal: 300,
  lastPostedAtUtc: null,
};

describe('the supplier dialog', () => {
  const saveSupplier = vi.fn();
  const close = vi.fn();

  async function render(data: SupplierDialogData) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { saveSupplier } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    });
    const fixture = TestBed.createComponent(SupplierDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    saveSupplier.mockReset().mockImplementation((id: string, body: object) => of({ ...nileFarms, ...body, id }));
    close.mockReset();
  });

  it('adds the name typed on the invoice, with what else is known, and closes with it', async () => {
    const { page } = await render({ name: ' Sinai Dairy ', fromInvoice: true });
    expect(page['form'].getRawValue().name).toBe('Sinai Dairy');
    page['form'].patchValue({ phone: ' 0122 000 1111 ', notes: '  ' });

    await page['save']();

    const [id, body] = saveSupplier.mock.lastCall!;
    expect(id).toMatch(/^[0-9a-f-]{36}$/);
    expect(body).toEqual({ name: 'Sinai Dairy', contactPerson: null, phone: '0122 000 1111', email: null, notes: null });
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ id, name: 'Sinai Dairy' }));
  });

  it('edits a supplier under its own id', async () => {
    const { page } = await render({ supplier: nileFarms });
    expect(page['form'].getRawValue()).toMatchObject({ name: 'Nile Farms', contactPerson: 'Hassan', phone: '0100 123 4567' });
    page['form'].patchValue({ phone: '0100 765 4321' });

    await page['save']();

    expect(saveSupplier).toHaveBeenCalledWith('nile', expect.objectContaining({ name: 'Nile Farms', phone: '0100 765 4321' }));
  });

  it('will not save without a name, or with an email missing its @', async () => {
    const { page } = await render({});

    expect(page['form'].invalid).toBe(true);
    page['form'].patchValue({ name: 'Delta Oils', email: 'orders.delta' });
    expect(page['form'].invalid).toBe(true);
    await page['save']();
    expect(saveSupplier).not.toHaveBeenCalled();
  });

  it('stays open with the refusal shown, nothing typed lost', async () => {
    saveSupplier.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['Nile Farms is already a supplier.'] } })),
    );
    const { fixture, page } = await render({ name: 'nile farms', fromInvoice: true });

    await page['save']();
    fixture.detectChanges();

    expect(close).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector('.error')!.textContent).toContain('Nile Farms is already a supplier.');
    expect(page['form'].getRawValue().name).toBe('nile farms');
  });
});
