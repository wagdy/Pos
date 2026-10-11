import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PrepItem } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

// The prep items: made in the kitchen from their own recipes, kept in stock, used by dishes.
// Each with what it costs by its recipe at today's prices, what the batches on the shelf cost,
// and how much is on the shelf. A row opens its profile.
@Component({
  selector: 'app-prep-items-page',
  imports: [DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    <p class="note">
      Made here from other materials (dough, house sauces) and kept in stock like them. A dish's recipe uses the prep item, so a sale
      takes dough, not flour; a production order turns the flour into dough.
      <span class="inline-ar">تُصنع في المطبخ وتُخزن، وتستهلكها الأطباق.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        <mat-form-field subscriptSizing="dynamic">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search · بحث</mat-label>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
        </mat-form-field>
        <span class="spacer"></span>
        <a mat-button routerLink="../production"><mat-icon>precision_manufacturing</mat-icon> Make a batch</a>
        <a mat-flat-button routerLink="new"><mat-icon>add</mat-icon> Add prep item</a>
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Prep item<span class="ar">الصنف</span></th>
              <th class="num">One batch<span class="ar">الدفعة</span></th>
              <th class="num">By its recipe<span class="ar">التكلفة المعيارية</span></th>
              <th class="num">In stock at<span class="ar">متوسط التكلفة</span></th>
              <th class="num">On hand<span class="ar">الرصيد</span></th>
              <th class="num">Stock value<span class="ar">قيمة المخزون</span></th>
              <th>Last made<span class="ar">آخر تصنيع</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of shown(); track p.id) {
              <tr class="clickable" (click)="open(p)">
                <td>
                  @if (p.code) {
                    <span class="muted">{{ p.code }} · </span>
                  }
                  {{ p.name }}
                  @if (p.ingredients.length === 0) {
                    <div class="warn small">No recipe yet</div>
                  } @else if (p.missingPrices.length) {
                    <div class="warn small">No cost yet: {{ p.missingPrices.join(', ') }}</div>
                  }
                </td>
                <td class="num">{{ p.batchQuantity / p.displayUnitSize | number: '1.0-3' }} {{ p.displayUnit }}</td>
                <td class="num">{{ p.standardCostPerDisplayUnit === null ? '—' : (p.standardCostPerDisplayUnit | money) + ' / ' + p.displayUnit }}</td>
                <td class="num">{{ p.averageCostPerDisplayUnit === null ? '—' : (p.averageCostPerDisplayUnit | money) + ' / ' + p.displayUnit }}</td>
                <td class="num" [class.warn]="p.quantityOnHand < 0">{{ p.quantityOnHand / p.displayUnitSize | number: '1.0-3' }} {{ p.displayUnit }}</td>
                <td class="num">{{ p.stockValue === null ? '—' : (p.stockValue | money) }}</td>
                <td class="nowrap">{{ p.lastProducedAtUtc ? (p.lastProducedAtUtc | date: 'd MMM, HH:mm') : 'Never' }}</td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="muted">
                  {{ items().length ? 'No prep item matches.' : 'No prep items yet. Add one, such as pizza dough, with its recipe for a batch.' }}
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
    .small {
      font-size: 0.85em;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class PrepItemsPage {
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);

  protected readonly items = signal<PrepItem[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');

  protected readonly shown = computed(() => {
    const text = this.search().trim().toLowerCase();
    return this.items().filter((p) => !text || p.name.toLowerCase().includes(text) || (p.code ?? '').toLowerCase().includes(text));
  });

  constructor() {
    void this.load();
  }

  protected open(item: PrepItem): void {
    void this.router.navigate(['/costing/recipes/prep', item.id]);
  }

  private async load(): Promise<void> {
    try {
      this.items.set(await firstValueFrom(this.api.prepItems()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The prep items could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
