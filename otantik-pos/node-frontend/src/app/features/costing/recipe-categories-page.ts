import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, RecipeCategory } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { RecipeCategoryDialog, RecipeCategoryDialogData } from './recipe-category-dialog';

// The kitchen's own recipe categories (أقسام الوصفات), apart from the menu's: added, renamed,
// deleted. One holding recipes is deleted only after choosing where they move, so no recipe is
// left without one. A count opens the recipes in it.
@Component({
  selector: 'app-recipe-categories-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, RouterLink],
  template: `
    <p class="note">
      How the kitchen sorts its recipes (sauces, staff meals, catering orders), whatever the menu's own categories. Every recipe written
      on the recipe screen is in one.
      <span class="inline-ar">تصنيف الوصفات الداخلي، مستقل عن أقسام المنيو.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        <span class="spacer"></span>
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add category</button>
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Category<span class="ar">القسم</span></th>
              <th>Arabic name<span class="ar">الاسم بالعربية</span></th>
              <th class="num">Recipes<span class="ar">الوصفات</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of categories(); track c.id) {
              <tr>
                <td>{{ c.name }}</td>
                <td dir="rtl" class="ar-cell">{{ c.nameAr ?? '—' }}</td>
                <td class="num">
                  @if (c.recipes) {
                    <a [routerLink]="['/costing/recipes']" [queryParams]="{ category: c.id }">{{ c.recipes }}</a>
                  } @else {
                    0
                  }
                </td>
                <td class="num actions">
                  <button mat-icon-button (click)="edit(c)" [attr.aria-label]="'Rename ' + c.name"><mat-icon>edit</mat-icon></button>
                  <button mat-icon-button class="danger" (click)="ask(c)" [disabled]="confirming() === c.id" [attr.aria-label]="'Delete ' + c.name">
                    <mat-icon>delete</mat-icon>
                  </button>
                </td>
              </tr>
              @if (confirming() === c.id) {
                <tr class="confirm-row">
                  <td colspan="4">
                    <div class="confirm">
                      @if (c.recipes) {
                        <span>Delete {{ c.name }}? Its {{ c.recipes }} {{ c.recipes === 1 ? 'recipe moves' : 'recipes move' }} to</span>
                        <mat-form-field subscriptSizing="dynamic" class="move">
                          <mat-select [(ngModel)]="moveTo" placeholder="Choose a category" aria-label="Move its recipes to">
                            @for (o of others(c); track o.id) {
                              <mat-option [value]="o.id">{{ o.name }}</mat-option>
                            }
                          </mat-select>
                        </mat-form-field>
                      } @else {
                        <span>Delete {{ c.name }}? It has no recipes.</span>
                      }
                      <span class="spacer"></span>
                      <button mat-button (click)="confirming.set(null)" [disabled]="saving()">Cancel</button>
                      <button mat-flat-button class="danger-fill" (click)="remove(c)" [disabled]="saving() || (c.recipes > 0 && !moveTo)">
                        {{ c.recipes ? 'Move and delete' : 'Delete' }}
                      </button>
                    </div>
                    @if (c.recipes && others(c).length === 0) {
                      <p class="small">Add another category first: its recipes need somewhere to go.</p>
                    }
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="4" class="muted">No categories yet. Add the first, such as Sauces or Staff meals.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .ar-cell {
      text-align: start;
    }
    .actions {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .danger-fill:not(:disabled) {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
    tr.confirm-row td {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .confirm {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .move {
      min-width: 200px;
    }
    .spacer {
      flex: 1;
    }
    .small {
      margin: 6px 0 0;
      font-size: 0.85em;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class RecipeCategoriesPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly categories = signal<RecipeCategory[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  // The category whose delete is being asked about, and where its recipes go.
  protected readonly confirming = signal<string | null>(null);
  protected moveTo: string | null = null;

  constructor() {
    void this.load();
  }

  protected others(category: RecipeCategory): RecipeCategory[] {
    return this.categories().filter((c) => c.id !== category.id);
  }

  protected async add(): Promise<void> {
    if (await this.open({})) {
      await this.load();
    }
  }

  protected async edit(category: RecipeCategory): Promise<void> {
    if (await this.open({ category })) {
      await this.load();
    }
  }

  protected ask(category: RecipeCategory): void {
    this.moveTo = null;
    this.confirming.set(category.id);
  }

  protected async remove(category: RecipeCategory): Promise<void> {
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.deleteRecipeCategory(category.id, category.recipes ? this.moveTo : null));
      const to = this.categories().find((c) => c.id === this.moveTo)?.name;
      this.notify.info(category.recipes && to ? `${category.name} deleted; its recipes are in ${to} now.` : `${category.name} deleted.`);
      this.confirming.set(null);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The category could not be deleted.');
    } finally {
      this.saving.set(false);
    }
  }

  private async open(data: RecipeCategoryDialogData): Promise<RecipeCategory | undefined> {
    return firstValueFrom(
      this.dialog.open<RecipeCategoryDialog, RecipeCategoryDialogData, RecipeCategory>(RecipeCategoryDialog, { data, width: '440px' }).afterClosed(),
    );
  }

  private async load(): Promise<void> {
    try {
      this.categories.set(await firstValueFrom(this.api.recipeCategories()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The categories could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
