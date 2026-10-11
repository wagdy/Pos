import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelect, MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import {
  CostStatus,
  CostingApi,
  MaterialCost,
  RecipeCategory,
  RecipeCostCard,
  RecipeTargetKind,
  SaveRecipe,
  statusLabels,
  unitLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { RecipeCategoryDialog, RecipeCategoryDialogData } from './recipe-category-dialog';

interface Line {
  rawMaterialId: string | null;
  quantity: number | null;
  yieldPercent: number | null;
}

// What one line works out to, as the template does it: EP$/unit = AP$/unit ÷ yield, and the
// line's cost is EP quantity × EP$, in purchase units.
interface Costed {
  material: MaterialCost | undefined;
  aps: number | null;
  eps: number | null;
  cost: number | null;
}

// A menu item a recipe can be linked to, as the link list offers it.
interface LinkOption {
  key: string;
  label: string;
  card: RecipeCostCard;
  // Already sells by another recipe.
  taken: boolean;
}

// view: the card, read-only. edit and new: the recipe written. menu: an old link to a menu item's
// card (/costing/recipes/MenuItem/6), sent on to its recipe, or to a new one for it.
export type RecipeCardMode = 'view' | 'edit' | 'new' | 'menu';

// Rounded as the server rounds (to the cent or the tenth, halves away from zero). The figure is
// first cut to 15 significant digits, so 228.94 / 4, stored as 57.23499…, rounds to 57.24.
const roundTo = (value: number, places: number) => {
  const scaled = Number((Math.abs(value) * 10 ** places).toPrecision(15));
  return (Math.sign(value) * Math.round(scaled)) / 10 ** places;
};
const round = (value: number) => roundTo(value, 2);

const linkKey = (kind: RecipeTargetKind | null, catalogItemId: number | null, variantId: number | null) =>
  kind && catalogItemId !== null ? `${kind}|${catalogItemId}|${variantId ?? ''}` : null;

// The Recipe Costing Template for one recipe. The recipe is written as the kitchen makes it: the
// edible portion (EP) of each ingredient for the whole batch, and how many portions the batch
// gives. Linked to a menu item, that item sells by it; off the menu (R&D), it is costed against
// a planned price, never sold. Figures follow every change.
@Component({
  selector: 'app-recipe-card-page',
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
    MoneyPipe,
    RouterLink,
  ],
  template: `
    <div class="toolbar no-print">
      <a mat-button routerLink="/costing/recipes"><mat-icon>arrow_back</mat-icon> All recipes</a>
      <span class="spacer"></span>
      @if (mode() === 'view' && card(); as c) {
        @if (!c.isDeleted && canEdit()) {
          <a mat-button [routerLink]="['/costing/recipes/recipe', c.recipeId, 'edit']"><mat-icon>edit</mat-icon> Edit</a>
          <button mat-button class="danger-text" (click)="confirmingRemove.set(true)" [disabled]="confirmingRemove()"><mat-icon>delete</mat-icon> Delete</button>
        }
        <button mat-button (click)="print()"><mat-icon>print</mat-icon> Print</button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <section class="sheet">
        @if (card()?.isDeleted) {
          <div class="banner no-print">
            <span>
              Deleted {{ card()!.deletedAtUtc | date: 'd MMM y, HH:mm' }}{{ card()!.deletedBy ? ' by ' + card()!.deletedBy : '' }}.
              Kept for the record; no sale takes stock by it.
            </span>
            <span class="spacer"></span>
            @if (canEdit()) {
              <button mat-flat-button (click)="restore()" [disabled]="saving()"><mat-icon>restore_from_trash</mat-icon> Restore</button>
            }
          </div>
        }
        @if (confirmingRemove()) {
          <div class="banner danger no-print">
            <span>
              Delete {{ title() }}?
              @if (linkedName(); as menuItem) {
                <strong>Sales of {{ menuItem }} stop taking stock</strong> until another recipe is linked to it.
              }
              It is kept, marked deleted, and can be restored; past reports are not changed.
            </span>
            <span class="spacer"></span>
            <button mat-button (click)="confirmingRemove.set(false)">Cancel</button>
            <button mat-flat-button class="danger-fill" (click)="removeRecipe()" [disabled]="saving()">Delete</button>
          </div>
        }

        <header class="title">
          <div>
            <h2>{{ title() }}</h2>
            @if (card()?.recipeAr && !editing()) {
              <div class="ar title-ar">{{ card()!.recipeAr }}</div>
            }
            <div class="muted subtitle">
              @if (linkedName(); as menuItem) {
                <span class="link linked">Linked to menu: {{ menuItem }}<span class="ar">مرتبطة بالمنيو</span></span>
              } @else {
                <span class="link standalone">Standalone / Off-menu<span class="ar">وصفة مستقلة</span></span>
              }
              @if (card()?.itemCode && !editing()) {
                · Item code<span class="inline-ar">كود الصنف</span>: <strong>{{ card()!.itemCode }}</strong>
              }
              @if (!editing()) {
                · <span class="category" [class.unsorted]="!card()?.recipeCategory">{{ card()?.recipeCategory ?? 'Uncategorized' }}</span>
                @if (card()?.recipeCategoryAr) {
                  <span class="inline-ar">{{ card()!.recipeCategoryAr }}</span>
                }
              }
            </div>
          </div>
          <span class="status" [class]="status()">
            {{ statusLabels[status()].en }}<span class="ar">{{ statusLabels[status()].ar }}</span>
          </span>
        </header>

        @if (editing()) {
          <div class="form no-print">
            <mat-form-field>
              <mat-label>Recipe name · اسم الوصفة</mat-label>
              <input matInput [ngModel]="name()" (ngModelChange)="name.set($event)" maxlength="200" [placeholder]="linked()?.label ?? ''" aria-label="Recipe name" />
              @if (linked()) {
                <mat-hint>Blank: it goes by the menu item's name.</mat-hint>
              }
            </mat-form-field>
            <mat-form-field>
              <mat-label>Recipe category · قسم الوصفة</mat-label>
              <mat-select #categorySelect [ngModel]="categoryId()" (ngModelChange)="categoryChosen($event, categorySelect)" aria-label="Recipe category" required>
                @for (c of categories(); track c.id) {
                  <mat-option [value]="c.id">{{ c.name }}{{ c.nameAr ? ' · ' + c.nameAr : '' }}</mat-option>
                }
                <mat-option [value]="newCategory" class="add-new">+ New category…</mat-option>
              </mat-select>
              @if (!categoryId()) {
                <mat-hint>Required: the kitchen's own sorting, not the menu's.</mat-hint>
              }
            </mat-form-field>
            <mat-form-field class="wide">
              <mat-label>Link to POS menu item · الربط بصنف المنيو</mat-label>
              <mat-select [ngModel]="link()" (ngModelChange)="link.set($event)" aria-label="Link to POS menu item">
                <mat-option [value]="null">None: off the menu (R&amp;D) · بدون ربط</mat-option>
                @for (o of linkOptions(); track o.key) {
                  <mat-option [value]="o.key" [disabled]="o.taken">{{ o.label }}{{ o.taken ? ' · has a recipe' : '' }}</mat-option>
                }
              </mat-select>
              <mat-hint>{{ linked() ? 'Sales of it take stock by this recipe.' : 'Costed only: no sale takes stock by it.' }}</mat-hint>
            </mat-form-field>
            @if (!linked()) {
              <mat-form-field>
                <mat-label>Planned price, L.E · السعر المخطط</mat-label>
                <input matInput type="number" min="0" step="any" [ngModel]="plannedPrice()" (ngModelChange)="plannedPrice.set($event)" aria-label="Planned price" />
                <mat-hint>Optional, before VAT: for its food cost %.</mat-hint>
              </mat-form-field>
            }
          </div>
        }

        <div class="figures">
          <div class="box key">
            <span class="label">Cost per recipe<span class="ar">تكلفة الوصفة</span></span>
            <strong>{{ costPerRecipe() | money }}</strong>
          </div>
          <div class="box key">
            <span class="label">Cost per portion<span class="ar">تكلفة الحصة</span></span>
            <strong>{{ costPerPortion() | money }}</strong>
            @if (shared() > 0) {
              <small class="muted">incl. {{ shared() | money }} shared</small>
            }
          </div>
          <div class="box key">
            <span class="label">Margin per portion<span class="ar">هامش الحصة</span></span>
            <strong>{{ price() === null || lines().length === 0 ? '—' : (margin() | money) }}</strong>
          </div>
          <div class="box">
            <span class="label">Portions<span class="ar">عدد الحصص</span></span>
            @if (editing()) {
              <mat-form-field subscriptSizing="dynamic" class="portions">
                <input matInput type="number" min="1" step="1" [ngModel]="portions()" (ngModelChange)="portions.set($event)" aria-label="Portions" />
              </mat-form-field>
            } @else {
              <strong>{{ portions() }}</strong>
            }
          </div>
          <div class="box">
            <span class="label">{{ linked() ? 'Menu price' : 'Planned price' }}<span class="ar">{{ linked() ? 'سعر البيع' : 'السعر المخطط' }}</span></span>
            <strong>{{ price() === null ? '—' : (price() | money) }}</strong>
            <small class="muted">before VAT</small>
          </div>
          <div class="box">
            <span class="label">Food cost % budget<span class="ar">نسبة التكلفة المستهدفة</span></span>
            <strong>{{ target() | number: '1.0-1' }}%</strong>
          </div>
          <div class="box" [class.over]="status() === 'AboveTarget'">
            <span class="label">Food cost % actual<span class="ar">نسبة التكلفة الفعلية</span></span>
            <strong>{{ foodCost() === null ? '—' : (foodCost() | number: '1.1-1') + '%' }}</strong>
          </div>
          <div class="box">
            <span class="label">Ideal selling price<span class="ar">سعر البيع المثالي</span></span>
            <strong>{{ lines().length ? (idealPrice() | money) : '—' }}</strong>
          </div>
        </div>

        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Ingredients<span class="ar">المكونات</span></th>
                <th class="num">Recipe quantity (EP)<span class="ar">كمية الوصفة (صافي)</span></th>
                <th class="num">AP$ / unit<span class="ar">تكلفة الشراء</span></th>
                <th>Unit<span class="ar">الوحدة</span></th>
                <th class="num">Yield %<span class="ar">نسبة الصافي</span></th>
                <th class="num">EP$ / unit<span class="ar">تكلفة الصافي</span></th>
                <th class="num">Recipe cost<span class="ar">تكلفة المكون</span></th>
                @if (editing()) {
                  <th class="no-print"></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (line of lines(); track $index; let i = $index) {
                @let x = costed()[i];
                <tr>
                  <td>
                    @if (editing()) {
                      <mat-form-field subscriptSizing="dynamic" class="ingredient">
                        <mat-select [ngModel]="line.rawMaterialId" (ngModelChange)="choose(i, $event)" placeholder="Choose…" [aria-label]="'Ingredient, line ' + (i + 1)">
                          @for (m of materials(); track m.id) {
                            <mat-option [value]="m.id">{{ m.code ? m.code + ' · ' : '' }}{{ m.name }}{{ m.kind === 'Prep' ? ' · prep' : '' }}</mat-option>
                          }
                        </mat-select>
                      </mat-form-field>
                    } @else {
                      {{ x.material?.name }}
                    }
                  </td>
                  <td class="num">
                    @if (editing()) {
                      <mat-form-field subscriptSizing="dynamic" class="small">
                        <input matInput type="number" min="0" step="any" [ngModel]="line.quantity" (ngModelChange)="change(i, { quantity: $event })" [attr.aria-label]="'Quantity, line ' + (i + 1)" />
                        <span matTextSuffix>&nbsp;{{ x.material ? unit(x.material) : '' }}</span>
                      </mat-form-field>
                    } @else {
                      {{ line.quantity | number: '1.0-3' }} {{ x.material ? unit(x.material) : '' }}
                    }
                  </td>
                  <td class="num">
                    @if (x.aps !== null) {
                      {{ x.aps | money }}
                    } @else if (x.material) {
                      <span class="error">No price</span>
                    }
                  </td>
                  <td>{{ x.material?.purchaseUnit ?? '' }}</td>
                  <td class="num">
                    @if (editing()) {
                      <mat-form-field subscriptSizing="dynamic" class="yield">
                        <input matInput type="number" min="1" max="100" step="any" [ngModel]="line.yieldPercent" (ngModelChange)="change(i, { yieldPercent: $event })" [attr.aria-label]="'Yield, line ' + (i + 1)" />
                        <span matTextSuffix>%</span>
                      </mat-form-field>
                    } @else {
                      {{ line.yieldPercent | number: '1.0-2' }}%
                    }
                  </td>
                  <td class="num">{{ x.eps === null ? '' : (x.eps | money) }}</td>
                  <td class="num">{{ x.cost === null ? '' : (x.cost | money) }}</td>
                  @if (editing()) {
                    <td class="no-print">
                      <button mat-icon-button (click)="remove(i)" aria-label="Remove ingredient"><mat-icon>delete</mat-icon></button>
                    </td>
                  }
                </tr>
              } @empty {
                <tr><td [attr.colspan]="editing() ? 8 : 7" class="muted">No ingredients yet.</td></tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <td>
                  @if (editing()) {
                    <button mat-button class="no-print" (click)="add()" [disabled]="materials().length === 0"><mat-icon>add</mat-icon> Add ingredient</button>
                  }
                </td>
                <td colspan="5" class="num">Total · الإجمالي</td>
                <td class="num">{{ costPerRecipe() | money }}</td>
                @if (editing()) {
                  <td class="no-print"></td>
                }
              </tr>
            </tfoot>
          </table>
        </div>

        @if (editing() && materials().length === 0) {
          <p class="note">Add the raw materials first, on the Raw materials tab, with their prices.</p>
        }
        @if (duplicate()) {
          <p class="error">An ingredient is on two lines: keep one, with the quantities added together.</p>
        }
        @if (missing().length) {
          <p class="error">No price yet for {{ missing().join(', ') }}: the cost shown is short of the truth until they have one.</p>
        }

        @if (editing()) {
          <div class="toolbar actions no-print">
            <span class="spacer"></span>
            <a mat-button [routerLink]="mode() === 'edit' ? ['/costing/recipes/recipe', recipeId()] : ['/costing/recipes']">Cancel</a>
            <button mat-flat-button (click)="save()" [disabled]="!valid() || (mode() === 'edit' && !dirty()) || saving()">
              <mat-icon>save</mat-icon> {{ mode() === 'new' ? 'Create recipe' : 'Save recipe' }}
            </button>
          </div>
          @if (problem(); as p) {
            <p class="error">{{ p }}</p>
          }
        }
      </section>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .sheet {
      display: grid;
      gap: 16px;
    }
    .title {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 16px;

      h2 {
        margin: 0;
        font: var(--mat-sys-headline-small);
      }
      .title-ar {
        font-size: 1.1em;
        text-align: start;
      }
    }
    .subtitle {
      margin-top: 4px;
    }
    .link {
      display: inline-block;
      padding: 2px 10px;
      border-radius: 999px;
      font: var(--mat-sys-label-medium);

      .ar {
        display: inline;
        margin-left: 6px;
      }
    }
    .category {
      font-weight: 500;

      &.unsorted {
        color: var(--mat-sys-error);
      }
    }
    .add-new {
      color: var(--mat-sys-primary);
      font-weight: 500;
    }
    .linked {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .standalone {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
    .form {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: 4px 12px;
    }
    .wide {
      grid-column: span 2;
    }
    .banner {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      padding: 10px 12px;
      border-radius: 12px;
      background: var(--mat-sys-surface-container-high);

      &.danger {
        background: var(--mat-sys-error-container);
        color: var(--mat-sys-on-error-container);
      }
    }
    .figures {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
      gap: 8px;
    }
    .box {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 10px 12px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      .label {
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      strong {
        font: var(--mat-sys-title-large);
      }
      &.key {
        background: var(--mat-sys-secondary-container);
        color: var(--mat-sys-on-secondary-container);
        border-color: transparent;
      }
      &.over strong {
        color: var(--mat-sys-error);
      }
    }
    .portions {
      width: 110px;
    }
    .ingredient {
      min-width: 220px;
      width: 100%;
    }
    .small {
      width: 140px;
    }
    .yield {
      width: 110px;
    }
    .danger-text {
      color: var(--mat-sys-error);
    }
    .danger-fill:not(:disabled) {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
    .spacer {
      flex: 1;
    }
    @media print {
      .box.key {
        border: 1px solid #000;
      }
    }
  `,
})
export class RecipeCardPage {
  private readonly api = inject(CostingApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);

  // From the route: its data (mode), /costing/recipes/recipe/:recipeId, an old
  // /costing/recipes/:kind/:id?variantId=, and new?targetKind=&catalogItemId=&variantId= to start linked.
  readonly mode = input<RecipeCardMode>('view');
  readonly recipeId = input<string>();
  readonly kind = input<RecipeTargetKind>();
  readonly id = input<string>();
  readonly targetKind = input<RecipeTargetKind>();
  readonly catalogItemId = input<string>();
  readonly variantId = input<string>();

  protected readonly statusLabels = statusLabels;
  protected readonly canEdit = computed(() => this.auth.can(Permissions.InventoryManage));
  protected readonly editing = computed(() => (this.mode() === 'edit' || this.mode() === 'new') && this.canEdit());

  protected readonly card = signal<RecipeCostCard | null>(null);
  // Every menu item, size and add-on, by the recipe it sells by: what can be linked.
  protected readonly menu = signal<RecipeCostCard[]>([]);
  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly confirmingRemove = signal(false);

  protected readonly name = signal('');
  // The kitchen's own categories, and the one this recipe is in.
  protected readonly categories = signal<RecipeCategory[]>([]);
  protected readonly categoryId = signal<string | null>(null);
  // The list's last option: add a category without leaving the recipe.
  protected readonly newCategory = '+new';
  protected readonly plannedPrice = signal<number | null>(null);
  protected readonly link = signal<string | null>(null);
  protected readonly lines = signal<Line[]>([]);
  protected readonly portions = signal<number | null>(1);
  // The recipe as saved, to tell a change from none.
  private readonly saved = signal('');

  protected readonly linkOptions = computed<LinkOption[]>(() =>
    this.menu().map((c) => ({
      key: linkKey(c.targetKind, c.catalogItemId, c.variantId)!,
      label: `${c.itemCode} · ${c.linkedTo ?? c.recipe}`,
      card: c,
      // A size borrowing its dish's recipe can be given its own.
      taken: c.hasRecipe && !c.usesBaseRecipe && c.recipeId !== (this.card()?.recipeId ?? null),
    })),
  );

  protected readonly linked = computed(() => this.linkOptions().find((o) => o.key === this.link()) ?? null);

  // The menu item it is linked to, by name: as written while editing, as saved otherwise.
  protected readonly linkedName = computed(() =>
    this.editing() ? (this.linked()?.card.linkedTo ?? null) : this.card()?.isStandalone === false ? this.card()!.linkedTo : null,
  );

  protected readonly title = computed(() => {
    if (!this.editing()) {
      return this.card()?.recipe ?? '';
    }
    return this.name().trim() || this.linked()?.card.linkedTo || (this.mode() === 'new' ? 'New recipe' : (this.card()?.recipe ?? ''));
  });

  // What it is costed against: the menu's price when linked, the planned price when not.
  protected readonly price = computed<number | null>(() => {
    if (!this.editing()) {
      return this.card()?.menuPrice ?? null;
    }
    const linked = this.linked();
    if (linked) {
      return linked.card.menuPrice;
    }
    const planned = this.plannedPrice();
    return planned === null || (planned as unknown) === '' ? null : Number(planned);
  });

  protected readonly target = computed(() => this.card()?.foodCostTargetPercent ?? this.menu()[0]?.foodCostTargetPercent ?? 30);

  protected readonly costed = computed<Costed[]>(() =>
    this.lines().map((line) => {
      const material = this.materials().find((m) => m.id === line.rawMaterialId);
      // From the exact average, not the rounded price shown: a 10 kg batch would be cents out.
      const aps = material?.costPerUnit == null ? null : material.costPerUnit * material.purchaseUnitSize;
      const yieldPercent = line.yieldPercent ?? 0;
      const eps = aps === null || yieldPercent <= 0 ? null : (aps * 100) / yieldPercent;
      const cost = eps === null || !material ? null : round((eps * (line.quantity ?? 0)) / material.purchaseUnitSize);
      return { material, aps, eps, cost };
    }),
  );

  protected readonly costPerRecipe = computed(() => round(this.costed().reduce((sum, l) => sum + (l.cost ?? 0), 0)));

  // Shared costs (frying oil and the like) per meal, as last month worked out and the server sent
  // it for this recipe as saved. A link changed while editing is costed with them once saved.
  protected readonly shared = computed(() => {
    const card = this.card();
    if (!card || this.lines().length === 0) {
      return 0;
    }
    return !this.editing() || this.link() === linkKey(card.targetKind, card.catalogItemId, card.variantId) ? card.sharedCostPerPortion : 0;
  });

  // Unrounded, as the template works: the figures below come from it, and only what is shown is
  // rounded, so they match the server's card to the cent.
  private readonly exactCostPerPortion = computed(() => {
    const portions = Math.max(1, Math.floor(this.portions() ?? 1));
    return this.costPerRecipe() / portions + this.shared();
  });

  protected readonly costPerPortion = computed(() => round(this.exactCostPerPortion()));

  protected readonly margin = computed(() => round((this.price() ?? 0) - this.exactCostPerPortion()));

  protected readonly foodCost = computed(() => {
    const price = this.price();
    return price !== null && price > 0 && this.lines().length > 0 ? roundTo((this.exactCostPerPortion() / price) * 100, 1) : null;
  });

  protected readonly idealPrice = computed(() => round((this.exactCostPerPortion() * 100) / this.target()));

  protected readonly missing = computed(() =>
    this.costed()
      .filter((l) => l.material && l.aps === null)
      .map((l) => l.material!.name),
  );

  protected readonly status = computed<CostStatus>(() => {
    const foodCost = this.foodCost();
    if (this.lines().length === 0) {
      return 'NoRecipe';
    }
    if (this.missing().length > 0) {
      return 'MissingPrices';
    }
    if (foodCost === null) {
      return 'NoPrice';
    }
    return foodCost > this.target() ? 'AboveTarget' : 'WithinTarget';
  });

  protected readonly dirty = computed(() => this.snapshot() !== this.saved());

  // Portions a whole number; each line an ingredient with a quantity and a yield, none twice; a
  // name when it is off the menu; a planned price not below nothing.
  protected readonly valid = computed(() => {
    const portions = this.portions();
    const lines = this.lines();
    const price = this.price();
    return (
      portions !== null &&
      Number.isInteger(portions) &&
      portions >= 1 &&
      lines.length > 0 &&
      lines.every((l) => l.rawMaterialId && (l.quantity ?? 0) > 0 && (l.yieldPercent ?? 0) > 0 && (l.yieldPercent ?? 0) <= 100) &&
      !this.duplicate() &&
      this.categoryId() !== null &&
      (this.linked() !== null || this.name().trim() !== '') &&
      (price === null || price >= 0)
    );
  });

  // The same ingredient on two lines: one line, with the quantities added together, is meant.
  protected readonly duplicate = computed(() => {
    const ids = this.lines()
      .map((l) => l.rawMaterialId)
      .filter((id) => id !== null);
    return new Set(ids).size !== ids.length;
  });

  constructor() {
    effect(() => {
      const mode = this.mode();
      const recipeId = this.recipeId();
      const legacy = { kind: this.kind(), id: this.id() };
      const start = { kind: this.targetKind(), id: this.catalogItemId(), variant: this.variantId() };
      untracked(() => void this.load(mode, recipeId, legacy, start));
    });
  }

  // A category chosen, or the last option: a new one, added over the recipe and chosen; cancelled,
  // the recipe keeps the one it had.
  protected async categoryChosen(value: string | null, select?: MatSelect): Promise<void> {
    if (value !== this.newCategory) {
      this.categoryId.set(value);
      return;
    }
    // The list shows the one it had while the new one is added; the recipe's own never changed.
    if (select) {
      select.value = this.categoryId();
    }
    const created = await firstValueFrom(
      this.dialog
        .open<RecipeCategoryDialog, RecipeCategoryDialogData, RecipeCategory>(RecipeCategoryDialog, { data: { fromRecipe: true }, width: '440px' })
        .afterClosed(),
    );
    if (!created) {
      return;
    }
    this.categories.set(await firstValueFrom(this.api.recipeCategories()));
    this.categoryId.set(created.id);
    this.notify.info(`${created.name} added, and this recipe is in it.`);
  }

  protected unit(material: MaterialCost): string {
    return unitLabels[material.unit];
  }

  protected choose(index: number, rawMaterialId: string): void {
    const material = this.materials().find((m) => m.id === rawMaterialId);
    this.change(index, { rawMaterialId, yieldPercent: material?.defaultYieldPercent ?? 100 });
  }

  protected change(index: number, patch: Partial<Line>): void {
    this.problem.set(null);
    this.lines.update((lines) => lines.map((line, i) => (i === index ? { ...line, ...patch } : line)));
  }

  protected add(): void {
    this.lines.update((lines) => [...lines, { rawMaterialId: null, quantity: null, yieldPercent: 100 }]);
  }

  protected remove(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
  }

  protected print(): void {
    window.print();
  }

  protected async save(): Promise<void> {
    if (!this.valid()) {
      return;
    }
    const linked = this.linked()?.card ?? null;
    const planned = this.plannedPrice();
    const recipe: SaveRecipe = {
      name: this.name().trim() || null,
      recipeCategoryId: this.categoryId(),
      // Kept while it is on the menu, for if it comes off it.
      plannedPrice: planned === null || (planned as unknown) === '' ? null : Number(planned),
      targetKind: linked?.targetKind ?? null,
      catalogItemId: linked?.catalogItemId ?? null,
      variantId: linked?.variantId ?? null,
      portions: this.portions() ?? 1,
      ingredients: this.lines().map((l) => ({ rawMaterialId: l.rawMaterialId!, quantity: l.quantity!, yieldPercent: l.yieldPercent })),
    };
    this.saving.set(true);
    this.problem.set(null);
    try {
      const written = await firstValueFrom(this.api.writeRecipe(this.mode() === 'edit' ? (this.recipeId() ?? null) : null, recipe));
      this.notify.info(linked ? `Recipe saved. ${linked.linkedTo} takes stock by it from the next sale.` : 'Recipe saved, off the menu.');
      this.saved.set(this.snapshot());
      await this.router.navigate(['/costing/recipes/recipe', written.id]);
    } catch (error) {
      this.problem.set(errorMessage(error, 'The recipe could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }

  protected async removeRecipe(): Promise<void> {
    const card = this.card();
    if (!card?.recipeId) {
      return;
    }
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.deleteRecipe(card.recipeId));
      this.notify.info(card.isStandalone ? `${card.recipe} deleted.` : `${card.recipe} deleted: ${card.linkedTo} no longer takes stock.`);
      await this.router.navigate(['/costing/recipes']);
    } catch (error) {
      this.notify.error(error, 'The recipe could not be deleted.');
    } finally {
      this.saving.set(false);
      this.confirmingRemove.set(false);
    }
  }

  protected async restore(): Promise<void> {
    const card = this.card();
    if (!card?.recipeId) {
      return;
    }
    this.saving.set(true);
    try {
      const restored = await firstValueFrom(this.api.restoreRecipe(card.recipeId, card.recipe));
      this.notify.info(
        card.isStandalone || restored.catalogItemId !== null
          ? `${card.recipe} restored.`
          : `${card.recipe} restored off the menu: ${card.linkedTo} has another recipe now.`,
      );
      await this.load('view', card.recipeId, {}, {});
    } catch (error) {
      this.notify.error(error, 'The recipe could not be restored.');
    } finally {
      this.saving.set(false);
    }
  }

  private snapshot(): string {
    return JSON.stringify({
      name: this.name(),
      categoryId: this.categoryId(),
      plannedPrice: this.plannedPrice(),
      link: this.link(),
      lines: this.lines(),
      portions: this.portions(),
    });
  }

  private async load(
    mode: RecipeCardMode,
    recipeId: string | undefined,
    legacy: { kind?: RecipeTargetKind; id?: string },
    start: { kind?: RecipeTargetKind; id?: string; variant?: string },
  ): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    this.confirmingRemove.set(false);
    try {
      if (mode === 'menu') {
        await this.forward(legacy.kind!, Number(legacy.id), start.variant ? Number(start.variant) : null);
        return;
      }
      const [card, materials, menu, categories] = await Promise.all([
        mode === 'new' || !recipeId ? Promise.resolve(null) : firstValueFrom(this.api.recipeCardById(recipeId)),
        firstValueFrom(this.api.materials()),
        firstValueFrom(this.api.menu()),
        firstValueFrom(this.api.recipeCategories()),
      ]);
      this.card.set(card);
      this.materials.set(materials);
      this.menu.set(menu);
      this.categories.set(categories);
      if (card) {
        this.name.set(card.recipeName ?? '');
        this.categoryId.set(card.recipeCategoryId);
        this.plannedPrice.set(card.plannedPrice);
        this.link.set(card.isStandalone ? null : linkKey(card.targetKind, card.catalogItemId, card.variantId));
        this.lines.set(card.lines.map((l) => ({ rawMaterialId: l.rawMaterialId, quantity: l.quantity, yieldPercent: l.yieldPercent })));
        this.portions.set(card.portions);
      } else {
        this.name.set('');
        this.categoryId.set(null);
        this.plannedPrice.set(null);
        this.link.set(start.kind && start.id ? linkKey(start.kind, Number(start.id), start.variant ? Number(start.variant) : null) : null);
        this.lines.set([{ rawMaterialId: null, quantity: null, yieldPercent: 100 }]);
        this.portions.set(1);
      }
      this.saved.set(this.snapshot());
    } catch (error) {
      this.error.set(errorMessage(error, 'The recipe could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  // An old link to a menu item's card: on to the recipe it sells by, or to a new one for it.
  private async forward(kind: RecipeTargetKind, catalogItemId: number, variantId: number | null): Promise<void> {
    const card = await firstValueFrom(this.api.recipeCard(kind, catalogItemId, variantId));
    if (card.recipeId) {
      await this.router.navigate(['/costing/recipes/recipe', card.recipeId], { replaceUrl: true });
    } else {
      await this.router.navigate(['/costing/recipes/new'], {
        replaceUrl: true,
        queryParams: { targetKind: kind, catalogItemId, ...(variantId === null ? {} : { variantId }) },
      });
    }
  }
}
