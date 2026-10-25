import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AssetItem, CustodyApi } from '../../core/custody/custody.api';
import { AssetDialog, CheckInDialog, CheckOutDialog } from './custody-dialogs';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function asset(extra: Partial<AssetItem> = {}): AssetItem {
  return {
    id: 'plates', name: 'Dinner plates', categoryId: 'tableware', categoryName: 'Tableware', isConsumable: true, unitCost: 35, quantity: 40, totalCost: 1_400,
    employeeId: 'hassan', employeeName: 'Hassan', origin: 'HrManual', status: 'InCustody', sourceInvoiceId: null, notes: null, createdBy: 'Omar',
    createdAtUtc: '2026-10-20T09:00:00Z', ...extra,
  };
}

describe('the custody dialogs', () => {
  const saveAsset = vi.fn();
  const checkIn = vi.fn();
  const checkOut = vi.fn();
  const close = vi.fn();

  async function render<T>(component: Type<T>, data: unknown) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CustodyApi, useValue: { saveAsset, checkIn, checkOut } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    });
    const fixture = TestBed.createComponent(component);
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.componentRef.changeDetectorRef.markForCheck();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    await refresh();
    return { el, refresh, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    for (const f of [saveAsset, checkIn, checkOut, close]) {
      f.mockReset();
    }
    const applied = of({ outcome: 'Applied', asset: asset(), voucher: null, request: null });
    saveAsset.mockReturnValue(applied);
    checkIn.mockReturnValue(applied);
    checkOut.mockReturnValue(applied);
  });

  it('adds an asset with how many there are, and changes one without', async () => {
    const { page } = await render(AssetDialog, { categories: [], canApprove: true });
    expect(page['problem']()).toBe('Give it its name.');
    Object.assign(page, { name: ' Dinner plates ', categoryId: 'tableware', unitCost: 35, quantity: 40, notes: ' Kitchen ' });
    expect(page['problem']()).toBeNull();
    page['quantity'] = 0;
    expect(page['problem']()).toBe('Say how many there are: 1 or more.');
    page['quantity'] = 40;
    await page['save']();
    const [id, input] = saveAsset.mock.lastCall!;
    expect(input).toMatchObject({ name: 'Dinner plates', categoryId: 'tableware', unitCost: 35, quantity: 40, notes: 'Kitchen', requestReason: null });
    expect(id).not.toBe(input.requestId);
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ outcome: 'Applied' }));

    TestBed.resetTestingModule();
    const existing = await render(AssetDialog, { asset: asset({ origin: 'FinancialPurchase' }), categories: [], canApprove: false });
    const inputs = [...existing.el.querySelectorAll('input')];
    expect(inputs.find((i) => i.getAttribute('aria-label') === 'How many')!.disabled).toBe(true);
    expect(inputs.find((i) => i.getAttribute('aria-label') === 'Cost a piece')!.disabled).toBe(true);
    expect(text(existing.el.querySelector('mat-dialog-actions')!)).toContain('Send for approval');
    expect(existing.el.querySelector('input[aria-label="Reason"]')).not.toBeNull();
  });

  it('checks in to the employee chosen, on the day chosen', async () => {
    const { el, page } = await render(CheckInDialog, { asset: asset({ status: 'InStore', employeeId: null, employeeName: null }), employees: [], today: '2026-10-25', canApprove: true });
    expect(text(el.querySelector('p.muted')!)).toBe('All 40 of it into their custody, from the store.');
    Object.assign(page, { employeeId: 'mona', date: '2026-10-24', notes: ' New shift ' });
    await page['save']();
    expect(checkIn.mock.lastCall![0]).toBe('plates');
    expect(checkIn.mock.lastCall![1]).toMatchObject({ employeeId: 'mona', date: '2026-10-24', notes: 'New shift' });
  });

  it('asks no cost of one who is not shown it, for an asset there already, and keeps its cost', async () => {
    const { el, page } = await render(AssetDialog, { asset: asset({ unitCost: null, totalCost: null }), categories: [], canApprove: false, costHidden: true });
    expect(el.querySelector('input[aria-label="Cost a piece"]')).toBeNull();
    Object.assign(page, { name: 'Dinner plates (white)', reason: 'Their colour' });
    await page['save']();
    expect(saveAsset.mock.lastCall![1]).toMatchObject({ name: 'Dinner plates (white)', unitCost: null });

    TestBed.resetTestingModule();
    const added = await render(AssetDialog, { categories: [], canApprove: false, costHidden: true });
    expect(added.el.querySelector('input[aria-label="Cost a piece"]')).not.toBeNull();
  });

  it('says a breakage goes to Waste without an amount when the cost is not known here', async () => {
    const { el, page, refresh } = await render(CheckOutDialog, { asset: asset({ unitCost: null, totalCost: null }), today: '2026-10-25', canApprove: false });
    page['choose']('Breakage');
    page['quantity'].set(2);
    await refresh();
    expect(text(el.querySelector('.waste')!)).toBe('delete_sweep Goes to Costing → Waste as breakage.');
  });

  it('checks out all of it handed back, some of it broken, and says a consumable’s breakage goes to Waste', async () => {
    const { el, page, refresh } = await render(CheckOutDialog, { asset: asset(), today: '2026-10-25', canApprove: true });
    // With someone: handed back to start with, all 40, fixed.
    expect(page['why']()).toBe('Returned');
    expect(page['reasons']).toEqual(['Returned', 'Maintenance', 'Breakage', 'Lost', 'Discarded']);
    expect(page['quantity']()).toBe(40);
    expect(page['all']()).toBe(true);

    page['choose']('Breakage');
    page['quantity'].set(3);
    await refresh();
    expect(text(el.querySelector('.waste')!)).toBe('delete_sweep Goes to Costing → Waste as breakage, at L.E 105.00.');
    page['quantity'].set(41);
    expect(page['problem']()).toBe('Between 1 and 40 of it.');
    page['quantity'].set(3);
    await page['save']();
    expect(checkOut.mock.lastCall![1]).toMatchObject({ reason: 'Breakage', quantity: 3, date: '2026-10-25', requestReason: null });

    // Back to handed back: all of it again.
    page['choose']('Returned');
    expect(page['quantity']()).toBe(40);
  });

  it('offers what fits where it is, and says a fixed asset broken is not waste', async () => {
    const { el, page, refresh } = await render(CheckOutDialog, {
      asset: asset({ status: 'InStore', employeeId: null, employeeName: null, isConsumable: false, name: 'Fryer', quantity: 1 }), today: '2026-10-25', canApprove: true,
    });
    expect(page['reasons']).toEqual(['Maintenance', 'Breakage', 'Lost', 'Discarded']);
    expect(page['why']()).toBe('Breakage');
    await refresh();
    expect(text(el.querySelector('p.muted.small')!)).toBe("A fixed asset: broken, it is not the day's waste.");
  });
});
