import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import {
  CostStatus,
  CostingApi,
  MaterialCost,
  RecipeCostCard,
  RecipeTargetKind,
  statusLabels,
  unitLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

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

// Rounded as the server rounds (to the cent or the tenth, halves away from zero). The figure is
// first cut to 15 significant digits, so 228.94 / 4, stored as 57.23499…, rounds to 57.24.
const roundTo = (value: number, places: number) => {
  const scaled = Number((Math.abs(value) * 10 ** places).toPrecision(15));
  return (Math.sign(value) * Math.round(scaled)) / 10 ** places;
};
const round = (value: number) => roundTo(value, 2);

// The Recipe Costing Template for one dish, size or add-on. The recipe is written as the kitchen
// makes it: the edible portion (EP) of each ingredient for the whole batch, and how many portions
// the batch gives. Figures follow every change; saving keeps the recipe, and stock is taken by it
// from the next sale.
@Component({
  selector: 'app-recipe-card-page',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <a mat-button routerLink="/costing/recipes"><mat-icon>arrow_back</mat-icon> All recipes</a>
      <span class="spacer"></span>
      <button mat-button (click)="print()"><mat-icon>print</mat-icon> Print</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (card(); as c) {
      <section class="sheet">
        <header class="title">
          <div>
            <h2>{{ c.recipe }}</h2>
            @if (c.recipeAr) {
              <div class="ar title-ar">{{ c.recipeAr }}</div>
            }
            <div class="muted">
              Item code<span class="inline-ar">كود الصنف</span>: <strong>{{ c.itemCode }}</strong>
              @if (c.category) {
                · {{ c.category }}
              }
              @if (c.targetKind === 'AddOn') {
                · Add-on · إضافة
              }
            </div>
          </div>
          <span class="status" [class]="status()">
            {{ statusLabels[status()].en }}<span class="ar">{{ statusLabels[status()].ar }}</span>
          </span>
        </header>

        @if (usesBaseRecipe()) {
          <p class="note">
            This size has no recipe of its own, so it is sold by the dish's base recipe, shown below. Saving here gives it its own.
          </p>
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
            <strong>{{ c.menuPrice === null || lines().length === 0 ? '—' : (margin() | money) }}</strong>
          </div>
          <div class="box">
            <span class="label">Portions<span class="ar">عدد الحصص</span></span>
            @if (canEdit()) {
              <mat-form-field subscriptSizing="dynamic" class="portions">
                <input matInput type="number" min="1" step="1" [ngModel]="portions()" (ngModelChange)="portions.set($event)" aria-label="Portions" />
              </mat-form-field>
            } @else {
              <strong>{{ portions() }}</strong>
            }
          </div>
          <div class="box">
            <span class="label">Menu price<span class="ar">سعر البيع</span></span>
            <strong>{{ c.menuPrice === null ? '—' : (c.menuPrice | money) }}</strong>
            <small class="muted">before VAT</small>
          </div>
          <div class="box">
            <span class="label">Food cost % budget<span class="ar">نسبة التكلفة المستهدفة</span></span>
            <strong>{{ c.foodCostTargetPercent | number: '1.0-1' }}%</strong>
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
                @if (canEdit()) {
                  <th class="no-print"></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (line of lines(); track $index; let i = $index) {
                @let x = costed()[i];
                <tr>
                  <td>
                    @if (canEdit()) {
                      <mat-form-field subscriptSizing="dynamic" class="ingredient">
                        <mat-select [ngModel]="line.rawMaterialId" (ngModelChange)="choose(i, $event)" placeholder="Choose…" [aria-label]="'Ingredient, line ' + (i + 1)">
                          @for (m of materials(); track m.id) {
                            <mat-option [value]="m.id">{{ m.code ? m.code + ' · ' : '' }}{{ m.name }}</mat-option>
                          }
                        </mat-select>
                      </mat-form-field>
                    } @else {
                      {{ x.material?.name }}
                    }
                  </td>
                  <td class="num">
                    @if (canEdit()) {
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
                    @if (canEdit()) {
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
                  @if (canEdit()) {
                    <td class="no-print">
                      <button mat-icon-button (click)="remove(i)" aria-label="Remove ingredient"><mat-icon>delete</mat-icon></button>
                    </td>
                  }
                </tr>
              } @empty {
                <tr><td [attr.colspan]="canEdit() ? 8 : 7" class="muted">No ingredients yet.</td></tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <td>
                  @if (canEdit()) {
                    <button mat-button class="no-print" (click)="add()" [disabled]="materials().length === 0"><mat-icon>add</mat-icon> Add ingredient</button>
                  }
                </td>
                <td colspan="5" class="num">Total · الإجمالي</td>
                <td class="num">{{ costPerRecipe() | money }}</td>
                @if (canEdit()) {
                  <td class="no-print"></td>
                }
              </tr>
            </tfoot>
          </table>
        </div>

        @if (materials().length === 0) {
          <p class="note">Add the raw materials first, on the Raw materials tab, with their prices.</p>
        }
        @if (duplicate()) {
          <p class="error">An ingredient is on two lines: keep one, with the quantities added together.</p>
        }
        @if (missing().length) {
          <p class="error">No price yet for {{ missing().join(', ') }}: the cost shown is short of the truth until they have one.</p>
        }

        @if (canEdit()) {
          <div class="toolbar actions no-print">
            @if (hasOwnRecipe()) {
              @if (confirmingRemove()) {
                <span>Remove this recipe? Sales stop taking stock for it.</span>
                <button mat-button (click)="confirmingRemove.set(false)">Keep it</button>
                <button mat-flat-button class="danger" (click)="removeRecipe()" [disabled]="saving()">Remove</button>
              } @else {
                <button mat-button (click)="confirmingRemove.set(true)"><mat-icon>delete_forever</mat-icon> Remove recipe</button>
              }
            }
            <span class="spacer"></span>
            @if (dirty()) {
              <button mat-button (click)="undo()" [disabled]="saving()">Undo changes</button>
            }
            <button mat-flat-button (click)="save()" [disabled]="!valid() || !dirty() || saving()">
              <mat-icon>save</mat-icon> Save recipe
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
    .danger {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
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

  // From the route: /costing/recipes/:kind/:id?variantId=
  readonly kind = input.required<RecipeTargetKind>();
  readonly id = input.required<string>();
  readonly variantId = input<string>();

  protected readonly statusLabels = statusLabels;
  protected readonly canEdit = computed(() => this.auth.can(Permissions.InventoryManage));

  protected readonly card = signal<RecipeCostCard | null>(null);
  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly confirmingRemove = signal(false);

  protected readonly lines = signal<Line[]>([]);
  protected readonly portions = signal<number | null>(1);
  // The recipe as saved, to tell a change from none.
  private readonly saved = signal('');
  // A size with a recipe of its own; false while it borrows the dish's.
  protected readonly hasOwnRecipe = signal(false);
  protected readonly usesBaseRecipe = computed(() => !this.hasOwnRecipe() && !!this.card()?.hasRecipe && this.card()?.variantId !== null);

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

  // Shared costs (frying oil and the like) per meal, as last month worked out: dishes only, and
  // only once the dish has a recipe, as the server counts it.
  protected readonly shared = computed(() => {
    const card = this.card();
    return card && card.targetKind === 'MenuItem' && this.lines().length > 0 ? card.sharedCostPerPortion : 0;
  });

  // Unrounded, as the template works: the figures below come from it, and only what is shown is
  // rounded, so they match the server's card to the cent.
  private readonly exactCostPerPortion = computed(() => {
    const portions = Math.max(1, Math.floor(this.portions() ?? 1));
    return this.costPerRecipe() / portions + this.shared();
  });

  protected readonly costPerPortion = computed(() => round(this.exactCostPerPortion()));

  protected readonly margin = computed(() => round((this.card()?.menuPrice ?? 0) - this.exactCostPerPortion()));

  protected readonly foodCost = computed(() => {
    const price = this.card()?.menuPrice ?? null;
    return price !== null && price > 0 && this.lines().length > 0 ? roundTo((this.exactCostPerPortion() / price) * 100, 1) : null;
  });

  protected readonly idealPrice = computed(() => {
    const target = this.card()?.foodCostTargetPercent ?? 30;
    return round((this.exactCostPerPortion() * 100) / target);
  });

  protected readonly missing = computed(() =>
    this.costed()
      .filter((l) => l.material && l.aps === null)
      .map((l) => l.material!.name),
  );

  protected readonly status = computed<CostStatus>(() => {
    const card = this.card();
    const foodCost = this.foodCost();
    if (!card || this.lines().length === 0) {
      return 'NoRecipe';
    }
    if (this.missing().length > 0) {
      return 'MissingPrices';
    }
    if (foodCost === null) {
      return 'NoPrice';
    }
    return foodCost > card.foodCostTargetPercent ? 'AboveTarget' : 'WithinTarget';
  });

  protected readonly dirty = computed(() => this.snapshot() !== this.saved());

  protected readonly valid = computed(() => {
    const portions = this.portions();
    const lines = this.lines();
    return (
      portions !== null &&
      Number.isInteger(portions) &&
      portions >= 1 &&
      lines.length > 0 &&
      lines.every((l) => l.rawMaterialId && (l.quantity ?? 0) > 0 && (l.yieldPercent ?? 0) > 0 && (l.yieldPercent ?? 0) <= 100) &&
      !this.duplicate()
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
      const kind = this.kind();
      const id = Number(this.id());
      const variant = this.variantId();
      untracked(() => void this.load(kind, id, variant ? Number(variant) : null));
    });
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

  protected undo(): void {
    const { lines, portions } = JSON.parse(this.saved()) as { lines: Line[]; portions: number };
    this.lines.set(lines);
    this.portions.set(portions);
    this.problem.set(null);
  }

  protected print(): void {
    window.print();
  }

  protected async save(): Promise<void> {
    if (!this.card() || !this.valid()) {
      return;
    }
    await this.store(
      this.lines().map((l) => ({ rawMaterialId: l.rawMaterialId!, quantity: l.quantity!, yieldPercent: l.yieldPercent })),
      'Recipe saved. Sales take stock by it from now on.',
    );
  }

  protected async removeRecipe(): Promise<void> {
    this.confirmingRemove.set(false);
    await this.store([], 'Recipe removed.');
  }

  private async store(ingredients: { rawMaterialId: string; quantity: number; yieldPercent: number | null }[], done: string): Promise<void> {
    const card = this.card()!;
    this.saving.set(true);
    this.problem.set(null);
    try {
      await firstValueFrom(
        this.api.saveRecipe({
          targetKind: card.targetKind,
          catalogItemId: card.catalogItemId,
          variantId: card.variantId,
          ingredients,
          portions: this.portions() ?? 1,
        }),
      );
      this.notify.info(done);
      await this.load(card.targetKind, card.catalogItemId, card.variantId);
    } catch (error) {
      this.problem.set(errorMessage(error, 'The recipe could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }

  private snapshot(): string {
    return JSON.stringify({ lines: this.lines(), portions: this.portions() });
  }

  private async load(kind: RecipeTargetKind, id: number, variantId: number | null): Promise<void> {
    this.error.set(null);
    try {
      const [card, materials, recipe] = await Promise.all([
        firstValueFrom(this.api.recipeCard(kind, id, variantId)),
        firstValueFrom(this.api.materials()),
        firstValueFrom(this.api.recipe(kind, id, variantId)),
      ]);
      this.card.set(card);
      this.materials.set(materials);
      this.hasOwnRecipe.set(recipe !== null);
      // A size borrowing the dish's recipe starts from it; saving gives the size its own copy.
      this.lines.set(card.lines.map((l) => ({ rawMaterialId: l.rawMaterialId, quantity: l.quantity, yieldPercent: l.yieldPercent })));
      this.portions.set(card.portions);
      this.saved.set(recipe !== null || !card.hasRecipe ? this.snapshot() : '');
    } catch (error) {
      this.error.set(errorMessage(error, 'The recipe card could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
