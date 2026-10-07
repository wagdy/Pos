import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostStatus, CostingApi, RecipeCostCard, statusLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

// Every dish, size and add-on with what a portion costs against its price. A tap opens its
// recipe card, where the recipe is written and costed.
@Component({
  selector: 'app-recipes-page',
  imports: [DecimalPipe, FormsModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Find · بحث</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Status · الحالة</mat-label>
        <mat-select [ngModel]="status()" (ngModelChange)="status.set($event)">
          <mat-option [value]="null">All</mat-option>
          @for (s of statuses; track s) {
            <mat-option [value]="s">{{ statusLabels[s].en }} · {{ statusLabels[s].ar }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <span class="spacer"></span>
      @if (counts(); as c) {
        <span class="muted">{{ c.costed }} of {{ c.all }} costed · {{ c.above }} above target</span>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Item code<span class="ar">كود الصنف</span></th>
              <th>Menu item<span class="ar">صنف المنيو</span></th>
              <th>Category<span class="ar">الفئة</span></th>
              <th class="num">Menu price<span class="ar">سعر البيع</span></th>
              <th class="num">Cost per portion<span class="ar">تكلفة الحصة</span></th>
              <th class="num">Food cost %<span class="ar">نسبة التكلفة</span></th>
              <th class="num">Margin<span class="ar">الهامش</span></th>
              <th>Status<span class="ar">الحالة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of shown(); track c.itemCode) {
              <tr class="clickable" (click)="open(c)">
                <td class="code">{{ c.itemCode }}</td>
                <td>
                  {{ c.recipe }}
                  @if (c.recipeAr) {
                    <span class="ar">{{ c.recipeAr }}</span>
                  }
                </td>
                <td>{{ c.targetKind === 'AddOn' ? 'Add-on' : (c.category ?? '') }}</td>
                <td class="num">{{ c.menuPrice === null ? '—' : (c.menuPrice | money) }}</td>
                <td class="num">{{ c.hasRecipe ? (c.costPerPortion | money) : '—' }}</td>
                <td class="num">{{ c.hasRecipe && c.foodCostPercentActual !== null ? (c.foodCostPercentActual | number: '1.1-1') + '%' : '—' }}</td>
                <td class="num">{{ c.hasRecipe && c.marginPerPortion !== null ? (c.marginPerPortion | money) : '—' }}</td>
                <td>
                  <span class="status" [class]="c.status">
                    {{ statusLabels[c.status].en }}<span class="ar">{{ statusLabels[c.status].ar }}</span>
                  </span>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="8" class="muted">Nothing matches.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
})
export class RecipesPage {
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);

  protected readonly statusLabels = statusLabels;
  protected readonly statuses = Object.keys(statusLabels) as CostStatus[];

  protected readonly cards = signal<RecipeCostCard[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly status = signal<CostStatus | null>(null);

  protected readonly shown = computed(() => {
    const needle = this.search().trim().toLowerCase();
    const status = this.status();
    return this.cards().filter(
      (c) =>
        (status === null || c.status === status) &&
        (!needle || [c.itemCode, c.recipe, c.recipeAr, c.category].some((v) => v?.toLowerCase().includes(needle))),
    );
  });

  protected readonly counts = computed(() => {
    const cards = this.cards();
    return {
      all: cards.length,
      costed: cards.filter((c) => c.hasRecipe).length,
      above: cards.filter((c) => c.status === 'AboveTarget').length,
    };
  });

  constructor() {
    void this.load();
  }

  protected open(card: RecipeCostCard): void {
    void this.router.navigate(['/costing/recipes', card.targetKind, card.catalogItemId], {
      queryParams: card.variantId === null ? {} : { variantId: card.variantId },
    });
  }

  private async load(): Promise<void> {
    try {
      this.cards.set(await firstValueFrom(this.api.menu()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The menu costs could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
