import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi, MaterialCost, RawMaterialImportResult } from '../../core/costing/costing.api';
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

describe('changing a material', () => {
  const materials = vi.fn();
  const saveMaterial = vi.fn();
  const changeMaterialUnit = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const approvals = { refresh: vi.fn(), pendingFor: vi.fn(), requested: vi.fn() };
  const tomatoes = { id: 'tomatoes', name: 'Tomatoes', unit: 'Gram' } as unknown as MaterialCost;
  const details = { name: 'Tomatoes', code: null, categoryId: 'veg', defaultYieldPercent: 100, purchaseUnit: 'kg', purchaseUnitSize: 1000, costPerPurchaseUnit: null, reorderLevel: 1000 };
  const unitChange = { requestId: 'r1', unit: 'Piece', oneNewIsOld: 60, purchaseUnit: 'crate', purchaseUnitSize: 100, costPerPurchaseUnit: null, reorderLevel: null, reason: null };

  async function render(closedWith: unknown) {
    open.mockReturnValue({ afterClosed: () => of(closedWith) });
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { materials, saveMaterial, changeMaterialUnit } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: Approvals, useValue: approvals },
      ],
    });
    const fixture = TestBed.createComponent(MaterialsPage);
    await fixture.whenStable();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  beforeEach(() => {
    materials.mockReset().mockReturnValue(of([]));
    saveMaterial.mockReset().mockReturnValue(of({}));
    changeMaterialUnit.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
    approvals.pendingFor.mockReset();
    approvals.requested.mockReset();
  });

  it('saves its details, then changes its unit: at once for a manager', async () => {
    changeMaterialUnit.mockReturnValue(of({ outcome: 'Applied', request: { id: 'r1' } }));
    const page = await render({ material: details, unitChange });

    await page['edit'](tomatoes);

    expect(saveMaterial).toHaveBeenCalledWith('tomatoes', details);
    expect(changeMaterialUnit).toHaveBeenCalledWith('tomatoes', unitChange);
    expect(saveMaterial.mock.invocationCallOrder[0]).toBeLessThan(changeMaterialUnit.mock.invocationCallOrder[0]);
    expect(notify.info).toHaveBeenCalledWith('Tomatoes saved, counted in pieces now.');
  });

  it('says an accountant’s change went to a manager', async () => {
    const request = { id: 'r1', module: 'RawMaterial', action: 'Update' };
    changeMaterialUnit.mockReturnValue(of({ outcome: 'Requested', request }));
    const page = await render({ material: details, unitChange });

    await page['edit'](tomatoes);

    expect(approvals.requested).toHaveBeenCalledWith({ outcome: 'Requested', request });
  });

  it('keeps the details saved and says why when the unit is refused', async () => {
    changeMaterialUnit.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['Tomatoes is counted in pieces already.'] } })));
    const page = await render({ material: details, unitChange });

    await page['edit'](tomatoes);

    expect(saveMaterial).toHaveBeenCalled();
    expect(notify.error).toHaveBeenCalledWith(null, 'Tomatoes was saved, but its unit was not changed: Tomatoes is counted in pieces already.');
  });

  it('sends no unit change when the unit stayed, and nothing while a request about it waits', async () => {
    const page = await render({ material: details, unitChange: null });
    await page['edit'](tomatoes);
    expect(changeMaterialUnit).not.toHaveBeenCalled();
    expect(notify.info).toHaveBeenCalledWith('Tomatoes saved.');

    approvals.pendingFor.mockReturnValue({ action: 'Update', module: 'RawMaterial' });
    open.mockClear();
    await page['edit'](tomatoes);
    expect(open).not.toHaveBeenCalled();
    expect(notify.info).toHaveBeenCalledWith('Tomatoes: change requested, waiting for a manager. It can be changed once that is decided.');
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
