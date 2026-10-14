import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, DailyCashLine, DailyCashReport } from '../../core/costing/costing.api';
import { DailyCashPage } from './daily-cash-page';

function line(overrides: Partial<DailyCashLine>): DailyCashLine {
  return {
    id: 'l', kind: 'InvoicePayment', date: '2026-10-13', description: 'Invoice', amount: 0, purchaseInvoiceId: null, items: [], recordedBy: 'Omar',
    approvedBy: null, ...overrides,
  };
}

// The 12th: two invoices paid out of its takings, the rest counted into the safe the next morning.
// The 11th: an invoice paid out of it, nothing counted in yet.
const report: DailyCashReport = {
  from: '2026-10-01', to: '2026-10-31', gross: 21_544.72, deductions: 5_598.72, netToSafe: 15_946,
  days: [
    {
      date: '2026-10-12', gross: 21_474.72, deductions: 5_528.72, netToSafe: 15_946,
      paid: [
        line({ id: 'veg', description: 'Invoice of 12 Oct · الخضار', amount: 439, purchaseInvoiceId: 'inv-veg', items: ['طماطم', 'كرنب'] }),
        line({ id: 'coal', description: 'Invoice F-3', amount: 5_089.72, purchaseInvoiceId: 'inv-coal', items: ['فحم'], recordedBy: 'Mona', approvedBy: 'Omar' }),
      ],
      counted: [line({ id: 'in', kind: 'RevenueTransfer', description: 'Counted into the safe', amount: 15_946 })],
    },
    {
      date: '2026-10-11', gross: 70, deductions: 70, netToSafe: 0,
      paid: [line({ id: 'm7', description: 'Invoice M-7', amount: 70, purchaseInvoiceId: 'inv-m7', items: ['بصل'] })],
      counted: [],
    },
  ],
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the daily cash', () => {
  const dailyCash = vi.fn();

  async function render() {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: CostingApi, useValue: { dailyCash } }] });
    const fixture = TestBed.createComponent(DailyCashPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  const days = (el: HTMLElement) => [...el.querySelectorAll('tr.day')].map((r) => [...r.querySelectorAll('td')].slice(1).map(text));
  // An invoice paid: when, the invoice, what it bought, how much, and who.
  const paid = (details: Element) =>
    [...details.querySelectorAll('tr.paid')].map((r) => {
      const [when, what, amount, by] = [...r.querySelectorAll('td')];
      return [text(when), text(what.querySelector('a')!), text(what.querySelector('.small')!), text(amount), text(by)];
    });

  beforeEach(() => dailyCash.mockReset().mockReturnValue(of(report)));

  it('shows each day of the month: its gross takings, what was paid out of them, and what reached the safe', async () => {
    const { el } = await render();

    const now = new Date();
    expect(dailyCash.mock.lastCall![0]).toBe(`${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-01`);
    expect([...el.querySelectorAll('.tile')].map((t) => [text(t.querySelector('.label')!), text(t.querySelector('strong')!)])).toEqual([
      ['Gross takings · إجمالي الإيراد', 'L.E 21,544.72'],
      ['Total deductions · إجمالي المنصرف', 'L.E 5,598.72'],
      ['Net to safe · الصافي للخزنة', 'L.E 15,946.00'],
    ]);
    expect(days(el)).toEqual([
      ['Mon 12 Oct', 'L.E 21,474.72', 'L.E 5,528.72', 'L.E 15,946.00'],
      ['Sun 11 Oct', 'L.E 70.00', 'L.E 70.00', '—'],
    ]);
    expect([...el.querySelectorAll('tfoot td')].slice(1).map(text)).toEqual(['The month', 'L.E 21,544.72', 'L.E 5,598.72', 'L.E 15,946.00']);
    // Closed until opened.
    expect(el.querySelector('tr.details')).toBeNull();
  });

  it('opens a day to the invoices paid out of its takings and the cash counted into the safe', async () => {
    const { fixture, el } = await render();

    const open = el.querySelector<HTMLButtonElement>('tr.day button')!;
    expect(open.getAttribute('aria-expanded')).toBe('false');
    (el.querySelector('tr.day') as HTMLElement).click();
    fixture.detectChanges();

    expect(open.getAttribute('aria-expanded')).toBe('true');
    const details = el.querySelector('tr.details')!;
    expect(paid(details)).toEqual([
      ['13 Oct', 'Invoice of 12 Oct · الخضار', 'طماطم, كرنب', 'L.E 439.00', 'Omar'],
      ['13 Oct', 'Invoice F-3', 'فحم', 'L.E 5,089.72', 'Mona approved by Omar'],
    ]);
    expect(details.querySelector('tr.paid a')!.getAttribute('href')).toBe('/costing/purchases/invoices/inv-veg');
    expect([...details.querySelectorAll('tr.counted td')].map(text)).toEqual(['13 Oct', 'Counted into the safe', 'L.E 15,946.00', 'Omar']);

    // The button closes it again, and opens the 11th, nothing counted from it yet.
    open.click();
    (el.querySelectorAll<HTMLButtonElement>('tr.day button')[1]).click();
    fixture.detectChanges();
    expect(el.querySelectorAll('tr.details').length).toBe(1);
    expect(paid(el.querySelector('tr.details')!)).toEqual([['13 Oct', 'Invoice M-7', 'بصل', 'L.E 70.00', 'Omar']]);
    expect(text(el.querySelector('tr.details p')!)).toBe('Not counted into the safe yet.');
  });

  it('goes month by month, each opening closed', async () => {
    const { fixture, el, page } = await render();
    (el.querySelector('tr.day') as HTMLElement).click();
    fixture.detectChanges();

    dailyCash.mockReturnValue(of({ ...report, days: [], gross: 0, deductions: 0, netToSafe: 0 }));
    page['move'](-1);
    await fixture.whenStable();
    fixture.detectChanges();

    const before = new Date(new Date().getFullYear(), new Date().getMonth() - 1, 1);
    expect(dailyCash.mock.lastCall![0]).toBe(`${before.getFullYear()}-${String(before.getMonth() + 1).padStart(2, '0')}-01`);
    expect(text(el.querySelector('tbody')!)).toBe('No takings paid out or counted into the safe this month.');
    expect([...el.querySelectorAll('button')].map(text)).toContain('This month');
  });
});
