import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { PurchaseInvoice, PurchaseInvoiceLine, UnitOfMeasure } from '../../core/costing/costing.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { PaperPreview } from './invoice-paper';

const baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

// One invoice, read-only: who it is from and when, each line as billed (quantity, unit, unit
// price, line total) and what it put into stock, or that it is an expense and under which
// category, the total split into stock and expenses, and the paper itself. Shown for review
// before posting, from the history's Show, and on the invoice's own page.
@Component({
  selector: 'app-invoice-view',
  imports: [DatePipe, DecimalPipe, MoneyPipe, PaperPreview],
  template: `
    @let inv = invoice();
    <dl class="details">
      <div><dt>Supplier · المورد</dt><dd>{{ inv.supplierName ?? '—' }}</dd></div>
      <div><dt>Invoice no. · رقم الفاتورة</dt><dd>{{ inv.supplierInvoiceNumber ?? '—' }}</dd></div>
      <div><dt>Date · التاريخ</dt><dd>{{ date() | date: 'd MMM y' }}</dd></div>
      <div><dt>Total · الإجمالي</dt><dd class="total">{{ inv.total | money }}</dd></div>
    </dl>
    <p class="muted status">
      @if (inv.status === 'Posted') {
        Posted {{ inv.postedAtUtc | date: 'EEE d MMM y, HH:mm' }} by {{ inv.postedBy || 'someone' }}; entered by {{ inv.createdBy || 'someone' }}.
      } @else {
        A draft, not posted: nothing on it is in stock or in the expenses yet.
      }
    </p>
    <div class="table-wrap">
      <table class="grid">
        <thead>
          <tr>
            <th>Item<span class="ar">الصنف</span></th>
            <th>Category<span class="ar">الفئة</span></th>
            <th class="num">Quantity<span class="ar">الكمية</span></th>
            <th>Unit<span class="ar">الوحدة</span></th>
            <th class="num">Unit price<span class="ar">سعر الوحدة</span></th>
            <th class="num">Line total<span class="ar">إجمالي السطر</span></th>
            <th class="num">Into stock<span class="ar">إلى المخزون</span></th>
          </tr>
        </thead>
        <tbody>
          @for (l of inv.lines; track l.rawMaterialId ?? l.expenseItemId) {
            <tr>
              <td>
                {{ l.material }}
                @if (l.itemType === 'Expense') {
                  <span class="badge expense">Expense · مصروف</span>
                }
              </td>
              <td>{{ l.category ?? '—' }}</td>
              <td class="num">{{ l.enteredQuantity | number: '1.0-3' }}</td>
              <td>{{ l.unitLabel }}</td>
              <td class="num">{{ l.cost === null ? '' : (l.cost / l.enteredQuantity | money) }}</td>
              <td class="num">{{ l.cost === null ? 'No price' : (l.cost | money) }}</td>
              <td class="num muted">{{ l.itemType === 'Expense' ? 'Not stock' : (l.quantity | number: '1.0-3') + ' ' + base(l) }}</td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="muted">No lines.</td></tr>
          }
        </tbody>
        <tfoot>
          @if (inv.expenseTotal > 0) {
            <tr class="split">
              <td colspan="5">Into stock · إلى المخزون</td>
              <td class="num">{{ inv.inventoryTotal | money }}</td>
              <td></td>
            </tr>
            <tr class="split">
              <td colspan="5">Expenses · مصروفات</td>
              <td class="num">{{ inv.expenseTotal | money }}</td>
              <td></td>
            </tr>
          }
          <tr>
            <td colspan="5">{{ inv.lines.length }} {{ inv.lines.length === 1 ? 'line' : 'lines' }}</td>
            <td class="num">{{ inv.total | money }}</td>
            <td></td>
          </tr>
        </tfoot>
      </table>
    </div>
    <section class="paper">
      <h3>Paper <span class="inline-ar">صورة الفاتورة</span></h3>
      @if (inv.attachment; as paper) {
        <app-paper-preview [invoiceId]="inv.id" [fileName]="paper.fileName" />
      } @else {
        <p class="muted">No photo or scan attached.</p>
      }
    </section>
  `,
  styleUrl: './costing.scss',
  styles: `
    .details {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
      gap: 8px 16px;
      margin: 0 0 4px;

      dt {
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      dd {
        margin: 2px 0 0;
        font: var(--mat-sys-body-large);
      }
      .total {
        font-weight: 600;
      }
    }
    .status {
      margin: 0 0 12px;
    }
    .badge.expense {
      background: color-mix(in srgb, var(--till-warn) 16%, transparent);
      color: var(--till-warn);
    }
    tfoot tr.split td {
      font-weight: 400;
    }
    .paper {
      margin-top: 16px;

      h3 {
        margin: 0 0 8px;
        font: var(--mat-sys-title-medium);
      }
    }
  `,
})
export class InvoiceView {
  readonly invoice = input.required<PurchaseInvoice>();

  // The date on the supplier's paper; when none was written, the day it was posted or entered.
  protected date(): string {
    const inv = this.invoice();
    return inv.invoiceDate ?? inv.postedAtUtc ?? inv.createdAtUtc;
  }

  protected base(line: PurchaseInvoiceLine): string {
    return line.unit ? baseLabels[line.unit] : '';
  }
}
