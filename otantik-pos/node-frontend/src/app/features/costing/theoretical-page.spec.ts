import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { CostingApi, TheoreticalCostReport } from '../../core/costing/costing.api';
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
};

describe('the monthly theoretical cost report', () => {
  async function render() {
    const theoretical = vi.fn().mockReturnValue(of(report));
    TestBed.configureTestingModule({ providers: [{ provide: CostingApi, useValue: { theoretical } }] });
    const fixture = TestBed.createComponent(TheoreticalPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, theoretical };
  }

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
  });
});
