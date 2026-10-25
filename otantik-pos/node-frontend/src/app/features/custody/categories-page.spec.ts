import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AssetCategory, CustodyApi, AssetVoucher } from '../../core/custody/custody.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { CategoriesPage, CategoryDialog } from './categories-page';
import { VouchersPage } from './vouchers-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

const tableware: AssetCategory = { id: 'tableware', name: 'Tableware', isConsumable: true, assets: 2, inCustody: 1 };
const devices: AssetCategory = { id: 'devices', name: 'Devices', isConsumable: false, assets: 0, inCustody: 0 };

describe('the custody categories', () => {
  const categories = vi.fn();
  const saveCategory = vi.fn();
  const deleteCategory = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CustodyApi, useValue: { categories, saveCategory, deleteCategory } },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
      ],
    });
    const fixture = TestBed.createComponent(CategoriesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    categories.mockReset().mockReturnValue(of([devices, tableware]));
    saveCategory.mockReset().mockReturnValue(of(tableware));
    deleteCategory.mockReset().mockReturnValue(of(undefined));
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
    canApprove.set(true);
  });

  it('lists each with its kind and how many assets are in it, and a manager adds, renames and deletes', async () => {
    const { el, page } = await render();
    expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(cell))).toEqual([
      ['Devices', 'Fixed asset أصل ثابت', '0', '0'],
      ['Tableware', 'Consumable مستهلك', '2', '1'],
    ]);

    open.mockReturnValue({ afterClosed: () => of({ name: 'Uniforms', isConsumable: false }) });
    await page['add']();
    expect(open.mock.lastCall![0]).toBe(CategoryDialog);
    expect(saveCategory).toHaveBeenCalledWith(expect.any(String), 'Uniforms', false);
    expect(notify.info).toHaveBeenLastCalledWith('Uniforms added.');

    open.mockReturnValue({ afterClosed: () => of({ name: 'Tableware', isConsumable: false }) });
    await page['edit'](tableware);
    expect(open.mock.lastCall![1].data).toEqual({ category: tableware });
    expect(saveCategory).toHaveBeenLastCalledWith('tableware', 'Tableware', false);

    await page['remove'](devices);
    expect(deleteCategory).toHaveBeenCalledWith('devices');
  });

  it('shows whoever is not a manager the categories, with nothing to change', async () => {
    canApprove.set(false);
    const { el } = await render();
    expect(el.querySelectorAll('button').length).toBe(0);
  });
});

describe('the custody vouchers', () => {
  const vouchers = vi.fn();

  function voucher(id: string, extra: Partial<AssetVoucher> = {}): AssetVoucher {
    return {
      id, assetItemId: 'plates', assetName: 'Dinner plates', type: 'CheckOut', date: '2026-10-24', employeeId: 'hassan', employeeName: 'Hassan', quantity: 3,
      reason: 'Breakage', notes: 'Dropped a tray', unitCost: 35, cost: 105, recordedBy: 'Omar', recordedAtUtc: '2026-10-24T12:00:00Z', wasteEntryId: 'w1',
      routedToWaste: true, ...extra,
    };
  }

  it('lists the last 30 days’ vouchers, saying which breakage went to Waste, and reads the days chosen', async () => {
    vouchers.mockReturnValue(of([voucher('v2'), voucher('v1', { type: 'CheckIn', reason: null, quantity: 40, cost: 1_400, notes: null, wasteEntryId: null, routedToWaste: false })]));
    TestBed.configureTestingModule({
      providers: [
        { provide: CustodyApi, useValue: { vouchers } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-25') } },
      ],
    });
    const fixture = TestBed.createComponent(VouchersPage);
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const page = fixture.componentInstance as unknown as Record<string, any>;

    expect(vouchers).toHaveBeenCalledWith('2026-09-26', '2026-10-25');
    const rows = [...el.querySelectorAll('tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].slice(1, 6).map(cell))).toEqual([
      ['Dinner plates', 'Out · إذن خروج Broken · كسر In Waste · في الهالك Dropped a tray', 'Hassan', '3', 'L.E 105.00'],
      ['Dinner plates', 'In · إذن دخول', 'Hassan', '40', 'L.E 1,400.00'],
    ]);
    expect(rows[0].classList).toContain('waste');
    expect(text(el.querySelector('.toolbar .muted')!)).toBe('2 vouchers · 1 in Waste');

    page['pick']('from', '2026-10-01');
    expect(vouchers).toHaveBeenLastCalledWith('2026-10-01', '2026-10-25');
    // A first day after the last waits for the last to move.
    page['pick']('from', '2026-11-01');
    expect(vouchers).toHaveBeenCalledTimes(2);
  });
});
