import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { CostingApi, MaterialCost, MaterialPurchaseHistory } from '../../core/costing/costing.api';
import { MaterialHistoryDialog } from './material-history-dialog';

const tomatoes = { id: 'tomatoes', name: 'Tomatoes', unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000 } as MaterialCost;

// 6 kg from Nile Farms correcting 5 kg, 500 g from Cairo Foods, and 1 kg with no price yet.
const card: MaterialPurchaseHistory = {
  materialId: 'tomatoes',
  material: 'Tomatoes',
  unit: 'Gram',
  purchaseUnit: 'kg',
  purchaseUnitSize: 1000,
  lines: [
    { invoiceId: 'f2', date: '2026-10-17', supplier: 'Nile Farms', invoiceNumber: 'F-2', quantity: 1, unit: 'kg', baseQuantity: 1000, unitPrice: null, total: null, voided: false, replacedBy: null, revisionOf: null },
    { invoiceId: 'c7', date: '2026-10-16', supplier: 'Cairo Foods', invoiceNumber: 'C-7', quantity: 500, unit: 'g', baseQuantity: 500, unitPrice: 0.03, total: 15, voided: false, replacedBy: null, revisionOf: null },
    { invoiceId: 'f1b', date: '2026-10-14', supplier: 'Nile Farms', invoiceNumber: 'F-1', quantity: 6, unit: 'kg', baseQuantity: 6000, unitPrice: 20, total: 120, voided: false, replacedBy: null, revisionOf: 'f1' },
    { invoiceId: 'f1', date: '2026-10-14', supplier: 'Nile Farms', invoiceNumber: 'F-1', quantity: 5, unit: 'kg', baseQuantity: 5000, unitPrice: 20, total: 100, voided: true, replacedBy: 'f1b', revisionOf: null },
  ],
  baseQuantity: 7500,
  total: 135,
  averagePerPurchaseUnit: 20.77,
  unpricedLines: 1,
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// A cell's parts, which the screen shows apart (the quantity over its base units), joined by a space.
const cell = (el: Element) =>
  [...el.childNodes]
    .filter((n) => n.nodeType !== Node.COMMENT_NODE)
    .map((n) => n.textContent!.replace(/\s+/g, ' ').trim())
    .filter(Boolean)
    .join(' ');

describe('a material’s purchase history', () => {
  const materialPurchases = vi.fn();
  const close = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { materialPurchases } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: tomatoes },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(MaterialHistoryDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, navigate };
  }

  beforeEach(() => {
    materialPurchases.mockReset().mockReturnValue(of(card));
    close.mockReset();
  });

  it('lists each invoice line that bought it, newest first, as the paper has it', async () => {
    const { el } = await render();

    expect(materialPurchases).toHaveBeenCalledWith('tomatoes');
    expect(text(el.querySelector('h2')!)).toBe('Tomatoes · Purchase history كارت الصنف');
    const rows = [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 6).map(cell));
    expect(rows).toEqual([
      ['17 Oct 2026', 'Nile Farms', 'F-2', '1 kg 1,000 g', '', 'No price'],
      ['16 Oct 2026', 'Cairo Foods', 'C-7', '500 g', 'L.E 0.03 / g', 'L.E 15.00'],
      ['14 Oct 2026', 'Nile Farms', 'F-1 Correction · تعديل', '6 kg 6,000 g', 'L.E 20.00 / kg', 'L.E 120.00'],
      ['14 Oct 2026', 'Nile Farms', 'F-1 Voided · ملغاة Corrected by a new invoice', '5 kg 5,000 g', 'L.E 20.00 / kg', 'L.E 100.00'],
    ]);
  });

  it('strikes a voided invoice through and leaves it out of the totals', async () => {
    const { el } = await render();

    const rows = el.querySelectorAll('tbody tr');
    expect([...rows].map((r) => r.classList.contains('voided'))).toEqual([false, false, false, true]);
    expect([...el.querySelectorAll('tfoot td')].map(cell).slice(0, 4)).toEqual([
      'Standing invoices · الفواتير السارية',
      '7.5 kg 7,500 g',
      'Average L.E 20.77 / kg',
      'L.E 135.00',
    ]);
    expect(text(el.querySelector('p.small')!)).toBe(
      '1 line has no price, so the average leaves it out. 1 voided invoice is shown for the paper, and not counted.',
    );
  });

  it('opens a row’s invoice in the archive, and the correction from a voided one', async () => {
    const { el, navigate } = await render();

    (el.querySelectorAll('tbody tr')[1] as HTMLElement).click();
    expect(close).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/costing/purchases/invoices', 'c7']);

    el.querySelector<HTMLButtonElement>('[aria-label="View invoice F-2 of 17 Oct 2026"]')!.click();
    expect(navigate).toHaveBeenLastCalledWith(['/costing/purchases/invoices', 'f2']);

    [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b) === 'Corrected by a new invoice')!.click();
    expect(navigate).toHaveBeenLastCalledWith(['/costing/purchases/invoices', 'f1b']);
    expect(navigate).toHaveBeenCalledTimes(3);
  });

  it('says when nothing has bought it, and when the card cannot be read', async () => {
    materialPurchases.mockReturnValue(of({ ...card, lines: [], baseQuantity: 0, total: 0, averagePerPurchaseUnit: null, unpricedLines: 0 }));
    const empty = await render();
    expect(text(empty.el.querySelector('mat-dialog-content')!)).toBe('No invoice has bought Tomatoes yet.');
    TestBed.resetTestingModule();

    materialPurchases.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    const failed = await render();
    expect(text(failed.el.querySelector('[role=alert]')!)).toBe('The purchase history could not be loaded.');
  });
});
