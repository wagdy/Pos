import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CostingApi, RecipeCategory } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { RecipeCategoriesPage } from './recipe-categories-page';
import { RecipeCategoryDialog } from './recipe-category-dialog';

const sauces: RecipeCategory = { id: 'sauces', name: 'Sauces', nameAr: 'صوصات', recipes: 3 };
const staff: RecipeCategory = { id: 'staff', name: 'Staff meals', nameAr: null, recipes: 0 };
const catering: RecipeCategory = { id: 'catering', name: 'Catering orders', nameAr: null, recipes: 1 };

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the recipe categories', () => {
  const recipeCategories = vi.fn();
  const deleteRecipeCategory = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CostingApi, useValue: { recipeCategories, deleteRecipeCategory } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(RecipeCategoriesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => {
      fixture.detectChanges();
      return [...el.querySelectorAll('tbody tr')];
    };
    const button = (row: Element, label: string) =>
      [...row.querySelectorAll('button')].find((b) => text(b).includes(label) || b.getAttribute('aria-label')?.startsWith(label))!;
    return { el, rows, button, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    recipeCategories.mockReset().mockReturnValue(of([catering, sauces, staff]));
    deleteRecipeCategory.mockReset().mockReturnValue(of(undefined));
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('lists them with how many recipes each holds, a count opening those recipes', async () => {
    const { el, rows } = await render();

    expect(rows().map((r) => [...r.querySelectorAll('td')].slice(0, 3).map(text))).toEqual([
      ['Catering orders', '—', '1'],
      ['Sauces', 'صوصات', '3'],
      ['Staff meals', '—', '0'],
    ]);
    expect(el.querySelectorAll('tbody a')).toHaveLength(2);
  });

  it('adds and renames in the category dialog', async () => {
    open.mockReturnValue({ afterClosed: () => of(staff) });
    const { page, rows, button } = await render();

    await page['add']();
    expect(open).toHaveBeenLastCalledWith(RecipeCategoryDialog, expect.objectContaining({ data: {} }));
    button(rows()[2], 'Rename').click();
    expect(open).toHaveBeenLastCalledWith(RecipeCategoryDialog, expect.objectContaining({ data: { category: staff } }));
    await vi.waitFor(() => expect(recipeCategories).toHaveBeenCalledTimes(3));
  });

  it('deletes one with no recipes after asking', async () => {
    const { rows, button } = await render();

    button(rows()[2], 'Delete').click();
    const ask = rows()[3];
    expect(text(ask)).toContain('Delete Staff meals? It has no recipes.');
    button(ask, 'Delete').click();

    await vi.waitFor(() => expect(deleteRecipeCategory).toHaveBeenCalledWith('staff', null));
    expect(notify.info).toHaveBeenCalledWith('Staff meals deleted.');
  });

  it('moves the recipes of one that has some to another category, then deletes it', async () => {
    const { page, rows, button } = await render();

    button(rows()[1], 'Delete').click();
    const ask = rows()[2];
    expect(text(ask)).toContain('Delete Sauces? Its 3 recipes move to');
    expect(page['others'](sauces)).toEqual([catering, staff]);
    expect(button(ask, 'Move and delete').disabled).toBe(true);

    page['moveTo'] = 'catering';
    await page['remove'](sauces);

    expect(deleteRecipeCategory).toHaveBeenCalledWith('sauces', 'catering');
    expect(notify.info).toHaveBeenCalledWith('Sauces deleted; its recipes are in Catering orders now.');
  });

  it('says another category is needed when it is the only one', async () => {
    recipeCategories.mockReturnValue(of([sauces]));
    const { rows, button } = await render();

    button(rows()[0], 'Delete').click();

    expect(text(rows()[1])).toContain('Add another category first');
  });
});
