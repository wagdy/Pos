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
import { CostingApi, ExpenseItem } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { ExpenseItemDialog, ExpenseItemDialogData } from './expense-item-dialog';

// The expense items (المصروفات العمومية): what is bought but never stock (cleaning supplies,
// repairs, bills), each under a category that says where it counts in the profit and loss. They
// never appear on the stock list or in recipes. One on an invoice is archived rather than
// deleted, as a supplier is; either way it is asked first, in the row.
@Component({
  selector: 'app-expense-items-page',
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
      <p class="note">
        Expenses bought on purchase invoices beside the stock. Posted, they are only their cost: never counted, never in a recipe. Each
        category's line takes them off the month's profit on the KPIs and the break-even worksheet.
        <span class="inline-ar">بنود تُشترى ولا تدخل المخزون: تُسجل بتكلفتها فقط.</span>
      </p>
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
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add expense item</button>
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Expense item<span class="ar">بند المصروف</span></th>
              <th>Category<span class="ar">الفئة</span></th>
              <th>Counted in<span class="ar">الوحدة</span></th>
              <th class="num">Invoices<span class="ar">الفواتير</span></th>
              <th class="num">Spent<span class="ar">إجمالي المصروف</span></th>
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
                  @if (s.nameAr) {
                    <span class="ar">{{ s.nameAr }}</span>
                  }
                </td>
                <td>{{ s.category }}</td>
                <td class="nowrap">{{ s.unit ?? '—' }}</td>
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
                          Archive {{ s.name }}? It is on {{ s.invoices }} {{ s.invoices === 1 ? 'invoice' : 'invoices' }}, so it is kept for them,
                          and in the reports, and taken off the list for new ones. It can be restored.
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
                  {{ items().length ? 'No expense item matches.' : 'No expense items yet. Add one here, such as Electricity or Cleaning supplies, or type a new name on an invoice.' }}
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
export class ExpenseItemsPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  // All of them, archived too; the archived are shown only when asked for.
  protected readonly items = signal<ExpenseItem[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly showArchived = signal(false);
  // The item whose delete is being asked about.
  protected readonly confirming = signal<string | null>(null);

  protected readonly shown = computed(() => {
    const text = this.search().trim().toLowerCase();
    return this.items().filter(
      (s) => (this.showArchived() || !s.isArchived) && (!text || [s.name, s.nameAr, s.category, s.unit].some((v) => v?.toLowerCase().includes(text))),
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

  protected async edit(item: ExpenseItem): Promise<void> {
    if (await this.open({ item })) {
      await this.load();
    }
  }

  protected async remove(item: ExpenseItem): Promise<void> {
    this.saving.set(true);
    try {
      const { outcome } = await firstValueFrom(this.api.deleteExpenseItem(item.id));
      this.notify.info(
        outcome === 'Archived' ? `${item.name} is archived: kept on its invoices and in the reports, off the list for new ones.` : `${item.name} is deleted.`,
      );
      this.confirming.set(null);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The expense item could not be removed.');
    } finally {
      this.saving.set(false);
    }
  }

  protected async restore(item: ExpenseItem): Promise<void> {
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.restoreExpenseItem(item.id));
      this.notify.info(`${item.name} is back on the list.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The expense item could not be restored.');
    } finally {
      this.saving.set(false);
    }
  }

  private async open(data: ExpenseItemDialogData): Promise<ExpenseItem | undefined> {
    return firstValueFrom(
      this.dialog.open<ExpenseItemDialog, ExpenseItemDialogData, ExpenseItem>(ExpenseItemDialog, { data, width: '480px' }).afterClosed(),
    );
  }

  private async load(): Promise<void> {
    try {
      this.items.set(await firstValueFrom(this.api.expenseItems(true)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The expense items could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
