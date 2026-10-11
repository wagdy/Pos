import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CostingApi, Supplier } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { SupplierDialog, SupplierDialogData } from './supplier-dialog';

// The suppliers: added, changed, and removed. One on an invoice is archived rather than deleted:
// it leaves the list new invoices choose from and stays on the invoices it is on, and can be
// restored. One never invoiced is deleted for good. Either way it is asked first, in the row.
@Component({
  selector: 'app-suppliers-page',
  imports: [
    DatePipe,
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MoneyPipe,
  ],
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
        <mat-checkbox [ngModel]="showArchived()" (ngModelChange)="showArchived.set($event)">
          Show archived <span class="inline-ar">المؤرشفة</span>
        </mat-checkbox>
        <span class="spacer"></span>
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add supplier</button>
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Supplier<span class="ar">المورد</span></th>
              <th>Contact<span class="ar">المسؤول</span></th>
              <th>Phone<span class="ar">الهاتف</span></th>
              <th class="num">Invoices<span class="ar">الفواتير</span></th>
              <th class="num">Bought<span class="ar">إجمالي المشتريات</span></th>
              <th>Last invoice<span class="ar">آخر فاتورة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (s of shown(); track s.id) {
              <tr [class.archived]="s.isArchived">
                <td>
                  {{ s.name }}
                  @if (s.isArchived) {
                    <span class="badge">Archived · مؤرشف</span>
                  }
                  @if (s.email) {
                    <div class="muted small">{{ s.email }}</div>
                  }
                </td>
                <td>{{ s.contactPerson ?? '—' }}</td>
                <td class="nowrap">{{ s.phone ?? '—' }}</td>
                <td class="num">{{ s.invoices }}</td>
                <td class="num">{{ s.postedTotal | money }}</td>
                <td class="nowrap">{{ s.lastPostedAtUtc ? (s.lastPostedAtUtc | date: 'd MMM y') : '—' }}</td>
                <td class="num actions">
                  @if (s.isArchived) {
                    <button mat-button (click)="restore(s)" [disabled]="saving()"><mat-icon>unarchive</mat-icon> Restore</button>
                  } @else {
                    <button mat-icon-button (click)="edit(s)" [attr.aria-label]="'Edit ' + s.name"><mat-icon>edit</mat-icon></button>
                    <button mat-icon-button class="danger" (click)="confirming.set(s.id)" [disabled]="confirming() === s.id"
                      [attr.aria-label]="'Delete ' + s.name">
                      <mat-icon>delete</mat-icon>
                    </button>
                  }
                </td>
              </tr>
              @if (confirming() === s.id) {
                <tr class="confirm-row">
                  <td colspan="7">
                    <div class="confirm">
                      <span class="ask">
                        @if (s.invoices) {
                          Archive {{ s.name }}? It is on {{ s.invoices }} {{ s.invoices === 1 ? 'invoice' : 'invoices' }}, so it is kept for them
                          and taken off the list for new ones. It can be restored.
                        } @else {
                          Delete {{ s.name }} for good? It is on no invoice.
                        }
                      </span>
                      <span class="spacer"></span>
                      <button mat-button (click)="confirming.set(null)" [disabled]="saving()">Cancel</button>
                      <button mat-flat-button class="danger-fill" (click)="remove(s)" [disabled]="saving()">
                        {{ s.invoices ? 'Archive' : 'Delete' }}
                      </button>
                    </div>
                  </td>
                </tr>
              }
            } @empty {
              <tr>
                <td colspan="7" class="muted">
                  {{ suppliers().length ? 'No supplier matches.' : 'No suppliers yet. Add one here, or type a new name on an invoice.' }}
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
      min-width: 240px;
    }
    .small {
      font-size: 0.85em;
    }
    .badge {
      margin-left: 6px;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
    tr.archived td {
      color: var(--mat-sys-on-surface-variant);
    }
    .actions,
    .nowrap {
      white-space: nowrap;
    }
    tr.confirm-row td {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .confirm {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .spacer {
      flex: 1;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .danger-fill:not(:disabled) {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class SuppliersPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  // All of them, archived too; the archived are shown only when asked for.
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly showArchived = signal(false);
  // The supplier whose delete is being asked about.
  protected readonly confirming = signal<string | null>(null);

  protected readonly shown = computed(() => {
    const text = this.search().trim().toLowerCase();
    return this.suppliers().filter(
      (s) =>
        (this.showArchived() || !s.isArchived) &&
        (!text || [s.name, s.contactPerson, s.phone, s.email].some((v) => v?.toLowerCase().includes(text))),
    );
  });

  constructor() {
    void this.load();
  }

  protected async add(): Promise<void> {
    if (await this.open({})) {
      await this.load();
    }
  }

  protected async edit(supplier: Supplier): Promise<void> {
    if (await this.open({ supplier })) {
      await this.load();
    }
  }

  protected async remove(supplier: Supplier): Promise<void> {
    this.saving.set(true);
    try {
      const { outcome } = await firstValueFrom(this.api.deleteSupplier(supplier.id));
      this.notify.info(
        outcome === 'Archived'
          ? `${supplier.name} is archived: kept on its invoices, off the list for new ones.`
          : `${supplier.name} is deleted.`,
      );
      this.confirming.set(null);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The supplier could not be removed.');
    } finally {
      this.saving.set(false);
    }
  }

  protected async restore(supplier: Supplier): Promise<void> {
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.restoreSupplier(supplier.id));
      this.notify.info(`${supplier.name} is back on the list.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The supplier could not be restored.');
    } finally {
      this.saving.set(false);
    }
  }

  private async open(data: SupplierDialogData): Promise<Supplier | undefined> {
    return firstValueFrom(
      this.dialog.open<SupplierDialog, SupplierDialogData, Supplier>(SupplierDialog, { data, width: '520px' }).afterClosed(),
    );
  }

  private async load(): Promise<void> {
    try {
      this.suppliers.set(await firstValueFrom(this.api.suppliers(true)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The suppliers could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
