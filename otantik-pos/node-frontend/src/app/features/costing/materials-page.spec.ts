import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi, RawMaterialImportResult } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { MaterialImportDialog } from './material-import-dialog';
import { MaterialsPage } from './materials-page';

const result: RawMaterialImportResult = {
  created: [
    { row: 2, id: 'c', name: 'Chicken breast', category: 'Poultry', unit: 'Gram', purchaseUnit: 'kg', costPerPurchaseUnit: 185 },
    { row: 3, id: 'm', name: 'Milk', category: 'Dairy', unit: 'Millilitre', purchaseUnit: 'L', costPerPurchaseUnit: 42.5 },
  ],
  skipped: [
    { row: 4, name: 'Tomatoes', reason: 'Tomatoes is already on the list.' },
    { row: 5, name: null, reason: 'No material name.' },
  ],
  categoriesAdded: ['Poultry'],
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('raw materials from Excel', () => {
  const materials = vi.fn();
  const rawMaterialsTemplate = vi.fn();
  const importRawMaterials = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { materials, rawMaterialsTemplate, importRawMaterials } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: Approvals, useValue: { refresh: vi.fn(), pendingFor: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(MaterialsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  // A file input as the browser leaves it once a file is chosen.
  function picked(file: File | null): HTMLInputElement {
    const input = document.createElement('input');
    input.type = 'file';
    Object.defineProperty(input, 'files', { value: file ? [file] : [] });
    return input;
  }

  beforeEach(() => {
    materials.mockReset().mockReturnValue(of([]));
    rawMaterialsTemplate.mockReset().mockReturnValue(of(new Blob(['xlsx'])));
    importRawMaterials.mockReset().mockReturnValue(of(result));
    open.mockReset();
    notify.error.mockReset();
  });

  it('offers the template and the import beside a new material', async () => {
    const { el } = await render();

    expect([...el.querySelectorAll('.toolbar button')].map(text)).toEqual([
      'download Excel templateنموذج إكسل',
      'upload_file Import from Excelاستيراد',
      'add New material',
    ]);
    expect(el.querySelector<HTMLInputElement>('input[type=file]')!.accept).toContain('.xlsx');
  });

  it('downloads the template under its own name', async () => {
    const { page } = await render();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    let saved: HTMLAnchorElement | undefined;
    click.mockImplementation(function (this: HTMLAnchorElement) {
      saved = this;
    });
    URL.createObjectURL = vi.fn(() => 'blob:template');
    URL.revokeObjectURL = vi.fn();

    await page['downloadTemplate']();

    expect(rawMaterialsTemplate).toHaveBeenCalled();
    expect(saved!.download).toBe('raw-materials-template.xlsx');
    click.mockRestore();
  });

  it('imports the chosen sheet, shows what it did, and reads the list again', async () => {
    const { page } = await render();
    const file = new File(['xlsx'], 'materials.xlsx');
    const input = picked(file);

    await page['importFile'](input);

    expect(importRawMaterials).toHaveBeenCalledWith(file);
    expect(open).toHaveBeenCalledWith(MaterialImportDialog, expect.objectContaining({ data: result }));
    expect(materials).toHaveBeenCalledTimes(2);
    expect(page['busy']()).toBe(false);
  });

  it('says why a file is refused, and sends nothing when none is chosen', async () => {
    const { page } = await render();
    importRawMaterials.mockReturnValue(throwError(() => new Error('That file could not be read as an Excel workbook (.xlsx).')));

    await page['importFile'](picked(new File(['?'], 'notes.xlsx')));
    expect(notify.error).toHaveBeenCalledWith(expect.anything(), 'The file could not be imported.');
    expect(open).not.toHaveBeenCalled();

    importRawMaterials.mockClear();
    await page['importFile'](picked(null));
    expect(importRawMaterials).not.toHaveBeenCalled();
  });
});

describe('what an import did', () => {
  it('counts what was added and lists each row skipped and why', async () => {
    TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: result }] });
    const fixture = TestBed.createComponent(MaterialImportDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el.querySelector('.added')!)).toBe('2 materials added, as stock items. أُضيفت 2 خامة.');
    expect(text(el)).toContain('New categories: Poultry.');
    expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text))).toEqual([
      ['4', 'Tomatoes', 'Tomatoes is already on the list.'],
      ['5', '—', 'No material name.'],
    ]);
  });
});
