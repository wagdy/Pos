import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PurchaseInvoice } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { InvoiceView } from './invoice-view';

export interface InvoiceViewData {
  invoiceId: string;
}

// The history's Show: one invoice, read-only, over the list, so the place in it is kept.
@Component({
  selector: 'app-invoice-view-dialog',
  imports: [InvoiceView, MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule, RouterLink],
  template: `
    <h2 mat-dialog-title>
      {{ invoice()?.supplierName ?? 'Purchase invoice' }}{{ invoice()?.supplierInvoiceNumber ? ' · ' + invoice()!.supplierInvoiceNumber : '' }}
      <span class="inline-ar">عرض تفاصيل الفاتورة</span>
    </h2>
    <mat-dialog-content>
      @if (error()) {
        <p class="error">{{ error() }}</p>
      } @else if (invoice(); as inv) {
        <app-invoice-view [invoice]="inv" />
      } @else if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      } @else {
        <p class="error">No such invoice. It may have been a draft that was discarded.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <a mat-button [routerLink]="['/costing/purchases/invoices', data.invoiceId]" mat-dialog-close><mat-icon>open_in_full</mat-icon> Open as a page</a>
      <button mat-flat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class InvoiceViewDialog {
  private readonly api = inject(CostingApi);
  protected readonly data = inject<InvoiceViewData>(MAT_DIALOG_DATA);

  protected readonly invoice = signal<PurchaseInvoice | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.invoice.set(await firstValueFrom(this.api.purchaseInvoice(this.data.invoiceId)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The invoice could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}

// Focus on the dialog itself: on its first button or the paper's viewer, it would open
// scrolled past the supplier and the lines.
export function showInvoice(dialog: MatDialog, invoiceId: string): void {
  dialog.open<InvoiceViewDialog, InvoiceViewData>(InvoiceViewDialog, {
    data: { invoiceId },
    width: '1000px',
    maxWidth: '95vw',
    autoFocus: 'dialog',
  });
}
