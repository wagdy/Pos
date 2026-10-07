import { DecimalPipe } from '@angular/common';
import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CostingApi, TheoreticalCostReport, statusLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

// The Monthly Theoretical Cost report: what the month's sales should have cost by their recipes,
// at what the ingredients cost when each was sold. Net sales are before VAT, as menu prices are.
@Component({
  selector: 'app-theoretical-page',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Month · الشهر</mat-label>
        <input matInput type="month" [ngModel]="month()" (ngModelChange)="pick($event)" [max]="thisMonth" />
      </mat-form-field>
      <span class="spacer"></span>
      <button mat-button (click)="exportCsv()" [disabled]="!report()?.rows?.length"><mat-icon>download</mat-icon> CSV</button>
      <button mat-button (click)="print()" [disabled]="!report()"><mat-icon>print</mat-icon> Print</button>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (report(); as r) {
      <header class="report-head">
        <h2>Monthly theoretical cost <span class="inline-ar">التكلفة النظرية الشهرية</span></h2>
        <div class="muted">
          From<span class="inline-ar">من</span>: <strong>{{ r.from }}</strong> &nbsp; To<span class="inline-ar">إلى</span>:
          <strong>{{ r.to }}</strong> &nbsp;·&nbsp; Food cost target<span class="inline-ar">النسبة المستهدفة</span>:
          <strong>{{ r.foodCostTargetPercent | number: '1.0-1' }}%</strong>
        </div>
      </header>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Item code<span class="ar">كود الصنف</span></th>
              <th>Menu item<span class="ar">صنف المنيو</span></th>
              <th class="num">Quantity sold<span class="ar">الكمية المباعة</span></th>
              <th class="num">Net sales<span class="ar">صافي المبيعات</span></th>
              <th class="num">Recipe cost / unit<span class="ar">تكلفة الوصفة/وحدة</span></th>
              <th class="num">Theoretical cost<span class="ar">التكلفة النظرية</span></th>
              <th class="num">Food cost %<span class="ar">نسبة تكلفة الطعام</span></th>
              <th>Status<span class="ar">الحالة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (row of r.rows; track row.itemCode) {
              <tr>
                <td class="code">{{ row.itemCode }}</td>
                <td>
                  {{ row.menuItem }}
                  @if (row.menuItemAr) {
                    <span class="ar">{{ row.menuItemAr }}</span>
                  }
                </td>
                <td class="num">{{ row.quantitySold }}</td>
                <td class="num">{{ row.netSales | money }}</td>
                <td class="num">{{ row.status === 'NoRecipe' ? '—' : (row.recipeCostPerUnit | money) }}</td>
                <td class="num">
                  @if (row.status !== 'NoRecipe') {
                    {{ row.theoreticalCost | money }}
                    @if (row.sharedCost > 0) {
                      <small class="muted shared">incl. {{ row.sharedCost | money }} shared</small>
                    }
                  } @else if (row.sharedCost > 0) {
                    <!-- Its share of the month's oil and the like: in the total, so shown here too. -->
                    {{ row.sharedCost | money }}
                    <small class="muted shared">shared only</small>
                  } @else {
                    —
                  }
                  @if (row.quantityCostedNow > 0) {
                    <small class="muted shared">{{ row.quantityCostedNow }} at today's prices *</small>
                  }
                </td>
                <td class="num">{{ row.status === 'NoRecipe' || row.foodCostPercent === null ? '—' : (row.foodCostPercent | number: '1.1-1') + '%' }}</td>
                <td>
                  <span class="status" [class]="row.status">
                    {{ statusLabels[row.status].en }}<span class="ar">{{ statusLabels[row.status].ar }}</span>
                  </span>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="8" class="muted">Nothing was sold in this month.</td></tr>
            }
          </tbody>
          @if (r.rows.length) {
            <tfoot>
              <tr>
                <td colspan="2">Total · الإجمالي</td>
                <td class="num">{{ r.quantitySold }}</td>
                <td class="num">{{ r.netSales | money }}</td>
                <td></td>
                <td class="num">{{ r.theoreticalCost | money }}</td>
                <td class="num">{{ r.foodCostPercent === null ? '—' : (r.foodCostPercent | number: '1.1-1') + '%' }}</td>
                <td></td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
      @if (costedNow(r)) {
        <p class="note">
          * Sold before the dish had a recipe, so no cost was recorded at the time: costed by its recipe now, at today's
          average prices.
        </p>
      }
      @if (unrecipedSales(r); as missing) {
        <p class="note">
          {{ missing.count }} {{ missing.count === 1 ? 'menu item' : 'menu items' }} sold this month {{ missing.count === 1 ? 'has' : 'have' }}
          no recipe ({{ missing.sales | money }} of sales): {{ missing.count === 1 ? 'it counts' : 'they count' }} in net sales at no
          ingredient cost, so the total food cost % reads low until {{ missing.count === 1 ? 'it has one' : 'they have recipes' }}.
        </p>
      }
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
    .shared {
      display: block;
    }
  `,
})
export class TheoreticalPage {
  private readonly api = inject(CostingApi);

  protected readonly statusLabels = statusLabels;
  protected readonly thisMonth = TheoreticalPage.monthOf(new Date());

  protected readonly month = signal(this.thisMonth);
  protected readonly report = signal<TheoreticalCostReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const month = this.month();
      untracked(() => void this.load(month));
    });
  }

  protected pick(month: string): void {
    // Cleared or half typed: keep the month on screen.
    if (/^\d{4}-\d{2}$/.test(month)) {
      this.month.set(month);
    }
  }

  protected unrecipedSales(report: TheoreticalCostReport): { count: number; sales: number } | null {
    const rows = report.rows.filter((r) => r.status === 'NoRecipe');
    return rows.length ? { count: rows.length, sales: rows.reduce((sum, r) => sum + r.netSales, 0) } : null;
  }

  protected costedNow(report: TheoreticalCostReport): boolean {
    return report.rows.some((r) => r.quantityCostedNow > 0);
  }

  protected print(): void {
    window.print();
  }

  // For a spreadsheet: a byte order mark so Excel reads the Arabic names as UTF-8.
  protected exportCsv(): void {
    const r = this.report();
    if (!r) {
      return;
    }
    const header = [
      'Item Code · كود الصنف',
      'Menu Item · صنف المنيو',
      'Menu Item (Arabic)',
      'Category',
      'Quantity Sold · الكمية المباعة',
      'Net Sales · صافي المبيعات',
      'Recipe Cost/Unit · تكلفة الوصفة/وحدة',
      'Theoretical Cost · التكلفة النظرية',
      'Shared Cost',
      "Qty Costed at Today's Prices",
      'Food Cost % · نسبة تكلفة الطعام',
      'Status · الحالة',
    ];
    const noRecipe = (status: string) => status === 'NoRecipe';
    const rows = r.rows.map((row) => [
      row.itemCode,
      row.menuItem,
      row.menuItemAr ?? '',
      row.category,
      row.quantitySold,
      row.netSales.toFixed(2),
      noRecipe(row.status) ? '' : row.recipeCostPerUnit.toFixed(2),
      noRecipe(row.status) && row.sharedCost === 0 ? '' : row.theoreticalCost.toFixed(2),
      row.sharedCost.toFixed(2),
      row.quantityCostedNow,
      noRecipe(row.status) || row.foodCostPercent === null ? '' : row.foodCostPercent.toFixed(1),
      `${statusLabels[row.status].en} · ${statusLabels[row.status].ar}`,
    ]);
    const total = ['Total', '', '', '', r.quantitySold, r.netSales.toFixed(2), '', r.theoreticalCost.toFixed(2), '', '', r.foodCostPercent?.toFixed(1) ?? '', ''];
    const csv = [header, ...rows, total].map((cells) => cells.map(TheoreticalPage.cell).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `theoretical-cost-${this.month()}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  private async load(month: string): Promise<void> {
    const [year, m] = month.split('-').map(Number);
    this.loading.set(true);
    this.error.set(null);
    try {
      this.report.set(await firstValueFrom(this.api.theoretical(year, m)));
    } catch (error) {
      this.report.set(null);
      this.error.set(errorMessage(error, 'The report could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private static cell(value: string | number): string {
    const text = String(value);
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  }

  private static monthOf(date: Date): string {
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
  }
}
