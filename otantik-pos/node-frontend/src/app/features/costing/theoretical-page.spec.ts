import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, ManualSalesSheet, TheoreticalCostReport } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { TheoreticalPage } from './theoretical-page';

const report: TheoreticalCostReport = {
  year: 2026,
  month: 10,
  from: '2026-10-01',
  to: '2026-10-31',
  foodCostTargetPercent: 30,
  rows: [
    {
      itemCode: '6',
      menuItemId: 6,
      variantId: null,
      menuItem: 'Classic Cheeseburger',
      menuItemAr: 'تشيز برجر',
      category: 'Mains',
      quantitySold: 3,
      netSales: 390,
      recipeCostPerUnit: 27,
      theoreticalCost: 81,
      sharedCost: 0,
      foodCostPercent: 20.8,
      status: 'WithinTarget',
      quantityCostedNow: 1,
    },
    {
      itemCode: '10-101',
      menuItemId: 10,
      variantId: 101,
      menuItem: 'Shawarma, "1 kg" tray',
      menuItemAr: null,
      category: 'Mains',
      quantitySold: 1,
      netSales: 400,
      recipeCostPerUnit: 0,
      theoreticalCost: 0,
      sharedCost: 0,
      foodCostPercent: null,
      status: 'NoRecipe',
      quantityCostedNow: 0,
    },
  ],
  quantitySold: 4,
  netSales: 790,
  theoreticalCost: 81,
  foodCostPercent: 10.3,
  source: 'Pos',
  enteredBy: null,
  enteredAtUtc: null,
};

const sheet: ManualSalesSheet = {
  year: 2026,
  month: 10,
  enteredBy: null,
  enteredAtUtc: null,
  items: [
    { kind: 'Recipe', itemId: 'r-burger', code: '6', name: 'Classic Cheeseburger', nameAr: 'تشيز برجر', group: 'Mains', unit: 'portion', costPerUnit: 27, price: 130, quantity: null },
    { kind: 'Recipe', itemId: 'r-staff', code: null, name: 'Staff meal', nameAr: null, group: 'Mains', unit: 'portion', costPerUnit: 15, price: null, quantity: null },
    { kind: 'PrepItem', itemId: 'p-dough', code: 'PREP-1', name: 'Pizza dough', nameAr: null, group: 'Prep items', unit: 'kg', costPerUnit: 12, price: null, quantity: null },
  ],
};

const manualReport: TheoreticalCostReport = {
  ...report,
  rows: [
    { ...report.rows[0], quantitySold: 10, netSales: 1300, theoreticalCost: 270, quantityCostedNow: 0, recipeId: 'r-burger' },
    {
      ...report.rows[0],
      itemCode: 'PREP-1',
      menuItemId: 0,
      menuItem: 'Pizza dough',
      menuItemAr: null,
      category: 'Prep items',
      quantitySold: 2.5,
      netSales: 0,
      recipeCostPerUnit: 12,
      theoreticalCost: 30,
      foodCostPercent: null,
      status: 'NoPrice',
      quantityCostedNow: 0,
      unit: 'kg',
      prepItemId: 'p-dough',
    },
  ],
  quantitySold: 10,
  netSales: 1300,
  theoreticalCost: 300,
  foodCostPercent: 23.1,
  source: 'Manual',
  enteredBy: 'Sara',
  enteredAtUtc: '2026-10-08T12:30:00Z',
};

describe('the monthly theoretical cost report', () => {
  async function render(saved: ManualSalesSheet = sheet) {
    const theoretical = vi.fn((_year: number, _month: number, source = 'Pos') => of(source === 'Manual' ? manualReport : report));
    const manualSales = vi.fn().mockReturnValue(of(saved));
    const saveManualSales = vi.fn().mockReturnValue(of(manualReport));
    const info = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { theoretical, manualSales, saveManualSales } },
        { provide: AuthService, useValue: { can: () => true } },
        { provide: NotifyService, useValue: { info, error: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(TheoreticalPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, theoretical, manualSales, saveManualSales, info };
  }

  function page(fixture: { nativeElement: unknown }): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  async function chooseManual(fixture: Awaited<ReturnType<typeof render>>['fixture']) {
    const toggle = [...page(fixture).querySelectorAll<HTMLButtonElement>('mat-button-toggle button')].find((b) => b.textContent?.includes('Manual'));
    toggle!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function type(fixture: Awaited<ReturnType<typeof render>>['fixture'], input: HTMLInputElement, value: string) {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('fetches the sales from the till unless told otherwise, and says so', async () => {
    const { fixture, theoretical, manualSales } = await render();

    expect(theoretical).toHaveBeenCalledWith(expect.any(Number), expect.any(Number));
    expect(manualSales).not.toHaveBeenCalled();
    expect(page(fixture).querySelector('.source-badge')?.textContent).toContain('Data source: Live POS');
  });

  it('takes the month\'s sales typed in, item by item, and makes the report from them', async () => {
    const { fixture, saveManualSales, info } = await render();
    await chooseManual(fixture);

    // Nothing entered for the month yet: straight to the sheet, grouped as the kitchen sorts them.
    const groups = [...page(fixture).querySelectorAll('tr.group')].map((g) => g.textContent?.trim());
    expect(groups).toEqual(['Mains', 'Prep items']);
    const inputs = [...page(fixture).querySelectorAll<HTMLInputElement>('input.qty-in')];
    expect(inputs.map((i) => i.getAttribute('aria-label'))).toEqual([
      'Classic Cheeseburger, sold in portions',
      'Staff meal, sold in portions',
      'Pizza dough, sold in kg',
    ]);

    await type(fixture, inputs[0], '10');
    // Enter goes on to the next, as Tab does.
    inputs[0].focus();
    inputs[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(document.activeElement).toBe(inputs[1]);
    await type(fixture, inputs[2], '2.5');

    const total = page(fixture).querySelector('tfoot')?.textContent ?? '';
    expect(total).toContain('2 of 3 entered');
    expect(total).toContain('10 portions');

    page(fixture).querySelector<HTMLButtonElement>('button.generate')!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(saveManualSales).toHaveBeenCalledWith(2026, 10, [
      { kind: 'Recipe', itemId: 'r-burger', quantity: 10 },
      { kind: 'PrepItem', itemId: 'p-dough', quantity: 2.5 },
    ]);
    expect(info).toHaveBeenCalled();
    const badge = page(fixture).querySelector('.source-badge');
    expect(badge?.classList).toContain('manual');
    expect(badge?.textContent).toContain('Data source: Manual entry');
    expect(page(fixture).querySelector('.source-line')?.textContent).toContain('Entered by Sara');
    const dough = [...page(fixture).querySelectorAll('tbody tr')][1];
    const cells = [...dough.querySelectorAll('td')].map((td) => td.textContent?.trim());
    expect(cells[2]).toBe('2.5 kg');
    expect(cells[3]).toBe('—');
    expect(cells[7]).toContain('Prep item');
  });

  it('will not save half a portion or less than nothing', async () => {
    const { fixture, saveManualSales } = await render();
    await chooseManual(fixture);
    const inputs = [...page(fixture).querySelectorAll<HTMLInputElement>('input.qty-in')];

    await type(fixture, inputs[0], '1.5');
    expect(inputs[0].classList).toContain('invalid');
    expect(page(fixture).querySelector('.actions .error')?.textContent).toContain('portions are whole numbers');
    expect(page(fixture).querySelector<HTMLButtonElement>('button.generate')!.disabled).toBe(true);

    await type(fixture, inputs[0], '2');
    await type(fixture, inputs[2], '-1');
    expect(page(fixture).querySelector('.actions .error')?.textContent).toContain('cannot be less than 0');
    page(fixture).querySelector<HTMLButtonElement>('button.generate')!.click();
    expect(saveManualSales).not.toHaveBeenCalled();
  });

  it('shows a month already entered as its report, with its quantities there to change', async () => {
    const saved: ManualSalesSheet = {
      ...sheet,
      enteredBy: 'Sara',
      enteredAtUtc: '2026-10-08T12:30:00Z',
      items: sheet.items.map((i) => (i.itemId === 'r-burger' ? { ...i, quantity: 10 } : i)),
    };
    const { fixture } = await render(saved);
    await chooseManual(fixture);

    expect(page(fixture).querySelector('input.qty-in')).toBeNull();
    expect(page(fixture).querySelector('.source-badge')?.textContent).toContain('Manual entry');

    const edit = [...page(fixture).querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent?.includes('Edit quantities'));
    edit!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(page(fixture).querySelector<HTMLInputElement>('input.qty-in')!.value).toBe('10');
  });

  it('never shows a dish without a recipe at 0% food cost', async () => {
    const { fixture } = await render();

    const tray = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')][1];
    const cells = [...tray.querySelectorAll('td')].map((td) => td.textContent?.trim());
    expect(cells.slice(4, 7)).toEqual(['—', '—', '—']);
    expect(cells[7]).toContain('No recipe');
  });

  it('exports a spreadsheet Excel reads in Arabic, with names that contain commas and quotes intact', async () => {
    const { fixture } = await render();
    let blob: Blob | undefined;
    vi.spyOn(URL, 'createObjectURL').mockImplementation((b) => {
      blob = b as Blob;
      return 'blob:report';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    (fixture.componentInstance as unknown as { exportCsv(): void }).exportCsv();

    const bytes = new Uint8Array(await blob!.arrayBuffer());
    expect([...bytes.slice(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
    const lines = new TextDecoder().decode(bytes.slice(3)).split('\r\n');
    expect(lines[0]).toContain('Item Code · كود الصنف');
    expect(lines[1]).toBe('6,Classic Cheeseburger,تشيز برجر,Mains,3,390.00,27.00,81.00,0.00,1,20.8,Within target · ضمن الهدف');
    expect(lines[2]).toBe('10-101,"Shawarma, ""1 kg"" tray",,Mains,1,400.00,,,0.00,0,,No recipe · لا توجد وصفة');
    expect(lines[3]).toBe('Total,,,,4,790.00,,81.00,,,10.3,');
    expect(lines[4]).toBe('Data Source: Live POS · مصدر البيانات: الكاشير');
  });
});
