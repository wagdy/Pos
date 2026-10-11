import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { CostingApi, Supplier } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { SupplierDialog } from './supplier-dialog';
import { SuppliersPage } from './suppliers-page';

function supplier(overrides: Partial<Supplier>): Supplier {
  return {
    id: 's',
    name: 'Supplier',
    contactPerson: null,
    phone: null,
    email: null,
    notes: null,
    isArchived: false,
    invoices: 0,
    postedTotal: 0,
    lastPostedAtUtc: null,
    ...overrides,
  };
}

const nileFarms = supplier({ id: 'nile', name: 'Nile Farms', contactPerson: 'Hassan', phone: '0100 123 4567', invoices: 3, postedTotal: 420, lastPostedAtUtc: '2026-10-05T08:00:00Z' });
const deltaOils = supplier({ id: 'delta', name: 'Delta Oils' });
const oldBakery = supplier({ id: 'bakery', name: 'Old Bakery', isArchived: true, invoices: 1, postedTotal: 80 });

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the suppliers', () => {
  const suppliers = vi.fn();
  const deleteSupplier = vi.fn();
  const restoreSupplier = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { suppliers, deleteSupplier, restoreSupplier } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(SuppliersPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => {
      fixture.detectChanges();
      return [...el.querySelectorAll('tbody tr')];
    };
    const button = (row: Element, label: string) => [...row.querySelectorAll('button')].find((b) => text(b).includes(label) || b.getAttribute('aria-label')?.startsWith(label))!;
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any>, rows, button };
  }

  beforeEach(() => {
    suppliers.mockReset().mockReturnValue(of([deltaOils, nileFarms, oldBakery]));
    deleteSupplier.mockReset();
    restoreSupplier.mockReset().mockReturnValue(of({ ...oldBakery, isArchived: false }));
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('lists them with how to reach them and what was bought, the archived ones only when asked', async () => {
    const { page, rows } = await render();

    expect(suppliers).toHaveBeenCalledWith(true);
    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 6).map(text))).toEqual([
      ['Delta Oils', '—', '—', '0', 'L.E 0.00', '—'],
      ['Nile Farms', 'Hassan', '0100 123 4567', '3', 'L.E 420.00', '5 Oct 2026'],
    ]);

    page['showArchived'].set(true);
    expect(rows().map((r) => text(r.querySelector('td')!))).toEqual(['Delta Oils', 'Nile Farms', 'Old Bakery Archived · مؤرشف']);
  });

  it('adds and edits in the supplier dialog, and reloads after', async () => {
    open.mockReturnValue({ afterClosed: () => of(nileFarms) });
    const { page, rows, button } = await render();

    await page['add']();
    expect(open).toHaveBeenLastCalledWith(SupplierDialog, expect.objectContaining({ data: {} }));

    button(rows()[1], 'Edit').click();
    expect(open).toHaveBeenLastCalledWith(SupplierDialog, expect.objectContaining({ data: { supplier: nileFarms } }));
    await vi.waitFor(() => expect(suppliers).toHaveBeenCalledTimes(3));
  });

  it('asks before deleting one never invoiced, and deletes it for good', async () => {
    deleteSupplier.mockReturnValue(of({ outcome: 'Deleted' }));
    const { rows, button } = await render();

    button(rows()[0], 'Delete').click();
    expect(deleteSupplier).not.toHaveBeenCalled();
    const ask = rows()[1];
    expect(ask.classList).toContain('confirm-row');
    expect(text(ask)).toContain('Delete Delta Oils for good? It is on no invoice.');

    button(ask, 'Delete').click();
    await vi.waitFor(() => expect(deleteSupplier).toHaveBeenCalledWith('delta'));
    expect(notify.info).toHaveBeenCalledWith('Delta Oils is deleted.');
  });

  it('archives one on invoices instead, saying it stays on them', async () => {
    deleteSupplier.mockReturnValue(of({ outcome: 'Archived' }));
    const { rows, button } = await render();

    button(rows()[1], 'Delete').click();
    expect(text(rows()[2])).toContain('Archive Nile Farms? It is on 3 invoices, so it is kept for them');
    button(rows()[2], 'Archive').click();

    await vi.waitFor(() => expect(deleteSupplier).toHaveBeenCalledWith('nile'));
    expect(notify.info).toHaveBeenCalledWith('Nile Farms is archived: kept on its invoices, off the list for new ones.');
  });

  it('can be talked out of it', async () => {
    const { rows, button } = await render();

    button(rows()[1], 'Delete').click();
    button(rows()[2], 'Cancel').click();

    expect(rows().some((r) => r.classList.contains('confirm-row'))).toBe(false);
    expect(deleteSupplier).not.toHaveBeenCalled();
  });

  it('restores an archived one', async () => {
    const { page, rows, button } = await render();
    page['showArchived'].set(true);

    button(rows()[2], 'Restore').click();

    await vi.waitFor(() => expect(restoreSupplier).toHaveBeenCalledWith('bakery'));
    expect(notify.info).toHaveBeenCalledWith('Old Bakery is back on the list.');
  });

  it('finds one by name, contact or phone', async () => {
    const { page } = await render();

    page['search'].set('hass');
    expect(page['shown']()).toEqual([nileFarms]);
    page['search'].set('0100');
    expect(page['shown']()).toEqual([nileFarms]);
    page['search'].set('delta');
    expect(page['shown']()).toEqual([deltaOils]);
  });
});
