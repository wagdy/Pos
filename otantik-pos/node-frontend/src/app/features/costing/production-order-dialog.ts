import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { ProductionOrder, UnitOfMeasure } from '../../core/costing/costing.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

const baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

// One batch as it was made, read-only: what was planned and what came out, each ingredient taken
// at its average cost then, and what that came to per unit of the prep item.
@Component({
  selector: 'app-production-order-dialog',
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatDialogModule, MoneyPipe],
  template: `
    @let o = order;
    <h2 mat-dialog-title>{{ o.prepItem }} <span class="inline-ar">إذن تصنيع</span></h2>
    <mat-dialog-content>
      <dl class="details">
        <div><dt>Date · التاريخ</dt><dd>{{ o.producedAtUtc | date: 'EEE d MMM y, HH:mm' }}</dd></div>
        <div><dt>By · بواسطة</dt><dd>{{ o.producedBy || '—' }}</dd></div>
        @if (o.approvedBy) {
          <div><dt>Approved by · اعتمده</dt><dd>{{ o.approvedBy }}</dd></div>
        }
        <div><dt>Planned · المخطط</dt><dd>{{ o.plannedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }}</dd></div>
        <div><dt>Came out · المنتج فعليًا</dt><dd>{{ o.producedQuantity / o.displayUnitSize | number: '1.0-3' }} {{ o.displayUnit }} ({{ o.yieldPercent | number: '1.0-1' }}%)</dd></div>
        <div><dt>Batch cost · تكلفة الدفعة</dt><dd>{{ o.totalCost | money }}</dd></div>
        <div><dt>Unit cost · تكلفة الوحدة</dt><dd class="strong">{{ o.unitCostPerDisplayUnit | money }} / {{ o.displayUnit }}</dd></div>
      </dl>
      @if (o.status === 'Voided') {
        <p>
          <span class="voided-badge">{{ o.replacedBy ? 'Corrected' : 'Voided' }}<span class="ar">{{ o.replacedBy ? 'مُصحح' : 'ملغى' }}</span></span>
          by {{ o.voidedBy }}, {{ o.voidedAtUtc | date: 'd MMM y, HH:mm' }}:
          {{ o.replacedBy
            ? 'reversed and made again with the right quantities, as another batch.'
            : 'reversed, its ingredients back in stock and the prep item back out.' }}
        </p>
      } @else if (o.revisionOf) {
        <p class="muted">A correction: the batch it replaced was reversed.</p>
      }
      @if (o.notes) {
        <p class="muted">{{ o.notes }}</p>
      }
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Ingredient<span class="ar">المكون</span></th>
              <th class="num">Taken<span class="ar">الكمية المصروفة</span></th>
              <th class="num">At<span class="ar">متوسط التكلفة</span></th>
              <th class="num">Cost<span class="ar">التكلفة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (l of o.lines; track l.rawMaterialId) {
              <tr>
                <td>{{ l.material }}</td>
                <td class="num">{{ l.quantity | number: '1.0-3' }} {{ base(l.unit) }}</td>
                <td class="num muted">{{ l.costPerPurchaseUnit === null ? '—' : (l.costPerPurchaseUnit | money) + ' / ' + l.purchaseUnit }}</td>
                <td class="num">{{ l.cost === null ? '—' : (l.cost | money) }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td colspan="3">{{ o.lines.length }} {{ o.lines.length === 1 ? 'ingredient' : 'ingredients' }}</td>
              <td class="num">{{ o.totalCost | money }}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styleUrl: './costing.scss',
  styles: `
    .details {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
      gap: 8px 16px;
      margin: 0 0 12px;

      dt {
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      dd {
        margin: 2px 0 0;
        font: var(--mat-sys-body-large);
      }
      .strong {
        font-weight: 600;
      }
    }
  `,
})
export class ProductionOrderDialog {
  protected readonly order = inject<ProductionOrder>(MAT_DIALOG_DATA);

  protected base(unit: UnitOfMeasure): string {
    return baseLabels[unit];
  }
}

export function showProductionOrder(dialog: MatDialog, order: ProductionOrder): void {
  dialog.open<ProductionOrderDialog, ProductionOrder>(ProductionOrderDialog, { data: order, width: '760px', maxWidth: '95vw', autoFocus: 'dialog' });
}
