import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, PrepImportResult, PrepItem } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from './approvals';
import { PrepImportDialog } from './prep-import-dialog';

// The prep items: made in the kitchen from their own recipes, kept in stock, used by dishes.
// Each with what it costs by its recipe at today's prices, what the batches on the shelf cost,
// and how much is on the shelf. A row opens its profile. New ones asked for by an accountant are
// listed first, waiting for a manager: they are not prep items until approved. Many at once from a
// sheet of batch recipes (المصنعات): each prep item's name on a row of its own, then its ingredients.
@Component({
  selector: 'app-prep-items-page',
  imports: [DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    <p class="note">
      Made here from other materials (dough, house sauces) and kept in stock like them. A dish's recipe uses the prep item, so a sale
      takes dough, not flour; a production order turns the flour into dough.
      <span class="inline-ar">تُصنع في المطبخ وتُخزن، وتستهلكها الأطباق.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        <mat-form-field subscriptSizing="dynamic">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search · بحث</mat-label>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
        </mat-form-field>
        <span class="spacer"></span>
        <a mat-button routerLink="../production"><mat-icon>precision_manufacturing</mat-icon> Make a batch</a>
        <button mat-stroked-button (click)="picker.click()" [disabled]="busy()">
          <mat-icon>upload_file</mat-icon> Import from Excel<span class="inline-ar">استيراد</span>
        </button>
        <input #picker type="file" hidden accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
          (change)="importFile(picker)" aria-label="Excel sheet of batch recipes to import" />
        <a mat-flat-button routerLink="new"><mat-icon>add</mat-icon> Add prep item</a>
      </div>
      @if (busy()) {
        <mat-progress-bar mode="indeterminate" />
      }
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Prep item<span class="ar">الصنف</span></th>
              <th class="num">One batch<span class="ar">الدفعة</span></th>
              <th class="num">By its recipe<span class="ar">التكلفة المعيارية</span></th>
              <th class="num">In stock at<span class="ar">متوسط التكلفة</span></th>
              <th class="num">On hand<span class="ar">الرصيد</span></th>
              <th class="num">Stock value<span class="ar">قيمة المخزون</span></th>
              <th>Last made<span class="ar">آخر تصنيع</span></th>
            </tr>
          </thead>
          <tbody>
            @for (r of waiting(); track r.id) {
              <tr class="pending-deletion">
                <td colspan="7">
                  {{ r.summary }}
                  <div>
                    <span class="pending-badge">{{ pendingLabel(r).en }}<span class="ar">{{ pendingLabel(r).ar }}</span></span>
                    <span class="muted small"> asked by {{ r.requestedBy }}, {{ r.requestedAtUtc | date: 'd MMM, HH:mm' }}</span>
                  </div>
                </td>
              </tr>
            }
            @for (p of shown(); track p.id) {
              @let asked = approvals.pendingFor(p.id);
              <tr class="clickable" [class.pending-deletion]="!!asked" (click)="open(p)">
                <td>
                  @if (p.code) {
                    <span class="muted">{{ p.code }} · </span>
                  }
                  {{ p.name }}
                  @if (asked) {
                    <div>
                      <span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span>
                      <span class="muted small"> by {{ asked.requestedBy }}</span>
                    </div>
                  }
                  @if (p.ingredients.length === 0) {
                    <div class="warn small">No recipe yet</div>
                  } @else if (p.missingPrices.length) {
                    <div class="warn small">No cost yet: {{ p.missingPrices.join(', ') }}</div>
                  }
                </td>
                <td class="num">{{ p.batchQuantity / p.displayUnitSize | number: '1.0-3' }} {{ p.displayUnit }}</td>
                <td class="num">{{ p.standardCostPerDisplayUnit === null ? '—' : (p.standardCostPerDisplayUnit | money) + ' / ' + p.displayUnit }}</td>
                <td class="num">{{ p.averageCostPerDisplayUnit === null ? '—' : (p.averageCostPerDisplayUnit | money) + ' / ' + p.displayUnit }}</td>
                <td class="num" [class.warn]="p.quantityOnHand < 0">{{ p.quantityOnHand / p.displayUnitSize | number: '1.0-3' }} {{ p.displayUnit }}</td>
                <td class="num">{{ p.stockValue === null ? '—' : (p.stockValue | money) }}</td>
                <td class="nowrap">{{ p.lastProducedAtUtc ? (p.lastProducedAtUtc | date: 'd MMM, HH:mm') : 'Never' }}</td>
              </tr>
            } @empty {
              @if (!waiting().length) {
                <tr>
                  <td colspan="7" class="muted">
                    {{ items().length ? 'No prep item matches.' : 'No prep items yet. Add one, such as pizza dough, with its recipe for a batch.' }}
                  </td>
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .small {
      font-size: 0.85em;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class PrepItemsPage {
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  protected readonly items = signal<PrepItem[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly busy = signal(false);

  protected readonly shown = computed(() => {
    const text = this.search().trim().toLowerCase();
    return this.items().filter((p) => !text || p.name.toLowerCase().includes(text) || (p.code ?? '').toLowerCase().includes(text));
  });

  // New prep items asked for, waiting for a manager: not made yet, so not openable.
  protected readonly waiting = computed(() => this.approvals.waitingToBeMade('PrepItem'));

  constructor() {
    void this.load();
    void this.approvals.refresh();
  }

  protected open(item: PrepItem): void {
    void this.router.navigate(['/costing/recipes/prep', item.id]);
  }

  // The sheet chosen: imported, what it did shown, and the list read again. The picker is cleared,
  // so the same file can be chosen again once it is put right.
  protected async importFile(picker: HTMLInputElement): Promise<void> {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) {
      return;
    }
    this.busy.set(true);
    try {
      const result = await firstValueFrom(this.api.importPrepItems(file));
      this.dialog.open<PrepImportDialog, PrepImportResult>(PrepImportDialog, { data: result, width: '820px', maxWidth: '95vw' });
      await Promise.all([this.load(), this.approvals.refresh()]);
    } catch (error) {
      this.notify.error(error, 'The file could not be imported.');
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.items.set(await firstValueFrom(this.api.prepItems()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The prep items could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
