import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  CostingApi,
  StockCountSummary,
  VarianceReport,
  VarianceRow,
  countUnit,
  evaluationLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

// Pillar 3: what left the stores between two counts against what the recipes say the sales
// took. Red is a shortfall beyond the tolerance (waste, theft, over-portioning, a recipe written
// too light); blue a surplus (under-portioning, a recipe too heavy, a delivery never entered).
// Quantities are as bought, before trimming, and valued at the average purchase cost.
@Component({
  selector: 'app-variance-page',
  imports: [DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>From count · جرد البداية</mat-label>
        <mat-select [ngModel]="report()?.from?.id" (ngModelChange)="pick('from', $event)">
          @for (c of posted(); track c.id) {
            <mat-option [value]="c.id">{{ c.postedAtUtc | date: 'EEE d MMM, HH:mm' }} · {{ c.postedBy }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>To count · جرد النهاية</mat-label>
        <mat-select [ngModel]="report()?.to?.id" (ngModelChange)="pick('to', $event)">
          @for (c of posted(); track c.id) {
            <mat-option [value]="c.id">{{ c.postedAtUtc | date: 'EEE d MMM, HH:mm' }} · {{ c.postedBy }}</mat-option>
          }
        </mat-select>
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
      <p class="note"><a routerLink="/costing/counts">Stock counts · الجرد</a></p>
    } @else if (report(); as r) {
      <header class="report-head">
        <h2>Variance analysis <span class="inline-ar">تحليل الانحراف</span></h2>
        <div class="muted">
          From<span class="inline-ar">من</span>: <strong>{{ r.from.postedAtUtc | date: 'd MMM y, HH:mm' }}</strong>
          &nbsp; To<span class="inline-ar">إلى</span>: <strong>{{ r.to.postedAtUtc | date: 'd MMM y, HH:mm' }}</strong>
          &nbsp;·&nbsp; Tolerance<span class="inline-ar">الحد المسموح</span>: <strong>±{{ r.tolerancePercent | number: '1.0-1' }}%</strong>
        </div>
      </header>

      <div class="figures">
        <div class="box" [class.deficit]="r.netVarianceValue > 0" [class.surplus]="r.netVarianceValue < 0">
          <span class="label">Net variance<span class="ar">صافي قيمة الانحراف</span></span>
          <strong>{{ r.netVarianceValue | money }}</strong>
          <small class="muted">{{ r.netVarianceValue > 0 ? 'more used than the recipes say' : r.netVarianceValue < 0 ? 'less used than the recipes say' : 'as the recipes say' }}</small>
        </div>
        <div class="box">
          <span class="label">Recorded waste<span class="ar">الهالك المسجل</span></span>
          <strong>{{ r.recordedWasteValue | money }}</strong>
        </div>
        <div class="box" [class.deficit]="r.unexplainedValue > 0" [class.surplus]="r.unexplainedValue < 0">
          <span class="label">Unexplained<span class="ar">غير مبرر</span></span>
          <strong>{{ r.unexplainedValue | money }}</strong>
          <small class="muted">not covered by recorded waste</small>
        </div>
        <div class="box" [class.deficit]="r.unfavourableCount > 0">
          <span class="label">Unfavourable materials<span class="ar">خامات غير مواتية</span></span>
          <strong>{{ r.unfavourableCount }} of {{ r.rows.length }}</strong>
        </div>
        <div class="box">
          <span class="label">Largest variance<span class="ar">أكبر انحراف نسبي</span></span>
          <strong>{{ r.largestRelativePercent === null ? '—' : (r.largestRelativePercent | number: '1.1-1') + '%' }}</strong>
          <small class="muted">{{ r.largestRelativeMaterial ?? '' }}</small>
        </div>
      </div>

      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Code<span class="ar">الكود</span></th>
              <th>Material<span class="ar">الخامة</span></th>
              <th class="num">Opening<span class="ar">رصيد أول</span></th>
              <th class="num">Received<span class="ar">الوارد</span></th>
              <th class="num">Waste<span class="ar">الهالك</span></th>
              <th class="num">Standard usage<span class="ar">الاستهلاك المعياري</span></th>
              <th class="num">Expected closing<span class="ar">المتوقع</span></th>
              <th class="num">Closing count<span class="ar">رصيد آخر</span></th>
              <th class="num">Actual usage<span class="ar">الاستهلاك الفعلي</span></th>
              <th class="num">Variance<span class="ar">الانحراف</span></th>
              <th class="num">%<span class="ar">النسبة</span></th>
              <th class="num">Unit cost<span class="ar">تكلفة الوحدة</span></th>
              <th class="num">Variance value<span class="ar">قيمة الانحراف</span></th>
              <th class="num">Unexplained<span class="ar">غير مبرر</span></th>
              <th class="eval-col">Evaluation<span class="ar">التقييم</span></th>
            </tr>
          </thead>
          <tbody>
            @for (row of r.rows; track row.rawMaterialId) {
              <tr [class.deficit]="row.evaluation === 'Unfavourable'" [class.surplus]="row.evaluation === 'Favourable'">
                <td class="code">{{ row.code ?? '' }}</td>
                <td>{{ row.material }}</td>
                <td class="num">{{ qty(row, row.opening) }}</td>
                <td class="num">{{ qty(row, row.received) }}</td>
                <td class="num">
                  {{ qty(row, row.rawWaste + row.productWaste) }}
                  @if (row.rawWaste && row.productWaste) {
                    <small class="muted block">{{ qty(row, row.rawWaste) }} raw · {{ qty(row, row.productWaste) }} dishes</small>
                  }
                </td>
                <td class="num">{{ qty(row, row.standardUsage) }}</td>
                <td class="num">{{ qty(row, row.expectedClosing) }}</td>
                <td class="num">{{ qty(row, row.closing) }}</td>
                <td class="num">{{ qty(row, row.actualUsage) }}</td>
                @if (row.evaluation === 'SharedCost') {
                  <!-- No recipe uses it, so no standard: what was used, and what it was worth. -->
                  <td class="num">—</td>
                  <td class="num">—</td>
                  <td class="num">{{ row.unitCost === null ? 'No price' : (row.unitCost * unitOf(row).size | money) + ' / ' + unitOf(row).label }}</td>
                  <td class="num muted">{{ row.varianceValue === null ? '—' : (row.varianceValue | money) }} used</td>
                  <td class="num">—</td>
                } @else {
                  <td class="num strong">{{ signed(row, row.varianceQuantity) }}</td>
                  <td class="num">{{ row.variancePercent === null ? '—' : (row.variancePercent > 0 ? '+' : '') + (row.variancePercent | number: '1.1-1') + '%' }}</td>
                  <td class="num">{{ row.unitCost === null ? 'No price' : (row.unitCost * unitOf(row).size | money) + ' / ' + unitOf(row).label }}</td>
                  <td class="num strong">{{ row.varianceValue === null ? '—' : (row.varianceValue | money) }}</td>
                  <td class="num">{{ row.unexplainedValue === null ? signed(row, row.unexplainedQuantity) : (row.unexplainedValue | money) }}</td>
                }
                <td>
                  <span class="eval" [class]="row.evaluation">
                    {{ labels[row.evaluation].en }}<span class="ar">{{ labels[row.evaluation].ar }}</span>
                  </span>
                  @if (coveredByWaste(row, r.tolerancePercent)) {
                    <small class="muted block">covered by recorded waste · يغطيه الهالك المسجل</small>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="15" class="muted">No material was counted in both counts.</td></tr>
            }
          </tbody>
          @if (r.rows.length) {
            <tfoot>
              <tr>
                <td colspan="12">Total · الإجمالي</td>
                <td class="num">{{ r.netVarianceValue | money }}</td>
                <td class="num">{{ r.unexplainedValue | money }}</td>
                <td></td>
              </tr>
            </tfoot>
          }
        </table>
      </div>

      <div class="notes">
        @if (r.soldWithoutStock.length) {
          <p class="note warn">
            <strong>Sold without a recipe, so they took nothing from stock:</strong>
            @for (s of r.soldWithoutStock; track s.itemCode; let last = $last) {
              {{ s.menuItem }} × {{ s.quantity }}{{ last ? '.' : ',' }}
            }
            Their ingredients show above as a shortfall: write their recipes before reading it as waste.
          </p>
        }
        @if (r.notInBothCounts.length) {
          <p class="note">Not in both counts, so not in the report: {{ r.notInBothCounts.join(', ') }}.</p>
        }
        @if (r.missingPrices.length) {
          <p class="note">No price, so no value: {{ r.missingPrices.join(', ') }}.</p>
        }
        @if (sharedRows(r)) {
          <p class="note">Shared costs, such as frying oil, are in no recipe: their usage is shown, but left out of the totals.</p>
        }
        <p class="note">
          Variance = actual usage − standard usage, and includes recorded waste; unexplained is what remains once the waste is taken out.
          Quantities are as bought, before trimming.
        </p>
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
    .figures {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
      gap: 8px;
      margin-bottom: 12px;
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
    }
    .strong {
      font-weight: 600;
    }
    .block {
      display: block;
    }
    .eval-col {
      min-width: 190px;
    }
    .notes {
      margin-top: 12px;
    }
    .warn {
      color: var(--mat-sys-on-surface);
    }
  `,
})
export class VariancePage {
  private readonly api = inject(CostingApi);

  protected readonly labels = evaluationLabels;

  protected readonly counts = signal<StockCountSummary[]>([]);
  protected readonly posted = computed(() => this.counts().filter((c) => c.status === 'Posted'));
  // What was asked for; null ids are the server's choice, the last two counts. The selects show
  // the counts the report actually covers.
  private readonly request = signal<{ from: string | null; to: string | null }>({ from: null, to: null });
  protected readonly report = signal<VarianceReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void firstValueFrom(this.api.stockCounts())
      .then((counts) => this.counts.set(counts))
      .catch(() => undefined);
    effect(() => {
      const { from, to } = this.request();
      untracked(() => void this.load(from, to));
    });
  }

  protected pick(end: 'from' | 'to', id: string): void {
    // Moving the end alone lets the server take the count before it as the start.
    this.request.set(end === 'to' ? { from: null, to: id } : { from: id, to: this.report()?.to.id ?? null });
  }

  // In kg, litres or pieces, as the stores are counted.
  protected qty(row: VarianceRow, quantity: number): string {
    const unit = this.unitOf(row);
    return `${(quantity / unit.size).toLocaleString('en-EG', { maximumFractionDigits: 3 })} ${unit.label}`;
  }

  protected unitOf(row: VarianceRow): { label: string; size: number } {
    return countUnit(row.unit, row.purchaseUnitSize);
  }

  protected signed(row: VarianceRow, quantity: number): string {
    return (quantity > 0 ? '+' : '') + this.qty(row, quantity);
  }

  // A shortfall the recorded waste accounts for, within the tolerance: a loss, but not a mystery.
  protected coveredByWaste(row: VarianceRow, tolerance: number): boolean {
    if (row.evaluation !== 'Unfavourable' || row.rawWaste + row.productWaste <= 0) {
      return false;
    }
    return row.standardUsage > 0
      ? Math.abs((row.unexplainedQuantity / row.standardUsage) * 100) <= tolerance
      : row.unexplainedQuantity <= 0;
  }

  protected sharedRows(report: VarianceReport): boolean {
    return report.rows.some((r) => r.evaluation === 'SharedCost');
  }

  protected print(): void {
    window.print();
  }

  // For a spreadsheet: a byte order mark so Excel reads the Arabic as UTF-8. Quantities in kg,
  // litres and pieces, as on screen.
  protected exportCsv(): void {
    const r = this.report();
    if (!r) {
      return;
    }
    const header = [
      'Code · الكود',
      'Material · الخامة',
      'Category',
      'Unit',
      'Opening · رصيد أول',
      'Received · الوارد',
      'Waste Raw · هالك خام',
      'Waste Dishes · هالك منتج',
      'Standard Usage · الاستهلاك المعياري',
      'Expected Closing · المتوقع',
      'Closing Count · رصيد آخر',
      'Actual Usage · الاستهلاك الفعلي',
      'Variance · الانحراف',
      'Variance % · النسبة',
      'Unit Cost · تكلفة الوحدة',
      'Variance Value · قيمة الانحراف',
      'Unexplained Value · غير مبرر',
      'Evaluation · التقييم',
    ];
    const n = (row: VarianceRow, q: number) => String(Math.round((q / this.unitOf(row).size) * 1000) / 1000);
    const rows = r.rows.map((row) => [
      row.code ?? '',
      row.material,
      row.category ?? '',
      this.unitOf(row).label,
      n(row, row.opening),
      n(row, row.received),
      n(row, row.rawWaste),
      n(row, row.productWaste),
      n(row, row.standardUsage),
      n(row, row.expectedClosing),
      n(row, row.closing),
      n(row, row.actualUsage),
      n(row, row.varianceQuantity),
      row.variancePercent?.toFixed(1) ?? '',
      row.unitCost === null ? '' : (row.unitCost * this.unitOf(row).size).toFixed(2),
      row.varianceValue?.toFixed(2) ?? '',
      row.unexplainedValue?.toFixed(2) ?? '',
      `${evaluationLabels[row.evaluation].en} · ${evaluationLabels[row.evaluation].ar}`,
    ]);
    const total = ['Total', '', '', '', '', '', '', '', '', '', '', '', '', '', '', r.netVarianceValue.toFixed(2), r.unexplainedValue.toFixed(2), ''];
    const csv = [header, ...rows, total].map((cells) => cells.map(VariancePage.cell).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `variance-${r.from.postedAtUtc.slice(0, 10)}-to-${r.to.postedAtUtc.slice(0, 10)}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  private async load(from: string | null, to: string | null): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const report = await firstValueFrom(this.api.variance(from, to));
      this.report.set(report);
    } catch (error) {
      this.report.set(null);
      this.error.set(errorMessage(error, 'The variance report could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private static cell(value: string): string {
    return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
  }
}
