import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { CostingApi, ExpenseItem } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { ExpenseItemDialog } from './expense-item-dialog';
import { ExpenseItemsPage } from './expense-items-page';

function item(overrides: Partial<ExpenseItem>): ExpenseItem {
  return {
    id: 'x', name: 'Item', nameAr: null, categoryId: 'utilities', category: 'Utilities', categoryAr: null, unit: null,
    isArchived: false, invoices: 0, postedTotal: 0, lastPostedAtUtc: null, ...overrides,
  };
}
const power = item({ id: 'power', name: 'Electricity', nameAr: 'الكهرباء', invoices: 2, postedTotal: 2900, lastPostedAtUtc: '2026-10-05T08:00:00Z' });
const spray = item({ id: 'spray', name: 'Cleaning spray', category: 'Cleaning', unit: 'bottle' });
const old = item({ id: 'old', name: 'Pest control', isArchived: true, invoices: 1, postedTotal: 400 });

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the expense items', () => {
  const expenseItems = vi.fn();
  const deleteExpenseItem = vi.fn();
  const restoreExpenseItem = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { expenseItems, deleteExpenseItem, restoreExpenseItem } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(ExpenseItemsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => {
      fixture.detectChanges();
      return [...el.querySelectorAll('tbody tr')];
    };
    return { rows, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    expenseItems.mockReset().mockReturnValue(of([spray, power, old]));
    deleteExpenseItem.mockReset();
    restoreExpenseItem.mockReset().mockReturnValue(of(old));
    open.mockReset();
    notify.info.mockReset();
  });

  it('lists each under its category, with what was spent on it; archived ones only when asked', async () => {
    const { rows, page } = await render();

    expect(expenseItems).toHaveBeenCalledWith(true);
    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 5).map(text))).toEqual([
      ['Cleaning spray', 'Cleaning', 'bottle', '0', 'L.E 0.00'],
      ['Electricity الكهرباء', 'Utilities', '—', '2', 'L.E 2,900.00'],
    ]);

    page['search'].set('clean');
    expect(rows()).toHaveLength(1);
    page['search'].set('');
    page['showArchived'].set(true);
    expect(text(rows()[2])).toContain('Pest control Archived · مؤرشف');
  });

  it('archives one that is on invoices, and says so', async () => {
    deleteExpenseItem.mockReturnValue(of({ outcome: 'Archived' }));
    const { page, rows } = await render();

    page['confirming'].set('power');
    expect(text(rows()[2])).toContain('Archive Electricity? It is on 2 invoices');
    await page['remove'](power);

    expect(deleteExpenseItem).toHaveBeenCalledWith('power');
    expect(notify.info).toHaveBeenCalledWith(expect.stringContaining('Electricity is archived'));
  });

  it('adds and edits in the expense item dialog, and restores an archived one', async () => {
    open.mockReturnValue({ afterClosed: () => of(spray) });
    const { page } = await render();

    await page['add']();
    expect(open).toHaveBeenLastCalledWith(ExpenseItemDialog, expect.objectContaining({ data: {} }));
    await page['edit'](spray);
    expect(open).toHaveBeenLastCalledWith(ExpenseItemDialog, expect.objectContaining({ data: { item: spray } }));
    await page['restore'](old);
    expect(restoreExpenseItem).toHaveBeenCalledWith('old');
  });
});
