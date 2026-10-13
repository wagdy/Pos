import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, PrepImportResult } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { PrepImportDialog } from './prep-import-dialog';
import { PrepItemsPage } from './prep-items-page';

const result: PrepImportResult = {
  imported: [
    {
      row: 297, id: 'dough', name: 'عجين كبه', batchQuantity: 4_460, ingredients: 9, requested: false,
      notes: ['Makes 4.46 kg a batch: the weight of its ingredients. Correct it where cooking changes the weight.', 'ملح_ 0.03 كيلو: the till keeps ملح_ in pieces, left out.'],
    },
    { row: 367, id: 'kibbeh', name: 'كبه', batchQuantity: 75, ingredients: 2, requested: false, notes: ['Makes 75 g a batch: the weight of its ingredients. Correct it where cooking changes the weight.'] },
    { row: 61, id: 'mash', name: 'بطاطس_بوريه', batchQuantity: 3_470, ingredients: 7, requested: true, notes: [] },
  ],
  skipped: [{ row: 614, name: 'صوص بيتزا', reason: 'Also on row 599, which is the one imported.' }],
  materialsAdded: [{ id: 'cream', name: 'كريمه', unit: 'Millilitre' }, { id: 'dill', name: 'شبت', unit: 'Piece' }],
  categoryAdded: 'مصنعات',
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('prep items from a sheet of batch recipes', () => {
  const importPrepItems = vi.fn();
  const prepItems = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { importPrepItems, prepItems, approvalRequests: () => of({ pending: [], decided: [] }) } },
        { provide: AuthService, useValue: { can: () => true } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(PrepItemsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  function picked(file: File | null): HTMLInputElement {
    const input = document.createElement('input');
    input.type = 'file';
    Object.defineProperty(input, 'files', { value: file ? [file] : [] });
    return input;
  }

  beforeEach(() => {
    importPrepItems.mockReset().mockReturnValue(of(result));
    prepItems.mockReset().mockReturnValue(of([]));
    open.mockReset();
    notify.error.mockReset();
  });

  it('offers the import beside adding one', async () => {
    const { el } = await render();

    expect([...el.querySelectorAll('.toolbar button, .toolbar a')].map(text)).toEqual([
      'precision_manufacturing Make a batch',
      'upload_file Import from Excelاستيراد',
      'add Add prep item',
    ]);
  });

  it('imports the chosen sheet, shows what it did, and reads the list again', async () => {
    const { page } = await render();
    const file = new File(['xlsx'], 'مصنعات.xlsx');

    await page['importFile'](picked(file));

    expect(importPrepItems).toHaveBeenCalledWith(file);
    expect(open).toHaveBeenCalledWith(PrepImportDialog, expect.objectContaining({ data: result }));
    expect(prepItems).toHaveBeenCalledTimes(2);
  });

  it('says why a sheet is refused, and sends nothing when none is chosen', async () => {
    const { page } = await render();
    importPrepItems.mockReturnValue(throwError(() => new Error('That file could not be read as an Excel workbook (.xlsx).')));

    await page['importFile'](picked(new File(['?'], 'notes.xlsx')));
    expect(notify.error).toHaveBeenCalledWith(expect.anything(), 'The file could not be imported.');
    expect(open).not.toHaveBeenCalled();

    importPrepItems.mockClear();
    await page['importFile'](picked(null));
    expect(importPrepItems).not.toHaveBeenCalled();
  });
});

describe('what an import of batch recipes did', () => {
  it('counts what was made and asked for, names the materials added, and lists what to check and what was skipped', async () => {
    TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: result }] });
    const fixture = TestBed.createComponent(PrepImportDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el.querySelector('.added')!)).toBe(
      '2 prep items added. أُضيف 2 صنف مصنع. 1 prep item sent to a manager for approval: not made until then. أُرسل 1 للمدير للموافقة.',
    );
    expect(text(el)).toContain('Filed under a new category: مصنعات.');
    expect(text(el)).toContain('Raw materials added for their recipes, with no price yet: كريمه (ml), شبت (pc).');

    const [checks, skipped] = [...el.querySelectorAll('table')];
    const rows = [...checks.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text));
    expect(rows[0].slice(0, 3)).toEqual(['297', 'عجين كبه', '4.46 kg']);
    expect([...checks.querySelectorAll('tbody tr')[0].querySelectorAll('li')].map(text)).toEqual(result.imported[0].notes);
    expect(rows[1].slice(0, 3)).toEqual(['367', 'كبه', '75 g']);
    expect(rows[2][1]).toBe('بطاطس_بوريه waiting for approval');
    expect([...skipped.querySelectorAll('tbody td')].map(text)).toEqual(['614', 'صوص بيتزا', 'Also on row 599, which is the one imported.']);
  });
});
