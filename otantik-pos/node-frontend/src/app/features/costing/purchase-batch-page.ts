import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  CostingApi,
  PurchaseInvoice,
  PurchasePaymentMethod,
  paymentMethodLabels,
  paymentStatusLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { BusinessDay } from './financials';
import { InvoiceLine, InvoiceLines, InvoiceLinesTable, InvoiceSupplier, PurchaseCatalog } from './invoice-lines';

// One invoice of the batch: its id, made here; its supplier, number and lines, entered as on the
// invoice screen.
interface BatchBlock {
  id: string;
  supplier: InvoiceSupplier;
  number: string;
  editor: InvoiceLines;
}

// What is kept in the browser while a batch is being entered, so a reload does not lose it.
interface StoredBatch {
  invoiceDate: string;
  paymentMethod: PurchasePaymentMethod | null;
  revenueDate: string;
  blocks: { id: string; supplierId: string | null; supplierName: string | null; number: string; lines: Omit<InvoiceLine, 'entry'>[] }[];
}

const storageKey = 'otantik-till.purchase-batch';

// A day's purchases entered together (يومية المشتريات المجمعة): the date and how they were paid,
// chosen once, then an invoice for each supplier, each entered as on the invoice screen. Sent
// together, each becomes its own posted invoice, its stock in and paid as chosen: on credit owed
// to its supplier, in cash paid for a manager and asked of one for anyone else. None is saved
// unless all can be. A batch not sent yet is kept in this browser.
@Component({
  selector: 'app-purchase-batch-page',
  providers: [PurchaseCatalog],
  imports: [
    FormsModule,
    InvoiceLinesTable,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MoneyPipe,
    RouterLink,
  ],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      @if (posted(); as made) {
        <section class="posted" role="status">
          <h2>
            {{ made.length === 1 ? '1 invoice posted' : made.length + ' invoices posted' }}
            <span class="inline-ar">تم ترحيل الفواتير</span>
          </h2>
          <div class="table-wrap">
            <table class="grid">
              <thead>
                <tr>
                  <th>Supplier<span class="ar">المورد</span></th>
                  <th>Invoice no.<span class="ar">رقم الفاتورة</span></th>
                  <th class="num">Total<span class="ar">الإجمالي</span></th>
                  <th>Payment<span class="ar">السداد</span></th>
                </tr>
              </thead>
              <tbody>
                @for (inv of made; track inv.id) {
                  <tr>
                    <td><a [routerLink]="['/costing/purchases/invoices', inv.id]">{{ inv.supplierName ?? 'No supplier' }}</a></td>
                    <td>{{ inv.supplierInvoiceNumber ?? '—' }}</td>
                    <td class="num">{{ inv.total | money }}</td>
                    <td>{{ statusLabels[inv.paymentStatus].en }}<span class="ar">{{ statusLabels[inv.paymentStatus].ar }}</span></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <button mat-button (click)="posted.set(null)">Done</button>
        </section>
      }

      <p class="note">
        A day's purchases from several suppliers, entered together: the date and how they were paid once, then an invoice for each
        supplier. Each is posted as its own invoice, on its supplier's account.
        <span class="inline-ar">يومية المشتريات المجمعة: كل مورد بفاتورة مستقلة.</span>
      </p>
      <div class="header">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Invoice date · تاريخ الفواتير</mat-label>
          <input matInput type="date" [(ngModel)]="invoiceDate" (ngModelChange)="changed()" aria-label="Invoice date" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="payment">
          <mat-label>Paid · طريقة الدفع</mat-label>
          <mat-select [(ngModel)]="paymentMethod" (ngModelChange)="changed()" aria-label="How they are paid" required>
            @for (m of paymentMethods; track m) {
              <mat-option [value]="m">{{ paymentLabels[m].en }} · {{ paymentLabels[m].ar }}</mat-option>
            }
          </mat-select>
          @if (paymentMethod === 'Credit') {
            <mat-hint>Each owed to its supplier until paid by a payment voucher.</mat-hint>
          } @else if (!paymentMethod) {
            <mat-hint>Required to post them.</mat-hint>
          }
        </mat-form-field>
        @if (paymentMethod === 'DayRevenue') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Takings of · إيراد يوم</mat-label>
            <input matInput type="date" [(ngModel)]="revenueDate" (ngModelChange)="changed()" [max]="today" aria-label="Takings of" />
          </mat-form-field>
        }
      </div>
      @if (!approvals.canApprove() && paymentMethod && paymentMethod !== 'Credit') {
        <p class="ask">Each invoice's cash payment goes to a manager for approval; the stock is in at once.</p>
      }

      @for (block of blocks(); track block.id; let i = $index) {
        <section class="block" [attr.aria-label]="'Invoice ' + (i + 1)">
          <div class="block-head">
            <h3>Invoice {{ i + 1 }} <span class="inline-ar">فاتورة {{ i + 1 }}</span></h3>
            <mat-form-field subscriptSizing="dynamic" class="supplier">
              <mat-label>Supplier · المورد</mat-label>
              <input matInput [(ngModel)]="block.supplier.entry" (ngModelChange)="block.supplier.entered($event); changed()" [matAutocomplete]="suppliersList"
                placeholder="Choose or type a name" [attr.aria-label]="'Invoice ' + (i + 1) + ', supplier'" />
              <mat-icon matSuffix>arrow_drop_down</mat-icon>
              <mat-autocomplete #suppliersList="matAutocomplete" [displayWith]="block.supplier.label">
                @for (s of block.supplier.matches(); track s.id) {
                  <mat-option [value]="s">{{ s.name }}</mat-option>
                }
                @if (block.supplier.typedNew(); as name) {
                  <mat-option [value]="{ addNew: name }" class="add-new"><mat-icon>add</mat-icon> Add new supplier: "{{ name }}"</mat-option>
                }
              </mat-autocomplete>
              @if (block.supplier.unresolved()) {
                <mat-hint class="warn-text">Choose the supplier from the list, or add it.</mat-hint>
              } @else if (paymentMethod === 'Credit' && !block.supplier.id) {
                <mat-hint class="warn-text">On credit, it is owed to a supplier: choose one.</mat-hint>
              }
            </mat-form-field>
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Invoice no. · رقم الفاتورة</mat-label>
              <input matInput [(ngModel)]="block.number" (ngModelChange)="changed()" maxlength="64" [attr.aria-label]="'Invoice ' + (i + 1) + ', number'" />
            </mat-form-field>
            <span class="spacer"></span>
            <strong class="block-total">{{ block.editor.total() | money }}</strong>
            <button mat-icon-button (click)="removeBlock(i)" [disabled]="blocks().length === 1" [attr.aria-label]="'Take invoice ' + (i + 1) + ' off the batch'">
              <mat-icon>delete</mat-icon>
            </button>
          </div>
          <app-invoice-lines [editor]="block.editor" [label]="'Invoice ' + (i + 1) + ', '" />
        </section>
      }

      <div class="toolbar actions">
        <button mat-stroked-button (click)="addBlock()"><mat-icon>add</mat-icon> Add another invoice <span class="inline-ar">فاتورة أخرى</span></button>
        <span class="spacer"></span>
        <span class="batch-total">{{ blocks().length }} {{ blocks().length === 1 ? 'invoice' : 'invoices' }} · <strong>{{ total() | money }}</strong></span>
        <button mat-button (click)="startAgain()" [disabled]="saving()">Start again</button>
        <button mat-flat-button (click)="submit()" [disabled]="!ready() || saving()">
          <mat-icon>inventory</mat-icon> Submit batch <span class="inline-ar">ترحيل اليومية</span>
        </button>
      </div>
      @if (saving()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (problems(); as p) {
        <p class="error problems" role="alert">{{ p }}</p>
      }
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 12px;
      margin-bottom: 12px;
    }
    .payment {
      min-width: 230px;
    }
    .block {
      margin: 0 0 16px;
      padding: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 12px;
    }
    .block-head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 12px;
      margin-bottom: 8px;

      h3 {
        margin: 0;
        font: var(--mat-sys-title-small);
      }
    }
    .supplier {
      min-width: 240px;
    }
    .block-total,
    .batch-total strong {
      font: var(--mat-sys-title-medium);
    }
    .add-new {
      color: var(--mat-sys-primary);
      font-weight: 500;
    }
    .warn-text {
      color: var(--mat-sys-error);
    }
    .actions {
      align-items: center;
      margin-top: 4px;
    }
    .problems {
      white-space: pre-line;
    }
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    .posted {
      margin-bottom: 16px;
      padding: 12px;
      border-radius: 12px;
      background: var(--mat-sys-secondary-container);

      h2 {
        margin: 0 0 8px;
        font: var(--mat-sys-title-medium);
      }
    }
  `,
})
export class PurchaseBatchPage {
  private readonly api = inject(CostingApi);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  private readonly catalog = inject(PurchaseCatalog);
  protected readonly approvals = inject(Approvals);

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  // What stopped the batch, each problem by its invoice; nothing was saved.
  protected readonly problems = signal<string | null>(null);
  // The invoices the last batch made.
  protected readonly posted = signal<PurchaseInvoice[] | null>(null);
  protected readonly blocks = signal<BatchBlock[]>([this.block()]);

  protected invoiceDate = PurchaseBatchPage.today();
  protected paymentMethod: PurchasePaymentMethod | null = null;
  protected revenueDate = PurchaseBatchPage.today();
  protected today = PurchaseBatchPage.today();
  protected readonly paymentMethods: PurchasePaymentMethod[] = ['Safe', 'DayRevenue', 'Credit'];
  protected readonly paymentLabels = paymentMethodLabels;
  protected readonly statusLabels = paymentStatusLabels;

  // The header's fields are plain; this bumps so what reads them follows.
  private readonly headerVersion = signal(0);

  protected readonly total = computed(() => this.blocks().reduce((sum, b) => sum + b.editor.total(), 0));

  // Every invoice ready as on the invoice screen, each with its supplier on credit; the payment
  // chosen, and from a day's takings, the day.
  protected readonly ready = computed(() => {
    this.headerVersion();
    const blocks = this.blocks();
    return (
      !!this.invoiceDate &&
      this.paymentMethod !== null &&
      (this.paymentMethod !== 'DayRevenue' || !!this.revenueDate) &&
      blocks.every((b) => {
        b.supplier.version();
        return b.editor.ready() && !b.supplier.unresolved() && (this.paymentMethod !== 'Credit' || b.supplier.id !== null);
      })
    );
  });

  constructor() {
    // Kept in this browser as it is typed, until it is sent.
    effect(() => {
      this.headerVersion();
      const blocks = this.blocks();
      blocks.forEach((b) => {
        b.editor.version();
        b.supplier.version();
      });
      if (!this.loading()) {
        this.store();
      }
    });
    void this.load();
  }

  protected changed(): void {
    this.headerVersion.update((v) => v + 1);
    this.problems.set(null);
  }

  protected addBlock(): void {
    this.blocks.update((blocks) => [...blocks, this.block()]);
  }

  protected removeBlock(index: number): void {
    this.blocks.update((blocks) => blocks.filter((_, i) => i !== index));
  }

  protected startAgain(): void {
    this.blocks.set([this.block()]);
    this.paymentMethod = null;
    this.invoiceDate = this.today;
    this.revenueDate = this.today;
    this.problems.set(null);
    this.changed();
  }

  // Sent together: each invoice made and posted, or none. Done, the batch starts again with new ids;
  // refused, it stays on screen with each problem said by its invoice.
  protected async submit(): Promise<void> {
    if (!this.ready() || !this.paymentMethod) {
      return;
    }
    this.saving.set(true);
    this.problems.set(null);
    try {
      const made = await firstValueFrom(
        this.api.postPurchaseBatch({
          invoiceDate: this.invoiceDate,
          paymentMethod: this.paymentMethod,
          revenueDate: this.paymentMethod === 'DayRevenue' ? this.revenueDate : null,
          invoices: this.blocks().map((b) => ({
            invoiceId: b.id,
            supplierId: b.supplier.id,
            supplierInvoiceNumber: b.number.trim() || null,
            lines: b.editor.toSave(),
          })),
        }),
      );
      this.posted.set(made);
      const pending = made.filter((i) => i.paymentStatus === 'Pending').length;
      this.notify.info(
        `${made.length === 1 ? '1 invoice' : `${made.length} invoices`} posted.` +
          (pending ? ` ${pending === 1 ? 'Its payment waits' : `${pending} payments wait`} for a manager's approval.` : ''),
      );
      this.blocks.set([this.block()]);
      this.changed();
      void this.approvals.refresh();
      await this.catalog.loadMaterials();
    } catch (error) {
      this.problems.set(errorMessage(error, 'The batch could not be posted.'));
    } finally {
      this.saving.set(false);
    }
  }

  private block(): BatchBlock {
    return { id: crypto.randomUUID(), supplier: new InvoiceSupplier(this.catalog), number: '', editor: new InvoiceLines(this.catalog) };
  }

  private store(): void {
    const batch: StoredBatch = {
      invoiceDate: this.invoiceDate,
      paymentMethod: this.paymentMethod,
      revenueDate: this.revenueDate,
      blocks: this.blocks().map((b) => ({
        id: b.id,
        supplierId: b.supplier.id,
        supplierName: typeof b.supplier.entry === 'string' ? b.supplier.entry : null,
        number: b.number,
        lines: b.editor.lines().map(({ entry, ...line }) => ({ ...line, ...(typeof entry === 'string' ? { typed: entry } : {}) })),
      })),
    };
    try {
      const empty = batch.blocks.length === 1 && !batch.blocks[0].supplierId && batch.blocks[0].lines.every((l) => !l.rawMaterialId && !l.expenseItemId);
      if (empty && !batch.paymentMethod) {
        localStorage.removeItem(storageKey);
      } else {
        localStorage.setItem(storageKey, JSON.stringify(batch));
      }
    } catch {
      // Storage refused: the batch is only on screen.
    }
  }

  // A batch left unsent in this browser, back on screen as it was.
  private restore(): void {
    let stored: StoredBatch | null = null;
    try {
      stored = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as StoredBatch | null;
    } catch {
      stored = null;
    }
    if (!stored?.blocks?.length) {
      return;
    }
    this.invoiceDate = stored.invoiceDate || this.today;
    this.paymentMethod = stored.paymentMethod;
    this.revenueDate = stored.revenueDate || this.today;
    this.blocks.set(
      stored.blocks.map((b) => {
        const block = this.block();
        block.id = b.id;
        block.number = b.number ?? '';
        if (b.supplierId) {
          block.supplier.set(b.supplierId, null);
        } else if (b.supplierName) {
          block.supplier.entry = b.supplierName;
        }
        block.editor.lines.set(
          b.lines.map((l) => {
            const { typed, ...line } = l as Omit<InvoiceLine, 'entry'> & { typed?: string };
            const entry =
              (line.rawMaterialId && this.catalog.materials().find((m) => m.id === line.rawMaterialId)) ||
              (line.expenseItemId && this.catalog.expenses().find((e) => e.id === line.expenseItemId)) ||
              typed;
            return { ...line, entry };
          }),
        );
        block.editor.touch();
        return block;
      }),
    );
  }

  private async load(): Promise<void> {
    void this.businessDay.today().then((today) => {
      // Not yet chosen: the business day.
      if (this.revenueDate === this.today) {
        this.revenueDate = today;
      }
      if (this.invoiceDate === this.today) {
        this.invoiceDate = today;
      }
      this.today = today;
      this.changed();
    });
    try {
      await this.catalog.load();
      this.restore();
    } catch (error) {
      this.error.set(errorMessage(error, 'The purchases could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private static today(): string {
    const now = new Date();
    return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  }
}
