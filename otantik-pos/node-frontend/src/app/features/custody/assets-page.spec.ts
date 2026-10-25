import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalRequest } from '../../core/costing/costing.api';
import { AssetCategory, AssetItem, CustodyApi } from '../../core/custody/custody.api';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { AssetsPage } from './assets-page';
import { AssetDialog, AssetHistoryDialog, CheckInDialog, CheckOutDialog } from './custody-dialogs';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

export function asset(id: string, name: string, extra: Partial<AssetItem> = {}): AssetItem {
  return {
    id, name, categoryId: 'tableware', categoryName: 'Tableware', isConsumable: true, unitCost: 35, quantity: 40, totalCost: 1_400, employeeId: null,
    employeeName: null, origin: 'HrManual', status: 'InStore', sourceInvoiceId: null, notes: null, createdBy: 'Omar', createdAtUtc: '2026-10-20T09:00:00Z',
    ...extra,
  };
}

const tableware: AssetCategory = { id: 'tableware', name: 'Tableware', isConsumable: true, assets: 2, inCustody: 1 };
const devices: AssetCategory = { id: 'devices', name: 'Devices', isConsumable: false, assets: 1, inCustody: 0 };
const plates = asset('plates', 'Dinner plates', { status: 'InCustody', employeeId: 'hassan', employeeName: 'Hassan', quantity: 37, totalCost: 1_295 });
const glasses = asset('glasses', 'Water glasses', { quantity: 24, unitCost: 20, totalCost: 480 });
const fryer = asset('fryer', 'Fryer', { categoryId: 'devices', categoryName: 'Devices', isConsumable: false, quantity: 0, unitCost: 9_000, totalCost: 0, status: 'BrokenWaste' });
const bought = asset('cups', 'Coffee cups', { origin: 'FinancialPurchase', sourceInvoiceId: 'inv', quantity: 12, unitCost: 25, totalCost: 300 });

describe('the assets', () => {
  const assets = vi.fn();
  const employees = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const pending = signal<ApprovalRequest[]>([]);
  const approvals = {
    canApprove: signal(true),
    refresh: vi.fn(),
    requested: vi.fn(),
    pendingFor: (id: string) => pending().find((r) => r.recordId === id),
  };

  async function render(manage = true) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CustodyApi, useValue: { assets, categories: () => of([tableware, devices]) } },
        { provide: HrApi, useValue: { employees } },
        { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, can: (p: string) => manage || p !== Permissions.HrManage } },
        { provide: Approvals, useValue: approvals },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-25') } },
      ],
    });
    const fixture = TestBed.createComponent(AssetsPage);
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    };
    await refresh();
    const rows = () => [...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].slice(0, 5).map(cell));
    return { el, refresh, rows, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    localStorage.clear();
    assets.mockReset().mockImplementation((withFinancial: boolean) => of(withFinancial ? [bought, plates, glasses, fryer] : [plates, glasses, fryer]));
    employees.mockReset().mockReturnValue(of([
      { id: 'hassan', name: 'Hassan', jobTitle: 'Chef', isActive: true },
      { id: 'gone', name: 'Gone', jobTitle: 'Chef', isActive: false },
    ] as Employee[]));
    open.mockReset();
    notify.info.mockReset();
    pending.set([]);
    approvals.requested.mockReset();
    approvals.canApprove.set(true);
  });

  it('lists what there is of each, its cost and whose custody it is in; what is gone only when asked', async () => {
    const { el, page, rows, refresh } = await render();

    expect(assets).toHaveBeenCalledWith(false);
    expect(rows()).toEqual([
      ['Dinner plates Tableware · consumable', '37', 'L.E 35.00', 'L.E 1,295.00', 'Hassan In custody في العهدة'],
      ['Water glasses Tableware · consumable', '24', 'L.E 20.00', 'L.E 480.00', 'In the store بالمخزن'],
    ]);
    expect(text(el.querySelector('.toolbar .muted')!)).toBe('2 assets · L.E 1,775.00');

    page['showGone'].set(true);
    await refresh();
    expect(rows()[2]).toEqual(['Fryer Devices', '0', 'L.E 9,000.00', 'L.E 0.00', 'Broken كسر هالك']);
    // Gone: nothing to check in or out.
    expect(el.querySelectorAll('tbody tr')[2].querySelector('button[aria-label^="Check"]')).toBeNull();

    page['category'].set('devices');
    await refresh();
    expect(rows().map((r) => r[0])).toEqual(['Fryer Devices']);
  });

  it('adds those bought on invoices with Link with Financials, and remembers it', async () => {
    const { page, rows, refresh } = await render();

    page['link'](true);
    await refresh();
    expect(assets).toHaveBeenLastCalledWith(true);
    expect(rows()[0][0]).toBe('Coffee cups Bought · مشتريات Tableware · consumable');
    expect(localStorage.getItem('otantik.custody.with-financial')).toBe('true');

    TestBed.resetTestingModule();
    await render();
    expect(assets).toHaveBeenLastCalledWith(true);
  });

  it('checks in to one employed now, checks out, and says when a breakage went to Waste or waits for a manager', async () => {
    const { el, page } = await render();
    const glassesRow = el.querySelectorAll('tbody tr')[1];
    expect([...glassesRow.querySelectorAll('button')].map(text)).toEqual(['history', 'login In', 'logout Out', 'edit']);
    // In custody: out, not in.
    expect([...el.querySelectorAll('tbody tr')[0].querySelectorAll('button')].map(text)).toEqual(['history', 'logout Out', 'edit']);

    open.mockReturnValue({ afterClosed: () => of({ outcome: 'Applied', asset: glasses, voucher: null, request: null }) });
    await page['checkIn'](glasses);
    expect(open.mock.lastCall![0]).toBe(CheckInDialog);
    expect(open.mock.lastCall![1].data).toMatchObject({ asset: glasses, today: '2026-10-25', canApprove: true });
    expect(open.mock.lastCall![1].data.employees.map((e: Employee) => e.id)).toEqual(['hassan']);
    expect(notify.info).toHaveBeenLastCalledWith('Water glasses checked in.');

    open.mockReturnValue({ afterClosed: () => of({ outcome: 'Applied', asset: plates, voucher: { routedToWaste: true }, request: null }) });
    await page['checkOut'](plates);
    expect(open.mock.lastCall![0]).toBe(CheckOutDialog);
    expect(notify.info).toHaveBeenLastCalledWith('Dinner plates checked out. Its breakage is in Costing → Waste.');

    open.mockReturnValue({ afterClosed: () => of({ outcome: 'Requested', request: { id: 'r', action: 'Update' } }) });
    await page['checkOut'](plates);
    expect(approvals.requested).toHaveBeenCalled();

    // Cancelled: nothing said, nothing read again.
    const reads = assets.mock.calls.length;
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    await page['edit'](plates);
    expect(open.mock.lastCall![0]).toBe(AssetDialog);
    expect(open.mock.lastCall![1].data).toEqual({ asset: plates, categories: [tableware, devices], canApprove: true });
    expect(assets.mock.calls.length).toBe(reads);

    page['history'](plates);
    expect(open.mock.lastCall![0]).toBe(AssetHistoryDialog);
    expect(open.mock.lastCall![1].data).toEqual({ assetId: 'plates' });
  });

  it('locks an asset a manager is asked about, and offers no changes to whoever only sees it', async () => {
    pending.set([{ id: 'r1', module: 'Custody', action: 'Update', recordId: 'plates', summary: 'Out of Hassan’s custody, lost · Dinner plates × 1', requestedBy: 'Sara' }] as ApprovalRequest[]);
    const { el } = await render();
    const platesRow = el.querySelectorAll('tbody tr')[0];
    expect(platesRow.querySelector('mat-icon.locked')).not.toBeNull();
    expect([...platesRow.querySelectorAll('button')].map(text)).toEqual(['history']);

    TestBed.resetTestingModule();
    pending.set([]);
    const viewer = await render(false);
    expect([...viewer.el.querySelectorAll('tbody button')].map(text)).toEqual(['history', 'history']);
    expect([...viewer.el.querySelectorAll('.toolbar button')].map(text).some((t) => t.includes('Add an asset'))).toBe(false);
  });
});
