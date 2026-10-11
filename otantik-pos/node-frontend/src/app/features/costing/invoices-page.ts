import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PurchaseInvoiceSummary } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { viewInvoicePaper } from './invoice-paper';
import { showInvoice } from './invoice-view-dialog';

// Every posted supplier invoice, newest first: what came in, from whom, for how much, who
// entered it, and whether the paper is on file. Show (or the row) opens the invoice as it was
// posted, read-only, over the list.
@Component({
  selector: 'app-invoices-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        <mat-form-field subscriptSizing="dynamic" class="search">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search · بحث</mat-label>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
        </mat-form-field>
        <span class="spacer"></span>
        <span class="muted">{{ shown().length }} {{ shown().length === 1 ? 'invoice' : 'invoices' }} · {{ shownTotal() | money }}</span>
        <a mat-flat-button routerLink="/costing/purchases/new"><mat-icon>add</mat-icon> New invoice</a>
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Supplier<span class="ar">المورد</span></th>
              <th>Invoice no.<span class="ar">رقم الفاتورة</span></th>
              <th class="num">Total<span class="ar">الإجمالي</span></th>
              <th>Created by<span class="ar">أنشأها</span></th>
              <th class="num">Paper<span class="ar">الصورة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (inv of shown(); track inv.id) {
              <tr class="clickable" (click)="open(inv)">
                <td class="nowrap">{{ date(inv) | date: 'd MMM y' }}</td>
                <td>{{ inv.supplierName ?? '—' }}</td>
                <td>{{ inv.supplierInvoiceNumber ?? '—' }}</td>
                <td class="num">
                  {{ inv.total | money }}
                  @if (inv.expenseTotal > 0) {
                    <small class="muted expenses">{{ inv.expenseTotal === inv.total ? 'all expenses' : 'incl. ' + (inv.expenseTotal | money) + ' expenses' }}</small>
                  }
                </td>
                <td>{{ inv.createdBy || '—' }}</td>
                <td class="num">
                  @if (inv.attachmentFileName; as fileName) {
                    <button mat-icon-button (click)="$event.stopPropagation(); viewPaper(inv.id, fileName)" [attr.aria-label]="'View ' + fileName">
                      <mat-icon>attach_file</mat-icon>
                    </button>
                  } @else {
                    <span class="muted" aria-label="No paper">—</span>
                  }
                </td>
                <td class="num">
                  <button mat-stroked-button class="show" (click)="$event.stopPropagation(); open(inv)">
                    <mat-icon>visibility</mat-icon> Show · عرض
                  </button>
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="muted">
                  {{ invoices().length ? 'No invoice matches.' : 'No invoices posted yet. They are entered under New invoice.' }}
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
    .search {
      min-width: 280px;
    }
    .show,
    .nowrap {
      white-space: nowrap;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
    .expenses {
      display: block;
    }
  `,
})
export class InvoicesPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);

  protected readonly invoices = signal<PurchaseInvoiceSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');

  protected readonly shown = computed(() => {
    const text = this.search().trim().toLowerCase();
    return this.invoices().filter(
      (i) => !text || (i.supplierName ?? '').toLowerCase().includes(text) || (i.supplierInvoiceNumber ?? '').toLowerCase().includes(text),
    );
  });

  protected readonly shownTotal = computed(() => this.shown().reduce((sum, i) => sum + i.total, 0));

  constructor() {
    void this.load();
  }

  // The date on the supplier's paper; when none was written, the day it was posted.
  protected date(invoice: PurchaseInvoiceSummary): string {
    return invoice.invoiceDate ?? invoice.postedAtUtc ?? invoice.createdAtUtc;
  }

  protected open(invoice: PurchaseInvoiceSummary): void {
    showInvoice(this.dialog, invoice.id);
  }

  protected viewPaper(invoiceId: string, fileName: string): void {
    viewInvoicePaper(this.dialog, invoiceId, fileName);
  }

  private async load(): Promise<void> {
    try {
      this.invoices.set(await firstValueFrom(this.api.purchaseInvoices('Posted')));
    } catch (error) {
      this.error.set(errorMessage(error, 'The invoices could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
