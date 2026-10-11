import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PurchaseInvoice } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { InvoiceView } from './invoice-view';

// One invoice on a page of its own, read-only, for a link to it. A posted invoice is not
// changed; a mistake is put right with a stock count.
@Component({
  selector: 'app-invoice-page',
  imports: [InvoiceView, MatButtonModule, MatIconModule, MatProgressBarModule, RouterLink],
  template: `
    <div class="toolbar no-print">
      <a mat-button routerLink="/costing/purchases/invoices"><mat-icon>arrow_back</mat-icon> All invoices</a>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (invoice(); as inv) {
      <h2 class="title">
        {{ inv.supplierName ?? 'Invoice' }}{{ inv.supplierInvoiceNumber ? ' · ' + inv.supplierInvoiceNumber : '' }}
        <span class="inline-ar">فاتورة شراء</span>
      </h2>
      @if (inv.status === 'Draft') {
        <p><a routerLink="/costing/purchases/new">Finish it on New invoice</a>.</p>
      }
      <app-invoice-view [invoice]="inv" />
    } @else {
      <p class="error">No such invoice. It may have been a draft that was discarded.</p>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .title {
      margin: 0 0 8px;
      font: var(--mat-sys-title-large);
    }
  `,
})
export class InvoicePage {
  private readonly api = inject(CostingApi);

  readonly id = input.required<string>();

  protected readonly invoice = signal<PurchaseInvoice | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  private async load(id: string): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.invoice.set(await firstValueFrom(this.api.purchaseInvoice(id)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The invoice could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
