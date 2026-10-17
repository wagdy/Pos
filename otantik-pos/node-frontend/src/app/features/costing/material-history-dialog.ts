import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, MaterialCost, MaterialPurchase, MaterialPurchaseHistory, unitLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';

// A material's purchase history card (كارت الصنف): every invoice line that bought it, newest first,
// as its paper has it, to hold against the paper invoices when a count looks wrong. A voided
// invoice is shown, struck through and left out of the totals, with the one that corrected it.
// A row opens its invoice in the archive.
@Component({
  selector: 'app-material-history-dialog',
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>{{ data.name }} · Purchase history <span class="inline-ar">كارت الصنف</span></h2>
    <mat-dialog-content>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      } @else if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      } @else if (history(); as h) {
        @if (!h.lines.length) {
          <p class="muted">No invoice has bought {{ h.material }} yet.</p>
        } @else {
          <div class="table-wrap">
            <table class="grid">
              <thead>
                <tr>
                  <th>Date<span class="ar">تاريخ الفاتورة</span></th>
                  <th>Supplier<span class="ar">المورد</span></th>
                  <th>Invoice no.<span class="ar">رقم الفاتورة</span></th>
                  <th class="num">Quantity bought<span class="ar">الكمية المشتراة</span></th>
                  <th class="num">Unit price<span class="ar">سعر الوحدة</span></th>
                  <th class="num">Line total<span class="ar">إجمالي السطر</span></th>
                  <th><span class="cdk-visually-hidden">Invoice</span></th>
                </tr>
              </thead>
              <tbody>
                @for (l of h.lines; track $index) {
                  <tr class="clickable" [class.voided]="l.voided" (click)="open(l.invoiceId)">
                    <td class="date">{{ l.date | date: 'd MMM y' }}</td>
                    <td>{{ l.supplier ?? '—' }}</td>
                    <td>
                      {{ l.invoiceNumber ?? '—' }}
                      @if (l.voided) {
                        <span class="badge void">Voided · ملغاة</span>
                        @if (l.replacedBy; as next) {
                          <button mat-button class="link" (click)="open(next); $event.stopPropagation()">Corrected by a new invoice</button>
                        }
                      } @else if (l.revisionOf) {
                        <span class="badge">Correction · تعديل</span>
                      }
                    </td>
                    <td class="num">
                      <span class="amount">{{ l.quantity | number: '1.0-3' }} {{ l.unit }}</span>
                      @if (l.baseQuantity !== l.quantity) {
                        <small class="muted">{{ l.baseQuantity | number: '1.0-3' }} {{ base(h) }}</small>
                      }
                    </td>
                    <td class="num amount">{{ l.unitPrice === null ? '' : 'L.E ' + (l.unitPrice | number: '1.2-4') + ' / ' + l.unit }}</td>
                    <td class="num amount">
                      @if (l.total === null) {
                        <span class="muted">No price</span>
                      } @else {
                        {{ l.total | money }}
                      }
                    </td>
                    <td>
                      <button mat-icon-button (click)="open(l.invoiceId); $event.stopPropagation()" [attr.aria-label]="'View invoice ' + (l.invoiceNumber ?? '') + ' of ' + (l.date | date: 'd MMM y')">
                        <mat-icon>open_in_new</mat-icon>
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td colspan="3">Standing invoices · الفواتير السارية</td>
                  <td class="num">
                    {{ h.baseQuantity / h.purchaseUnitSize | number: '1.0-3' }} {{ h.purchaseUnit }}
                    @if (h.purchaseUnitSize !== 1) {
                      <small class="muted">{{ h.baseQuantity | number: '1.0-3' }} {{ base(h) }}</small>
                    }
                  </td>
                  <td class="num">
                    @if (h.averagePerPurchaseUnit !== null) {
                      Average {{ h.averagePerPurchaseUnit | money }} / {{ h.purchaseUnit }}
                    }
                  </td>
                  <td class="num">{{ h.total | money }}</td>
                  <td></td>
                </tr>
              </tfoot>
            </table>
          </div>
          @if (notes(h); as n) {
            <p class="muted small">{{ n }}</p>
          }
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styleUrl: './costing.scss',
  styles: `
    .date {
      white-space: nowrap;
    }
    td.num small {
      display: block;
    }
    tr.voided .amount {
      text-decoration: line-through;
      color: var(--mat-sys-on-surface-variant);
    }
    .badge {
      margin-inline-start: 6px;
    }
    .badge.void {
      background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
      color: var(--mat-sys-error);
    }
    .link {
      display: block;
      margin-top: 2px;
      padding: 0;
      min-width: 0;
      height: auto;
    }
    tfoot td {
      font-weight: 500;
    }
  `,
})
export class MaterialHistoryDialog {
  protected readonly data = inject<MaterialCost>(MAT_DIALOG_DATA);
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<MaterialHistoryDialog>);
  private readonly router = inject(Router);

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly history = signal<MaterialPurchaseHistory | null>(null);

  constructor() {
    void this.load();
  }

  protected base(history: MaterialPurchaseHistory): string {
    return unitLabels[history.unit];
  }

  // What the totals leave out: lines with no price yet, and voided invoices.
  protected notes(history: MaterialPurchaseHistory): string | null {
    const voided = history.lines.filter((l: MaterialPurchase) => l.voided).length;
    const parts = [
      history.unpricedLines ? `${history.unpricedLines === 1 ? '1 line has' : `${history.unpricedLines} lines have`} no price, so the average leaves it out` : '',
      voided ? `${voided === 1 ? '1 voided invoice is' : `${voided} voided invoices are`} shown for the paper, and not counted` : '',
    ].filter(Boolean);
    return parts.length ? `${parts.join('. ')}.` : null;
  }

  // The invoice in the archive, as it was entered and paid.
  protected open(invoiceId: string): void {
    this.ref.close();
    void this.router.navigate(['/costing/purchases/invoices', invoiceId]);
  }

  private async load(): Promise<void> {
    try {
      this.history.set(await firstValueFrom(this.api.materialPurchases(this.data.id)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The purchase history could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
