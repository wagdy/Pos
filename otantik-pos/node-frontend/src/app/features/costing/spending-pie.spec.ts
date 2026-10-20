import { TestBed } from '@angular/core/testing';
import { PieSlice, SpendingPie, pieColors } from './spending-pie';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const words = (el: Element) => [...el.children].map(text).join(' ');

const slice = (key: string, amount: number, ar: string | null = null): PieSlice => ({ key, en: key, ar, amount });

describe('a spending pie', () => {
  async function render(slices: PieSlice[], extra: Record<string, string> = {}) {
    const fixture = TestBed.createComponent(SpendingPie);
    fixture.componentRef.setInput('heading', 'Expenses, by category');
    fixture.componentRef.setInput('headingAr', 'المصروفات حسب القسم');
    fixture.componentRef.setInput('slices', slices);
    fixture.componentRef.setInput('shareOf', 'of the expenses');
    for (const [name, value] of Object.entries(extra)) {
      fixture.componentRef.setInput(name, value);
    }
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const legend = () => [...el.querySelectorAll('.legend li')].map((li) => [...li.querySelectorAll('.name, .amount, .percent')].map(text));
    return { el, refresh, legend };
  }

  it('draws a slice for each, largest first, with a legend of every amount and share, and the total in the middle', async () => {
    const { el, legend } = await render([slice('Utilities', 2_000), slice('Rent', 8_000, 'إيجار'), slice('Nothing', 0), slice('Repairs', 500)]);

    expect(text(el.querySelector('h3')!)).toBe('Expenses, by category المصروفات حسب القسم');
    expect(legend()).toEqual([
      ['Rent إيجار', 'L.E 8,000.00', '76.2%'],
      ['Utilities', 'L.E 2,000.00', '19.0%'],
      ['Repairs', 'L.E 500.00', '4.8%'],
    ]);
    const paths = [...el.querySelectorAll('path')];
    expect(paths.map((p) => p.getAttribute('fill'))).toEqual(pieColors.slice(0, 3));
    // Rent starts at the top and, more than half, sweeps the long way round.
    expect(paths[0].getAttribute('d')).toMatch(/^M 100 10 A 90 90 0 1 1 /);
    expect(paths[1].getAttribute('d')).toMatch(/A 90 90 0 0 1 /);
    expect(words(el.querySelector('.centre')!)).toBe('L.E 10,500');
    expect(el.querySelector('svg')!.getAttribute('aria-label')).toBe(
      'Expenses, by category: Rent 8000.00 L.E, 76.2%; Utilities 2000.00 L.E, 19.0%; Repairs 500.00 L.E, 4.8%',
    );
    expect([...el.querySelectorAll<HTMLElement>('.legend .swatch')].map((s) => s.style.background)).toEqual([
      'rgb(42, 120, 214)',
      'rgb(235, 104, 52)',
      'rgb(27, 153, 139)',
    ]);
  });

  it('says the amount and the share of a slice pointed at, on the pie or in the legend, and fades the rest', async () => {
    const { el, refresh } = await render([slice('Rent', 8_000, 'إيجار'), slice('Utilities', 2_000)]);
    expect(el.querySelector('[role=tooltip]')).toBeNull();

    el.querySelectorAll('path')[0].dispatchEvent(new Event('mouseenter'));
    await refresh();
    expect(words(el.querySelector('[role=tooltip]')!)).toBe('Rent إيجار L.E 8,000.00 80.0% of the expenses');
    expect([...el.querySelectorAll('path')].map((p) => p.classList.contains('faded'))).toEqual([false, true]);

    el.querySelectorAll('path')[0].dispatchEvent(new Event('mouseleave'));
    el.querySelectorAll('.legend li')[1].dispatchEvent(new Event('focus'));
    await refresh();
    expect(words(el.querySelector('[role=tooltip]')!)).toBe('Utilities L.E 2,000.00 20.0% of the expenses');
    expect(el.querySelectorAll('.legend li')[1].classList).toContain('active');

    el.querySelectorAll('.legend li')[1].dispatchEvent(new Event('blur'));
    await refresh();
    expect(el.querySelector('[role=tooltip]')).toBeNull();
  });

  it('puts the smallest together as Other past eight', async () => {
    const amounts = [900, 800, 700, 600, 500, 400, 300, 200, 100];
    const { el, legend, refresh } = await render(amounts.map((a, i) => slice(`C${i + 1}`, a)));

    expect(legend().map((row) => row[0])).toEqual(['C1', 'C2', 'C3', 'C4', 'C5', 'C6', 'C7', 'Other أخرى']);
    expect(legend().at(-1)).toEqual(['Other أخرى', 'L.E 300.00', '6.7%']);
    expect(el.querySelectorAll('path')[7].getAttribute('fill')).toBe('#7a8794');

    el.querySelectorAll('.legend li')[7].dispatchEvent(new Event('mouseenter'));
    await refresh();
    expect(words(el.querySelector('[role=tooltip]')!)).toBe('Other أخرى 2 categories L.E 300.00 6.7% of the expenses');
  });

  it('draws one that is everything as a whole ring', async () => {
    const { el, legend } = await render([slice('Dairy', 300), slice('Meat', 0)]);

    expect(legend()).toEqual([['Dairy', 'L.E 300.00', '100.0%']]);
    const d = el.querySelector('path')!.getAttribute('d')!;
    expect(d).toBe(
      'M 100 10 A 90 90 0 1 1 100 190 A 90 90 0 1 1 100 10 Z M 100 46 A 54 54 0 1 0 100 154 A 54 54 0 1 0 100 46 Z',
    );
  });

  it('says so when there is nothing to draw', async () => {
    const { el } = await render([slice('Rent', 0)], { empty: 'No expenses in these days.' });

    expect(el.querySelector('svg')).toBeNull();
    expect(el.querySelector('.legend')).toBeNull();
    expect(text(el.querySelector('.none')!)).toBe('No expenses in these days.');
  });
});
