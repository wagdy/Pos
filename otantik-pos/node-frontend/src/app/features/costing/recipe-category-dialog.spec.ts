import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { CostingApi } from '../../core/costing/costing.api';
import { RecipeCategoryDialog, RecipeCategoryDialogData } from './recipe-category-dialog';

describe('the recipe category dialog', () => {
  const saveRecipeCategory = vi.fn();
  const close = vi.fn();

  async function render(data: RecipeCategoryDialogData) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { saveRecipeCategory } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    });
    const fixture = TestBed.createComponent(RecipeCategoryDialog);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    saveRecipeCategory.mockReset().mockImplementation((id: string, body: { name: string; nameAr: string | null }) => of({ id, ...body, recipes: 0, prepItems: 0 }));
    close.mockReset();
  });

  it('adds a category under a new id and closes with it', async () => {
    const { page } = await render({ fromRecipe: true });
    page['form'].patchValue({ name: ' Staff meals ', nameAr: '  ' });

    await page['save']();

    const [id, body] = saveRecipeCategory.mock.lastCall!;
    expect(id).toMatch(/^[0-9a-f-]{36}$/);
    expect(body).toEqual({ name: 'Staff meals', nameAr: null });
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ id, name: 'Staff meals' }));
  });

  it('renames one under its own id', async () => {
    const { page } = await render({ category: { id: 'sauces', name: 'Sauces', nameAr: 'صوصات', recipes: 2, prepItems: 0 } });
    page['form'].patchValue({ name: 'Sauce bar' });

    await page['save']();

    expect(saveRecipeCategory).toHaveBeenCalledWith('sauces', { name: 'Sauce bar', nameAr: 'صوصات' });
  });

  it('stays open with the refusal shown when the name is taken', async () => {
    saveRecipeCategory.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 422, error: { errors: ['There is already a category called Sauces.'] } })),
    );
    const { fixture, page } = await render({});
    page['form'].patchValue({ name: 'sauces' });

    await page['save']();
    fixture.detectChanges();

    expect(close).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector('.error')!.textContent).toContain('There is already a category called Sauces.');
  });

  it('will not save without a name', async () => {
    const { page } = await render({});

    expect(page['form'].invalid).toBe(true);
    await page['save']();
    expect(saveRecipeCategory).not.toHaveBeenCalled();
  });
});
