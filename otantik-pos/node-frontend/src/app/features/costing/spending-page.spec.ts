import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { CostingApi, SpendingReport } from '../../core/costing/costing.api';
import { SpendingPage } from './spending-page';

const report: SpendingReport = {
  from: '2026-10-01',
  to: '2026-10-08',
  total: 10_600,
  inventory: 300,
  expenses: 10_300,
  invoices: 2,
  unpricedLines: 1,
  categories: [
    {
      categoryId: 'rent', category: 'Rent', categoryAr: 'إيجار', expenseLine: 'Occupation', inventory: 0, expenses: 8_000, total: 8_000, sharePercent: 75.5,
      items: [{ itemType: 'Expense', itemId: 'shop', name: 'Shop rent', quantity: 1, unit: 'unit', total: 8_000, lines: 1 }],
    },
    {
      categoryId: 'utilities', category: 'Utilities', categoryAr: null, expenseLine: 'Controllable', inventory: 0, expenses: 2_000, total: 2_000, sharePercent: 18.9,
      items: [
        { itemType: 'Expense', itemId: 'power', name: 'Electricity', quantity: 1, unit: 'unit', total: 1_500, lines: 1 },
        { itemType: 'Expense', itemId: 'spray', name: 'Cleaning, "pro" spray', quantity: 4, unit: 'bottle', total: 500, lines: 1 },
      ],
    },
    {
      categoryId: 'dairy', category: 'Dairy', categoryAr: null, expenseLine: 'Controllable', inventory: 300, expenses: 300, total: 600, sharePercent: 5.7,
      items: [{ itemType: 'Inventory', itemId: 'milk', name: 'Milk', quantity: 10, unit: 'L', total: 300, lines: 1 }],
    },
  ],
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

describe('purchases by category', () => {
  const spending = vi.fn();

  async function render() {
    TestBed.configureTestingModule({ providers: [{ provide: CostingApi, useValue: { spending } }] });
    const fixture = TestBed.createComponent(SpendingPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    return { el, refresh, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => spending.mockReset().mockReturnValue(of(report)));

  it('starts with this month so far', async () => {
    await render();
    const today = new Date();
    expect(spending).toHaveBeenCalledExactlyOnceWith(iso(new Date(today.getFullYear(), today.getMonth(), 1)), iso(today));
  });

  it('draws a bar per category against the largest, split into stock and expenses, with its total and share', async () => {
    const { el } = await render();

    const rows = [...el.querySelectorAll('.bar-row')];
    expect(rows.map((r) => text(r.querySelector('.name')!))).toEqual(['Rent إيجار', 'Utilities', 'Dairy']);
    const widths = (row: Element) => [...row.querySelectorAll<HTMLElement>('.segment')].map((s) => [s.className.replace('segment ', ''), s.style.width]);
    expect(widths(rows[0])).toEqual([['expense', '100%']]);
    expect(widths(rows[1])).toEqual([['expense', '25%']]);
    expect(widths(rows[2])).toEqual([['stock', '3.75%'], ['expense', '3.75%']]);
    expect(text(rows[2].querySelector('.value')!)).toBe('L.E 600.00 5.7%');
    expect(rows[2].getAttribute('aria-label')).toBe('Dairy: 600.00 L.E, 300.00 into stock and 300.00 expenses, 5.7% of all spending');
    expect([...el.querySelectorAll('.tile strong')].map(text)).toEqual(['L.E 10,600.00', 'L.E 300.00', 'L.E 10,300.00', '2']);
    expect(text(el)).toContain('1 line was received with no price');
  });

  it('shows the figures on hover, and what was bought when a category is opened', async () => {
    const { el, page, refresh } = await render();

    el.querySelectorAll('.bar-row')[1].dispatchEvent(new Event('mouseenter'));
    await refresh();
    expect([...el.querySelectorAll('.tip .tip-row')].map(text)).toEqual(['Into stock L.E 0.00', 'Expenses L.E 2,000.00', 'Share of all spending 18.9%']);

    page['toggle']('utilities');
    await refresh();
    const items = [...el.querySelectorAll('tr.item')].map((r) => text(r.querySelector('td')!));
    expect(items).toEqual(['Electricity Expense · مصروف', 'Cleaning, "pro" spray Expense · مصروف']);
  });

  it('takes a preset or any two days', async () => {
    const { page } = await render();
    const today = new Date();

    page['choose']('last-month');
    TestBed.tick();
    expect(spending).toHaveBeenLastCalledWith(
      iso(new Date(today.getFullYear(), today.getMonth() - 1, 1)),
      iso(new Date(today.getFullYear(), today.getMonth(), 0)),
    );
    page['custom']('from', '2026-01-01');
    page['custom']('to', '2026-0');
    TestBed.tick();
    expect(page['period']()).toBe('custom');
    expect(spending.mock.lastCall![0]).toBe('2026-01-01');
  });

  it('exports a spreadsheet with a row per category and per item', async () => {
    const { page } = await render();
    let blob: Blob | undefined;
    vi.spyOn(URL, 'createObjectURL').mockImplementation((b) => {
      blob = b as Blob;
      return 'blob:spending';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    page['exportCsv']();

    const lines = new TextDecoder().decode(new Uint8Array(await blob!.arrayBuffer()).slice(3)).split('\r\n');
    expect(lines[0]).toBe('Category · الفئة,Item · الصنف,Type,Quantity,Unit,Into Stock,Expenses,Total,Share %');
    expect(lines[3]).toBe('Utilities,,,,,0.00,2000.00,2000.00,18.9');
    expect(lines[4]).toBe('Utilities,Electricity,Expense,1,unit,,1500.00,1500.00,');
    expect(lines[5]).toBe('Utilities,"Cleaning, ""pro"" spray",Expense,4,bottle,,500.00,500.00,');
    expect(lines.at(-1)).toBe('Total,,,,,300.00,10300.00,10600.00,100.0');
  });
});
