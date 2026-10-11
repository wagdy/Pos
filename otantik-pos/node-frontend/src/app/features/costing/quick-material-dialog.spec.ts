import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi } from '../../core/costing/costing.api';
import { QuickMaterialDialog } from './quick-material-dialog';

describe('adding a raw material from the invoice', () => {
  const saveMaterial = vi.fn();
  const close = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { saveMaterial } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: { name: ' Sumac ' } },
      ],
    });
    const fixture = TestBed.createComponent(QuickMaterialDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    saveMaterial.mockReset().mockReturnValue(of({ id: 'sumac', name: 'Sumac' }));
    close.mockReset();
  });

  it('starts from the typed name, bought by the kg, and saves only what a delivery needs', async () => {
    const { page } = await render();
    page['form'].patchValue({ categoryId: 'spices', yield: 95 });

    await page['save']();

    expect(saveMaterial).toHaveBeenCalledWith(null, {
      name: 'Sumac',
      unit: 'Gram',
      reorderLevel: 0,
      code: null,
      categoryId: 'spices',
      purchaseUnit: 'kg',
      purchaseUnitSize: 1000,
      defaultYieldPercent: 95,
      costPerPurchaseUnit: null,
    });
    expect(close).toHaveBeenCalledWith({ id: 'sumac', name: 'Sumac' });
  });

  it('suggests the purchase unit that goes with what it is counted in', async () => {
    const { page } = await render();

    page['unitChanged']('Piece');
    expect(page['form'].getRawValue()).toMatchObject({ purchaseUnit: 'piece', purchaseUnitSize: 1 });
    page['unitChanged']('Millilitre');
    expect(page['form'].getRawValue()).toMatchObject({ purchaseUnit: 'L', purchaseUnitSize: 1000 });
  });

  it('stays open with the reason when the till refuses it, so nothing typed is lost', async () => {
    saveMaterial.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['Raw material name is required.'] } })));
    const { fixture, page } = await render();

    await page['save']();
    fixture.detectChanges();

    expect(close).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Raw material name is required.');
    expect(page['form'].getRawValue().name).toBe('Sumac');
  });
});
