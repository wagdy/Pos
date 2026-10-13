import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostStatus, CostingApi, ApprovalRequest, RecipeCategory, RecipeCostCard, statusLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from './approvals';

// What the table shows: the recipes (all, on the menu, off it), the menu items still without
// one, or the deleted ones.
export type RecipesView = 'active' | 'linked' | 'standalone' | 'unrecipied' | 'deleted';

// The columns the table sorts by.
export type RecipeSort = 'recipe' | 'category' | 'cost' | 'foodCost';

// The recipes: each with what it is linked to on the menu (or that it is off the menu), what a
// portion costs against its price, and View, Edit and Delete. Deleting marks it deleted: it stays
// for the record and can be restored. A menu item with no recipe yet is a click from one.
@Component({
  selector: 'app-recipes-page',
  imports: [
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MatTooltipModule,
    MoneyPipe,
    RouterLink,
  ],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-icon matPrefix>search</mat-icon>
        <mat-label>Find · بحث</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Show · عرض</mat-label>
        <mat-select [ngModel]="view()" (ngModelChange)="view.set($event)" aria-label="Show">
          <mat-option value="active">All recipes · كل الوصفات</mat-option>
          <mat-option value="linked">Linked to the menu · مرتبطة بالمنيو</mat-option>
          <mat-option value="standalone">Standalone / off-menu · مستقلة</mat-option>
          <mat-option value="unrecipied">Menu items without a recipe · أصناف بلا وصفة</mat-option>
          <mat-option value="deleted">Deleted · المحذوفة</mat-option>
        </mat-select>
      </mat-form-field>
      @if (view() !== 'unrecipied') {
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Category · القسم</mat-label>
          <mat-select [ngModel]="category()" (ngModelChange)="category.set($event)" aria-label="Category">
            <mat-option [value]="null">All categories · كل الأقسام</mat-option>
            @for (c of categories(); track c.id) {
              <mat-option [value]="c.id">{{ c.name }}{{ c.nameAr ? ' · ' + c.nameAr : '' }}</mat-option>
            }
            <mat-option [value]="uncategorized">Uncategorized · بدون قسم</mat-option>
          </mat-select>
        </mat-form-field>
      }
      @if (view() !== 'unrecipied' && view() !== 'deleted') {
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Cost · التكلفة</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event)" aria-label="Cost status">
            <mat-option [value]="null">Any</mat-option>
            @for (s of statuses; track s) {
              <mat-option [value]="s">{{ statusLabels[s].en }} · {{ statusLabels[s].ar }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      <span class="spacer"></span>
      <a mat-flat-button class="create" routerLink="new"><mat-icon>add</mat-icon> Create New Recipe</a>
    </div>
    @if (!loading() && !error()) {
      <p class="muted counts">
        {{ counts().recipes }} {{ counts().recipes === 1 ? 'recipe' : 'recipes' }}: {{ counts().linked }} linked to the menu,
        {{ counts().standalone }} off it.
        @if (counts().unrecipied) {
          <a href="" (click)="$event.preventDefault(); view.set('unrecipied')">{{ counts().unrecipied }} menu {{ counts().unrecipied === 1 ? 'item has' : 'items have' }} no recipe.</a>
        }
      </p>
    }
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (view() === 'unrecipied') {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Item code<span class="ar">كود الصنف</span></th>
              <th>Menu item<span class="ar">صنف المنيو</span></th>
              <th>Category<span class="ar">الفئة</span></th>
              <th class="num">Menu price<span class="ar">سعر البيع</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of unrecipied(); track c.itemCode) {
              <tr>
                <td class="code">{{ c.itemCode }}</td>
                <td>{{ c.linkedTo ?? c.recipe }}</td>
                <td>{{ c.targetKind === 'AddOn' ? 'Add-on' : (c.category ?? '') }}</td>
                <td class="num">{{ c.menuPrice === null ? '—' : (c.menuPrice | money) }}</td>
                <td class="num">
                  <button mat-stroked-button (click)="createFor(c)"><mat-icon>add</mat-icon> Create recipe</button>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="muted">Every menu item has a recipe.</td></tr>
            }
          </tbody>
        </table>
      </div>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th [attr.aria-sort]="ariaSort('recipe')">
                <button class="sort" (click)="sortBy('recipe')">Recipe {{ arrow('recipe') }}</button><span class="ar">الوصفة</span>
              </th>
              <th [attr.aria-sort]="ariaSort('category')">
                <button class="sort" (click)="sortBy('category')">Category {{ arrow('category') }}</button><span class="ar">القسم</span>
              </th>
              <th>Link<span class="ar">الارتباط</span></th>
              <th class="num">Portions<span class="ar">الحصص</span></th>
              <th class="num">Price<span class="ar">السعر</span></th>
              <th class="num" [attr.aria-sort]="ariaSort('cost')">
                <button class="sort" (click)="sortBy('cost')">Cost per portion {{ arrow('cost') }}</button><span class="ar">تكلفة الحصة</span>
              </th>
              <th class="num" [attr.aria-sort]="ariaSort('foodCost')">
                <button class="sort" (click)="sortBy('foodCost')">Food cost % {{ arrow('foodCost') }}</button><span class="ar">نسبة التكلفة</span>
              </th>
              <th>Status<span class="ar">الحالة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of shown(); track c.recipeId) {
              <tr class="clickable" [class.deleted]="c.isDeleted" [class.pending-deletion]="!!pendingOf(c)" (click)="viewRecipe(c)">
                <td>{{ c.recipe }}</td>
                <td>
                  @if (c.recipeCategory) {
                    {{ c.recipeCategory }}
                    @if (c.recipeCategoryAr) {
                      <span class="ar">{{ c.recipeCategoryAr }}</span>
                    }
                  } @else {
                    <span class="unsorted">Uncategorized</span>
                  }
                </td>
                <td>
                  @if (c.isStandalone) {
                    <span class="link standalone">Standalone / Off-menu<span class="ar">مستقلة</span></span>
                  } @else {
                    <span class="link linked">Linked to menu: {{ c.linkedTo }}<span class="ar">مرتبطة بالمنيو</span></span>
                  }
                </td>
                <td class="num">{{ c.portions }}</td>
                <td class="num">
                  {{ c.menuPrice === null ? '—' : (c.menuPrice | money) }}
                  @if (c.isStandalone && c.menuPrice !== null) {
                    <div class="muted small">planned</div>
                  }
                </td>
                <td class="num">{{ c.costPerPortion | money }}</td>
                <td class="num">{{ c.foodCostPercentActual === null ? '—' : (c.foodCostPercentActual | number: '1.1-1') + '%' }}</td>
                <td>
                  @if (c.isDeleted) {
                    <span class="muted">Deleted {{ c.deletedAtUtc | date: 'd MMM y' }}{{ c.deletedBy ? ' by ' + c.deletedBy : '' }}</span>
                  } @else if (pendingOf(c); as p) {
                    <span class="pending-badge">{{ pendingLabel(p).en }}<span class="ar">{{ pendingLabel(p).ar }}</span></span>
                    <div class="muted small">by {{ p.requestedBy }}, {{ p.requestedAtUtc | date: 'd MMM' }}</div>
                  } @else {
                    <span class="status" [class]="c.status">
                      {{ statusLabels[c.status].en }}<span class="ar">{{ statusLabels[c.status].ar }}</span>
                    </span>
                  }
                </td>
                <td class="num actions" (click)="$event.stopPropagation()">
                  <button mat-icon-button (click)="viewRecipe(c)" matTooltip="View" [attr.aria-label]="'View ' + c.recipe"><mat-icon>visibility</mat-icon></button>
                  @if (c.isDeleted) {
                    <button mat-button (click)="restore(c)" [disabled]="saving()"><mat-icon>restore_from_trash</mat-icon> Restore</button>
                  } @else if (pendingOf(c)) {
                    <mat-icon class="locked" matTooltip="Waiting for a manager's approval: locked until decided" aria-label="Locked: deletion requested">lock_clock</mat-icon>
                  } @else {
                    <button mat-icon-button (click)="edit(c)" matTooltip="Edit" [attr.aria-label]="'Edit ' + c.recipe"><mat-icon>edit</mat-icon></button>
                    <button mat-icon-button class="danger" (click)="remove(c)" [disabled]="saving()"
                      matTooltip="Delete" [attr.aria-label]="'Delete ' + c.recipe">
                      <mat-icon>delete</mat-icon>
                    </button>
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="9" class="muted">
                  @if (view() === 'deleted') {
                    No deleted recipes.
                  } @else if (recipes().length === 0) {
                    No recipes yet. Create one, on the menu or off it.
                  } @else {
                    Nothing matches.
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .counts {
      margin: 0 0 8px;
    }
    .sort {
      padding: 0;
      border: 0;
      background: none;
      font: inherit;
      color: inherit;
      cursor: pointer;
      white-space: nowrap;
    }
    .unsorted {
      color: var(--mat-sys-error);
    }
    .create {
      white-space: nowrap;
    }
    .small {
      font-size: 0.85em;
    }
    .link {
      display: inline-block;
      padding: 2px 10px;
      border-radius: 12px;
      font: var(--mat-sys-label-medium);

      .ar {
        display: inline;
        margin-left: 6px;
      }
    }
    .linked {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .standalone {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
    tr.deleted td {
      color: var(--mat-sys-on-surface-variant);
    }
    .actions {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .spacer {
      flex: 1;
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
export class RecipesPage {
  protected readonly pendingLabel = pendingLabel;
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly approvals = inject(Approvals);

  protected readonly statusLabels = statusLabels;
  protected readonly statuses = Object.keys(statusLabels).filter((s) => s !== 'NoRecipe') as CostStatus[];

  // Every recipe, the deleted ones too; and every menu item by the recipe it sells by.
  protected readonly recipes = signal<RecipeCostCard[]>([]);
  protected readonly menu = signal<RecipeCostCard[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly view = signal<RecipesView>('active');
  protected readonly status = signal<CostStatus | null>(null);
  // The kitchen's own categories; the one shown (null: all), or none for the unsorted ones.
  protected readonly categories = signal<RecipeCategory[]>([]);
  protected readonly category = signal<string | null>(null);
  protected readonly uncategorized = 'none';
  protected readonly sort = signal<{ by: RecipeSort; direction: 1 | -1 }>({ by: 'recipe', direction: 1 });

  // From the query: a category to show, from the categories' counts.
  readonly categoryParam = input<string | undefined>(undefined, { alias: 'category' });

  protected readonly shown = computed(() => {
    const needle = this.search().trim().toLowerCase();
    const view = this.view();
    const status = this.status();
    const category = this.category();
    const { by, direction } = this.sort();
    return this.recipes()
      .filter(
        (c) =>
          (view === 'deleted' ? c.isDeleted : !c.isDeleted) &&
          (view !== 'linked' || !c.isStandalone) &&
          (view !== 'standalone' || c.isStandalone) &&
          (view === 'deleted' || status === null || c.status === status) &&
          (category === null || (category === this.uncategorized ? c.recipeCategoryId === null : c.recipeCategoryId === category)) &&
          (!needle || [c.recipe, c.recipeAr, c.recipeCategory, c.recipeCategoryAr, c.linkedTo].some((v) => v?.toLowerCase().includes(needle))),
      )
      .sort((a, b) => direction * RecipesPage.compare(a, b, by) || a.recipe.localeCompare(b.recipe));
  });

  // Menu items, sizes and add-ons with no recipe to sell by. A size that borrows its dish's has one.
  protected readonly unrecipied = computed(() => {
    const needle = this.search().trim().toLowerCase();
    return this.menu().filter(
      (c) => !c.hasRecipe && (!needle || [c.itemCode, c.linkedTo, c.recipe, c.category].some((v) => v?.toLowerCase().includes(needle))),
    );
  });

  protected readonly counts = computed(() => {
    const active = this.recipes().filter((c) => !c.isDeleted);
    return {
      recipes: active.length,
      linked: active.filter((c) => !c.isStandalone).length,
      standalone: active.filter((c) => c.isStandalone).length,
      unrecipied: this.menu().filter((c) => !c.hasRecipe).length,
    };
  });

  constructor() {
    effect(() => {
      const category = this.categoryParam();
      untracked(() => this.category.set(category ?? null));
    });
    void this.load();
  }

  // By the column clicked; again, the other way.
  protected sortBy(by: RecipeSort): void {
    this.sort.update((s) => ({ by, direction: s.by === by ? (-s.direction as 1 | -1) : 1 }));
  }

  protected arrow(by: RecipeSort): string {
    const sort = this.sort();
    return sort.by !== by ? '' : sort.direction === 1 ? '↑' : '↓';
  }

  protected ariaSort(by: RecipeSort): 'ascending' | 'descending' | 'none' {
    const sort = this.sort();
    return sort.by !== by ? 'none' : sort.direction === 1 ? 'ascending' : 'descending';
  }

  // Uncategorized last, and a recipe with no food cost % after those with one, whichever way.
  private static compare(a: RecipeCostCard, b: RecipeCostCard, by: RecipeSort): number {
    switch (by) {
      case 'category':
        return a.recipeCategory === b.recipeCategory ? 0 : a.recipeCategory === null ? 1 : b.recipeCategory === null ? -1 : a.recipeCategory.localeCompare(b.recipeCategory);
      case 'cost':
        return a.costPerPortion - b.costPerPortion;
      case 'foodCost':
        return (a.foodCostPercentActual ?? Number.MAX_VALUE) - (b.foodCostPercentActual ?? Number.MAX_VALUE);
      default:
        return a.recipe.localeCompare(b.recipe);
    }
  }

  protected viewRecipe(card: RecipeCostCard): void {
    void this.router.navigate(['/costing/recipes/recipe', card.recipeId]);
  }

  protected edit(card: RecipeCostCard): void {
    void this.router.navigate(['/costing/recipes/recipe', card.recipeId, 'edit']);
  }

  // A recipe for this menu item, linked to it from the start.
  protected createFor(card: RecipeCostCard): void {
    void this.router.navigate(['/costing/recipes/new'], {
      queryParams: { targetKind: card.targetKind, catalogItemId: card.catalogItemId, ...(card.variantId === null ? {} : { variantId: card.variantId }) },
    });
  }

  // Deleted for a manager (it can be restored from Deleted); asked of one for anyone else.
  protected async remove(card: RecipeCostCard): Promise<void> {
    this.saving.set(true);
    try {
      const detail = card.isStandalone ? undefined : `Sales of ${card.linkedTo} stop taking stock until another recipe is linked to it.`;
      if ((await this.approvals.delete('Recipe', card.recipeId!, card.recipe, detail)) === 'Applied') {
        await this.load();
      }
    } finally {
      this.saving.set(false);
    }
  }

  protected pendingOf(card: RecipeCostCard): ApprovalRequest | undefined {
    return card.recipeId ? this.approvals.pendingFor(card.recipeId) : undefined;
  }

  // Back, linked again if its menu item has no other recipe since; off the menu if it has.
  protected async restore(card: RecipeCostCard): Promise<void> {
    this.saving.set(true);
    try {
      const restored = await firstValueFrom(this.api.restoreRecipe(card.recipeId!, card.recipe));
      this.notify.info(
        card.isStandalone || restored.catalogItemId !== null
          ? `${card.recipe} restored.`
          : `${card.recipe} restored off the menu: ${card.linkedTo} has another recipe now.`,
      );
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The recipe could not be restored.');
    } finally {
      this.saving.set(false);
    }
  }

  private async load(): Promise<void> {
    void this.approvals.refresh();
    try {
      const [recipes, menu, categories] = await Promise.all([
        firstValueFrom(this.api.recipes(true)),
        firstValueFrom(this.api.menu()),
        firstValueFrom(this.api.recipeCategories()),
      ]);
      this.categories.set(categories);
      this.recipes.set(recipes);
      this.menu.set(menu);
    } catch (error) {
      this.error.set(errorMessage(error, 'The recipes could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
