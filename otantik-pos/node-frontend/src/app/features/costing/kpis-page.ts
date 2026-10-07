import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi, Kpi, KpiReport, MonthlyExpenses, kpiLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

type Tone = 'ok' | 'bad' | 'check' | 'none';

const expenseFields: { key: keyof MonthlyExpenses; en: string; ar: string; hours?: boolean }[] = [
  { key: 'wagesAndBenefits', en: 'Wages and benefits', ar: 'الرواتب والمزايا' },
  { key: 'labourHours', en: 'Labour hours', ar: 'ساعات العمل', hours: true },
  { key: 'occupationCost', en: 'Rent and occupation', ar: 'الإيجار' },
  { key: 'otherControllableCosts', en: 'Other controllable costs', ar: 'تكاليف أخرى قابلة للتحكم' },
  { key: 'interest', en: 'Interest', ar: 'الفوائد' },
  { key: 'depreciation', en: 'Depreciation', ar: 'الإهلاك' },
];

// Pillar 4: the template's financial KPIs for a business month, each against its healthy range.
// Sales and recipe costs come from the till, losses from the stock ledger; wages, hours and
// overheads are entered here for the month. A KPI missing what it needs says so, never 0.
@Component({
  selector: 'app-kpis-page',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Month · الشهر</mat-label>
        <input matInput type="month" [ngModel]="month()" (ngModelChange)="pick($event)" [max]="thisMonth" />
      </mat-form-field>
      <span class="spacer"></span>
      <button mat-button (click)="print()" [disabled]="!report()"><mat-icon>print</mat-icon> Print</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      <header class="report-head">
        <h2>Financial KPIs <span class="inline-ar">مؤشرات الأداء المالية</span></h2>
        <div class="muted">
          From<span class="inline-ar">من</span>: <strong>{{ r.from }}</strong> &nbsp; To<span class="inline-ar">إلى</span>:
          <strong>{{ r.to }}</strong> &nbsp;·&nbsp; {{ r.statement.paidOrders }} paid orders
        </div>
      </header>

      <div class="kpis">
        @for (k of r.kpis; track k.key) {
          <div class="kpi" [class]="tone(k)">
            <div class="name">
              {{ labels[k.key].en }}
              <span class="ar">{{ labels[k.key].ar }}</span>
            </div>
            @if (k.value !== null) {
              <strong class="value">
                {{ labels[k.key].money ? (k.value | money) : (k.value | number: '1.1-1') + '%' }}
              </strong>
            } @else {
              <strong class="value muted">—</strong>
            }
            <div class="status">
              @switch (k.status) {
                @case ('Healthy') {
                  Healthy · صحي
                }
                @case ('Above') {
                  Above the healthy range · أعلى من المعدل
                }
                @case ('Below') {
                  Below the healthy range · أقل من المعدل
                }
                @case ('NoRange') {
                  Depends on the restaurant · يختلف
                }
                @default {
                  {{ k.missing }}
                }
              }
            </div>
            @if (k.healthyFrom !== null) {
              <div class="range muted">Healthy<span class="inline-ar">المعدل الصحي</span>: {{ k.healthyFrom }}–{{ k.healthyTo }}%</div>
            }
            @if (k.key === 'FoodCost' && k.value !== null) {
              <div class="range muted">
                Recipes {{ share(r.statement.foodRecipeCost, r.statement.foodSales) | number: '1.1-1' }}% + losses
                {{ share(lossTotal(r), r.statement.foodSales) | number: '1.1-1' }}%
              </div>
            }
            @if (tone(k) === 'check') {
              <div class="range">{{ lowHint(k) }}</div>
            }
            <div class="formula muted">{{ labels[k.key].formula }}<span class="ar">{{ labels[k.key].formulaAr }}</span></div>
          </div>
        }
      </div>

      @if (r.itemsWithoutRecipe > 0) {
        <p class="note warn">
          {{ r.itemsWithoutRecipe }} {{ r.itemsWithoutRecipe === 1 ? 'dish' : 'dishes' }} sold this month {{ r.itemsWithoutRecipe === 1 ? 'has' : 'have' }} no recipe
          ({{ r.salesWithoutRecipe | money }} of sales): their ingredients are not in the costs, so the cost % reads low.
          <a routerLink="/costing/recipes">Recipes</a>
        </p>
      }
      @if (r.beverageCategories.length === 0) {
        <p class="note">Every sale counts as food until the drinks categories are marked in <a routerLink="/costing/settings">Settings</a>.</p>
      }

      <div class="columns">
        <section>
          <h3>The month <span class="inline-ar">قائمة الدخل</span></h3>
          <div class="table-wrap">
            <table class="grid statement">
              <tbody>
                <tr><td>Food sales · مبيعات الطعام</td><td class="num">{{ r.statement.foodSales | money }}</td><td class="num muted"></td></tr>
                <tr><td>Beverage sales · مبيعات المشروبات</td><td class="num">{{ r.statement.beverageSales | money }}</td><td></td></tr>
                @if (r.statement.deliveryFees) {
                  <tr><td>Delivery fees · رسوم التوصيل</td><td class="num">{{ r.statement.deliveryFees | money }}</td><td></td></tr>
                }
                <tr class="total"><td>Revenue · الإيرادات</td><td class="num">{{ r.statement.revenue | money }}</td><td class="num">100%</td></tr>
                <tr><td>Food cost · تكلفة الطعام</td><td class="num">{{ r.statement.foodRecipeCost | money }}</td><td class="num muted">{{ pct(r.statement.foodRecipeCost, r) }}</td></tr>
                <tr><td>Beverage cost · تكلفة المشروبات</td><td class="num">{{ r.statement.beverageRecipeCost | money }}</td><td class="num muted">{{ pct(r.statement.beverageRecipeCost, r) }}</td></tr>
                <tr><td>Kitchen waste · هالك المطبخ</td><td class="num">{{ r.statement.kitchenWaste | money }}</td><td class="num muted">{{ pct(r.statement.kitchenWaste, r) }}</td></tr>
                <tr><td>Spoilage · الهالك المسجل</td><td class="num">{{ r.statement.spoilage | money }}</td><td class="num muted">{{ pct(r.statement.spoilage, r) }}</td></tr>
                <tr><td>Count differences · فروق الجرد</td><td class="num">{{ r.statement.countDifferences | money }}</td><td class="num muted">{{ pct(r.statement.countDifferences, r) }}</td></tr>
                <tr class="total"><td>Cost of sales · تكلفة المبيعات</td><td class="num">{{ r.statement.costOfSales | money }}</td><td class="num">{{ pct(r.statement.costOfSales, r) }}</td></tr>
                <tr class="total"><td>Gross profit · الربح الإجمالي</td><td class="num">{{ r.statement.grossProfit | money }}</td><td class="num">{{ pct(r.statement.grossProfit, r) }}</td></tr>
                <tr><td>Wages and benefits · الرواتب</td><td class="num">{{ entered(r.statement.wagesAndBenefits) }}</td><td class="num muted">{{ pct(r.statement.wagesAndBenefits, r) }}</td></tr>
                <tr><td>Rent and occupation · الإيجار</td><td class="num">{{ entered(r.statement.occupationCost) }}</td><td class="num muted">{{ pct(r.statement.occupationCost, r) }}</td></tr>
                <tr><td>Other controllable · تكاليف أخرى</td><td class="num">{{ entered(r.statement.otherControllableCosts) }}</td><td class="num muted">{{ pct(r.statement.otherControllableCosts, r) }}</td></tr>
                <tr><td>Interest · الفوائد</td><td class="num">{{ entered(r.statement.interest) }}</td><td class="num muted">{{ pct(r.statement.interest, r) }}</td></tr>
                <tr><td>Depreciation · الإهلاك</td><td class="num">{{ entered(r.statement.depreciation) }}</td><td class="num muted">{{ pct(r.statement.depreciation, r) }}</td></tr>
                <tr class="total" [class.loss]="(r.statement.netProfit ?? 0) < 0">
                  <td>Net profit · صافي الربح</td>
                  <td class="num">{{ r.statement.netProfit === null ? 'Enter the wages' : (r.statement.netProfit | money) }}</td>
                  <td class="num">{{ pct(r.statement.netProfit, r) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="note small">
            Revenue is before VAT, after promo discounts. Food and beverage cost: what the sales took by their recipes, with
            shared costs such as frying oil on the meals. Losses are counted against food.
          </p>
        </section>

        <section class="no-print">
          <h3>Monthly costs <span class="inline-ar">التكاليف الشهرية</span></h3>
          <p class="note small">What {{ monthName() }} cost beyond its ingredients. Leave a figure empty until it is known.</p>
          <div class="expenses">
            @for (f of fields; track f.key) {
              <mat-form-field subscriptSizing="dynamic" floatLabel="always">
                <mat-label>{{ f.en }} · {{ f.ar }}</mat-label>
                @if (!f.hours) {
                  <span matTextPrefix>L.E&nbsp;</span>
                }
                <input matInput type="number" min="0" step="any" [ngModel]="draft()[f.key]" (ngModelChange)="set(f.key, $event)" [disabled]="!canEdit()" />
                @if (f.hours) {
                  <span matTextSuffix>&nbsp;h</span>
                }
              </mat-form-field>
            }
          </div>
          @if (canEdit()) {
            <div class="toolbar">
              <span class="muted small">
                @if (r.expenses.updatedBy) {
                  Last saved by {{ r.expenses.updatedBy }}.
                }
              </span>
              <span class="spacer"></span>
              <button mat-flat-button (click)="saveExpenses()" [disabled]="saving() || !changed()">Save the month's costs</button>
            </div>
          }
        </section>
      </div>

      <h3>Item profitability <span class="inline-ar">ربحية العنصر</span></h3>
      <p class="note small">Selling price − ingredient cost, over what was sold this month. The most profitable first.</p>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Item code<span class="ar">كود الصنف</span></th>
              <th>Menu item<span class="ar">صنف المنيو</span></th>
              <th class="num">Sold<span class="ar">المباع</span></th>
              <th class="num">Net sales<span class="ar">صافي المبيعات</span></th>
              <th class="num">Ingredient cost<span class="ar">تكلفة المكونات</span></th>
              <th class="num">Profit<span class="ar">الربح</span></th>
              <th class="num">Profit per item<span class="ar">ربح الوحدة</span></th>
              <th class="num">Margin<span class="ar">الهامش</span></th>
            </tr>
          </thead>
          <tbody>
            @for (item of r.items; track item.itemCode; let i = $index) {
              <tr [class.top]="i < 3 && item.profit !== null && item.profit > 0">
                <td class="code">{{ item.itemCode }}</td>
                <td>
                  {{ item.menuItem }}
                  @if (item.beverage) {
                    <small class="muted"> · drink</small>
                  }
                  @if (item.menuItemAr) {
                    <span class="ar">{{ item.menuItemAr }}</span>
                  }
                </td>
                <td class="num">{{ item.quantitySold }}</td>
                <td class="num">{{ item.netSales | money }}</td>
                <td class="num">{{ item.cost === null ? 'No recipe' : (item.cost | money) }}</td>
                <td class="num strong">{{ item.profit === null ? '—' : (item.profit | money) }}</td>
                <td class="num">{{ item.profitPerUnit === null ? '—' : (item.profitPerUnit | money) }}</td>
                <td class="num">{{ item.marginPercent === null ? '—' : (item.marginPercent | number: '1.1-1') + '%' }}</td>
              </tr>
            } @empty {
              <tr><td colspan="8" class="muted">Nothing was sold this month.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .report-head {
      margin-bottom: 12px;

      h2 {
        margin: 0 0 4px;
        font: var(--mat-sys-title-large);
      }
    }
    h3 {
      margin: 20px 0 8px;
      font: var(--mat-sys-title-medium);
    }
    .kpis {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
      gap: 10px;
    }
    .kpi {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 12px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      .name {
        font: var(--mat-sys-title-small);
      }
      .value {
        font: var(--mat-sys-headline-small);
      }
      .status {
        font: var(--mat-sys-label-large);
      }
      .range,
      .formula {
        font: var(--mat-sys-body-small);
      }
      .formula {
        margin-top: auto;
        padding-top: 6px;
      }
      &.ok {
        border-color: var(--till-ok);
        background: color-mix(in srgb, var(--till-ok) 10%, var(--mat-sys-surface));
      }
      &.bad {
        border-color: var(--mat-sys-error);
        background: color-mix(in srgb, var(--mat-sys-error) 9%, var(--mat-sys-surface));

        .value,
        .status {
          color: var(--mat-sys-error);
        }
      }
      &.check {
        border-color: #b26a00;
        background: color-mix(in srgb, #f5a623 12%, var(--mat-sys-surface));
      }
    }
    .columns {
      display: grid;
      grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
      gap: 16px;
    }
    @media (max-width: 900px) {
      .columns {
        grid-template-columns: 1fr;
      }
    }
    .statement tr.total td {
      font-weight: 600;
      background: var(--mat-sys-surface-container-low);
    }
    .statement tr.loss td {
      color: var(--mat-sys-error);
    }
    .expenses {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
      gap: 10px;
      margin-bottom: 8px;
    }
    tr.top td {
      background: color-mix(in srgb, var(--till-ok) 10%, var(--mat-sys-surface));
    }
    .strong {
      font-weight: 600;
    }
    .small {
      font: var(--mat-sys-body-small);
    }
    .warn {
      color: var(--mat-sys-on-surface);
    }
  `,
})
export class KpisPage {
  private readonly api = inject(CostingApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  protected readonly labels = kpiLabels;
  protected readonly fields = expenseFields;
  protected readonly canEdit = computed(() => this.auth.can(Permissions.InventoryManage));
  protected readonly thisMonth = KpisPage.monthOf(new Date());

  protected readonly month = signal(this.thisMonth);
  protected readonly report = signal<KpiReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly draft = signal<MonthlyExpenses>(KpisPage.blank());

  protected readonly changed = computed(() => {
    const saved = this.report()?.expenses;
    const draft = this.draft();
    return !!saved && expenseFields.some((f) => (saved[f.key] ?? null) !== (draft[f.key] ?? null));
  });

  protected readonly monthName = computed(() => {
    const [year, month] = this.month().split('-').map(Number);
    return new Date(year, month - 1, 1).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
  });

  constructor() {
    effect(() => {
      const month = this.month();
      untracked(() => void this.load(month));
    });
  }

  // Green in range; red on the side that hurts (a cost above, a margin below); amber on the
  // other side of a cost, which more often means a recipe or a price is missing than a windfall.
  protected tone(kpi: Kpi): Tone {
    switch (kpi.status) {
      case 'Healthy':
        return 'ok';
      case 'Above':
        return kpi.highIsBad ? 'bad' : 'ok';
      case 'Below':
        return kpi.highIsBad ? 'check' : 'bad';
      default:
        return 'none';
    }
  }

  // Under the range on a cost: more often something left out than a saving.
  protected lowHint(kpi: Kpi): string {
    return kpi.key === 'LabourCost'
      ? 'Low: check every wage and benefit is entered.'
      : 'Low: check every dish has a recipe and every material a price.';
  }

  protected lossTotal(r: KpiReport): number {
    return r.statement.kitchenWaste + r.statement.spoilage + r.statement.countDifferences;
  }

  protected share(part: number, whole: number): number | null {
    return whole > 0 ? (part / whole) * 100 : null;
  }

  // As a share of revenue, for the statement's last column.
  protected pct(value: number | null, r: KpiReport): string {
    return value === null || r.statement.revenue <= 0 ? '' : `${((value / r.statement.revenue) * 100).toFixed(1)}%`;
  }

  protected entered(value: number | null): string {
    return value === null ? 'Not entered' : `L.E ${value.toLocaleString('en-EG', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  }

  protected pick(month: string): void {
    if (/^\d{4}-\d{2}$/.test(month)) {
      this.month.set(month);
    }
  }

  protected set(key: keyof MonthlyExpenses, value: number | string | null): void {
    const number = value === '' || value === null ? null : Number(value);
    this.draft.update((draft) => ({ ...draft, [key]: Number.isFinite(number) ? number : null }));
  }

  protected async saveExpenses(): Promise<void> {
    const [year, month] = this.month().split('-').map(Number);
    this.saving.set(true);
    try {
      const draft = this.draft();
      await firstValueFrom(
        this.api.saveExpenses(year, month, Object.fromEntries(expenseFields.map((f) => [f.key, draft[f.key] ?? null])) as unknown as MonthlyExpenses),
      );
      this.notify.info(`${this.monthName()}'s costs saved.`);
      await this.load(this.month());
    } catch (error) {
      this.notify.error(error, "The month's costs could not be saved.");
    } finally {
      this.saving.set(false);
    }
  }

  protected print(): void {
    window.print();
  }

  private async load(month: string): Promise<void> {
    const [year, m] = month.split('-').map(Number);
    this.loading.set(true);
    this.error.set(null);
    try {
      const report = await firstValueFrom(this.api.kpis(year, m));
      this.report.set(report);
      this.draft.set({ ...report.expenses });
    } catch (error) {
      this.report.set(null);
      this.error.set(errorMessage(error, 'The KPIs could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private static blank(): MonthlyExpenses {
    return { wagesAndBenefits: null, labourHours: null, otherControllableCosts: null, occupationCost: null, interest: null, depreciation: null };
  }

  private static monthOf(date: Date): string {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
  }
}
