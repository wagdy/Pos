import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import {
  CostingApi,
  ExpenseItem,
  MaterialCost,
  PurchaseInvoice,
  PurchaseInvoiceSummary,
  SavePurchaseInvoice,
  Supplier,
  PurchasePaymentMethod,
  paymentMethodLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { BusinessDay } from './financials';
import { AddNew, InvoiceLine, InvoiceLines, InvoiceLinesTable, InvoiceSupplier, PurchaseCatalog } from './invoice-lines';
import { paperIcon, viewInvoicePaper } from './invoice-paper';
import { InvoiceView } from './invoice-view';

// A supplier's invoice, in two steps. Entered as it is written (who from, their number and date,
// each line in the unit billed, and a photo of the paper), then saved as a draft and reviewed;
// only "Confirm & post" takes the stock in and moves the average costs. A draft survives a
// reload, and can be finished on another tablet. A line is a raw material or an expense item
// (cleaning, a repair, a bill), on the same invoice or one of its own: an expense is only its
// cost, under its category, and never goes into stock.
@Component({
  selector: 'app-purchases-page',
  providers: [PurchaseCatalog],
  imports: [
    DatePipe,
    FormsModule,
    InvoiceLinesTable,
    InvoiceView,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MoneyPipe,
  ],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (review(); as inv) {
      <!-- Step two: the invoice as it will be posted. -->
      <section class="review">
        <h2>Review the invoice <span class="inline-ar">مراجعة الفاتورة</span></h2>
        <p class="note">Check it against the paper. Nothing is in stock until you confirm.</p>
        <app-invoice-view [invoice]="inv" />
        <div class="toolbar actions">
          <button mat-button (click)="backToEdit()" [disabled]="saving()"><mat-icon>edit</mat-icon> Back to edit</button>
          <span class="spacer"></span>
          <button mat-flat-button (click)="confirm()" [disabled]="saving()"><mat-icon>inventory</mat-icon> Confirm &amp; post</button>
        </div>
      </section>
    } @else {
      <!-- Step one: the invoice as written. -->
      @if (drafts().length) {
        <div class="drafts">
          <strong>Unfinished invoices · فواتير لم تكتمل</strong>
          @for (d of drafts(); track d.id) {
            <div class="draft">
              <span>{{ d.supplierName ?? 'No supplier' }} · {{ d.lines }} {{ d.lines === 1 ? 'line' : 'lines' }} · {{ d.total | money }}
                <span class="muted">· {{ d.createdBy }}, {{ d.createdAtUtc | date: 'd MMM, HH:mm' }}</span></span>
              <span class="spacer"></span>
              <button mat-button (click)="continueDraft(d.id)">Continue</button>
              <button mat-button class="danger" (click)="discardDraft(d.id)" [disabled]="saving()">Discard</button>
            </div>
          }
        </div>
      }
      <p class="note">
        Enter the invoice as it is written: how much came, in the unit it is billed in, and what each line cost. Stock is kept in
        grams, millilitres and pieces; the conversion is done for you. A line can also be an expense (cleaning, a repair, a bill):
        it is recorded at its cost and never goes into stock.
        <span class="inline-ar">أدخل الفاتورة كما هي مكتوبة.</span>
      </p>
      <div class="header">
        <mat-form-field subscriptSizing="dynamic" class="supplier">
          <mat-label>Supplier · المورد</mat-label>
          <input matInput [(ngModel)]="supplierEntry" (ngModelChange)="supplierEntered($event)" [matAutocomplete]="suppliersList"
            placeholder="Choose or type a name" aria-label="Supplier" />
          <mat-icon matSuffix>arrow_drop_down</mat-icon>
          <mat-autocomplete #suppliersList="matAutocomplete" [displayWith]="supplierLabel">
            @for (s of supplierMatches(); track s.id) {
              <mat-option [value]="s">{{ s.name }}</mat-option>
            }
            @if (typedNewSupplier(); as name) {
              <mat-option [value]="{ addNew: name }" class="add-new">
                <mat-icon>add</mat-icon> Add new supplier: "{{ name }}"
              </mat-option>
            }
          </mat-autocomplete>
          @if (supplierUnresolved()) {
            <mat-hint class="warn-text">Choose the supplier from the list, or add it.</mat-hint>
          }
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Invoice no. · رقم الفاتورة</mat-label>
          <input matInput [(ngModel)]="supplierInvoiceNumber" maxlength="64" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Invoice date · تاريخ الفاتورة</mat-label>
          <input matInput type="date" [(ngModel)]="invoiceDate" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="payment">
          <mat-label>Paid · طريقة الدفع</mat-label>
          <mat-select [(ngModel)]="paymentMethod" (ngModelChange)="touch()" aria-label="How it is paid" required>
            @for (m of paymentMethods; track m) {
              <mat-option [value]="m">{{ paymentLabels[m].en }} · {{ paymentLabels[m].ar }}</mat-option>
            }
          </mat-select>
          @if (paymentMethod === 'Credit' && !supplierId) {
            <mat-hint class="warn-text">On credit, it is owed to a supplier: choose one.</mat-hint>
          } @else if (paymentMethod === 'Credit') {
            <mat-hint>Owed to the supplier until paid by a payment voucher.</mat-hint>
          } @else if (!paymentMethod) {
            <mat-hint>Required to post it.</mat-hint>
          }
        </mat-form-field>
        @if (paymentMethod === 'DayRevenue') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Takings of · إيراد يوم</mat-label>
            <input matInput type="date" [(ngModel)]="revenueDate" (ngModelChange)="touch()" [max]="today" aria-label="Takings of" />
          </mat-form-field>
        }
        <div class="paper">
          <input #file type="file" accept="image/jpeg,image/png,image/webp,application/pdf" hidden (change)="attach(file)" />
          @if (paper(); as p) {
            <button mat-button (click)="viewPaper(invoiceId, p)"><mat-icon>{{ icon(p) }}</mat-icon> {{ p }}</button>
            <button mat-button (click)="file.click()" [disabled]="saving()">Replace</button>
          } @else {
            <button mat-stroked-button (click)="file.click()" [disabled]="saving()"><mat-icon>add_a_photo</mat-icon> Attach the paper</button>
          }
        </div>
      </div>
      @if (materials().length === 0 && expenses().length === 0) {
        <p class="note">No raw materials or expense items yet: type one's name on a line to add it.</p>
      }
      <app-invoice-lines [editor]="editor" />
      <div class="toolbar actions">
        <span class="spacer"></span>
        <button mat-button (click)="reset()" [disabled]="saving()">Start again</button>
        <button mat-flat-button (click)="save()" [disabled]="!ready() || saving()">
          <mat-icon>fact_check</mat-icon> Save &amp; review
        </button>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .material {
      min-width: 240px;
      width: 100%;
    }
    .add-new {
      color: var(--mat-sys-primary);
      font-weight: 500;
    }
    .small {
      width: 130px;
    }
    .quantity {
      display: flex;
      gap: 6px;
      justify-content: flex-end;
    }
    .unit {
      width: 110px;
    }
    .actions {
      margin-top: 12px;
    }
    .header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 12px;
      margin-bottom: 12px;
    }
    .supplier {
      min-width: 240px;
    }
    .payment {
      min-width: 230px;
    }
    .paper {
      display: flex;
      align-items: center;
      gap: 4px;
    }
    .drafts {
      display: grid;
      gap: 4px;
      padding: 10px 12px;
      margin-bottom: 12px;
      border-radius: 12px;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .draft {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .review h2 {
      margin: 0 0 4px;
      font: var(--mat-sys-title-large);
    }
    .warn-text {
      color: var(--mat-sys-error);
    }
    .badge.expense {
      margin: 4px 0 0;
      display: inline-block;
      background: color-mix(in srgb, var(--till-warn) 16%, transparent);
      color: var(--till-warn);
    }
    .category {
      white-space: nowrap;
    }
    .split {
      display: block;
      font-weight: 400;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class PurchasesPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);
  private readonly businessDay = inject(BusinessDay);
  private readonly catalog = inject(PurchaseCatalog);

  // The invoice's lines and its supplier, as entered: shared with the daily batch's invoices.
  protected readonly editor = new InvoiceLines(this.catalog);
  private readonly supplier = new InvoiceSupplier(this.catalog);

  protected readonly materials = this.catalog.materials;
  // The expense items new lines can name: the archived ones are not offered.
  protected readonly expenses = this.catalog.expenses;
  // The suppliers new invoices can name: the archived ones are not offered.
  protected readonly suppliers = this.catalog.suppliers;
  private readonly unfinished = signal<PurchaseInvoiceSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly lines = this.editor.lines;

  // The saved draft being reviewed; null while the invoice is being entered.
  protected readonly review = signal<PurchaseInvoice | null>(null);
  // The attached paper's name, once one is.
  protected readonly paper = signal<string | null>(null);

  // What the supplier field holds: text being typed, a supplier chosen, or one to add.
  protected get supplierEntry(): string | Supplier | AddNew | null {
    return this.supplier.entry;
  }
  protected set supplierEntry(value: string | Supplier | AddNew | null) {
    this.supplier.entry = value;
  }
  protected get supplierId(): string | null {
    return this.supplier.id;
  }
  protected set supplierId(id: string | null) {
    this.supplier.id = id;
  }
  protected supplierInvoiceNumber = '';
  protected invoiceDate: string = PurchasesPage.today();
  // How it is paid: required to post. From a day's takings names the day: the till's business
  // day to begin with (after midnight, still the night before), and none later.
  protected paymentMethod: PurchasePaymentMethod | null = null;
  protected revenueDate: string = PurchasesPage.today();
  protected today = PurchasesPage.today();
  protected readonly paymentMethods: PurchasePaymentMethod[] = ['Safe', 'DayRevenue', 'Credit'];
  protected readonly paymentLabels = paymentMethodLabels;

  // One invoice, one id, made here: the draft's, and the stock movements' when it is posted, so a
  // retry after a lost answer posts it once. A new one for each new invoice.
  private readonly currentId = signal<string>(crypto.randomUUID());
  protected get invoiceId(): string {
    return this.currentId();
  }
  protected set invoiceId(id: string) {
    this.currentId.set(id);
  }

  // The drafts waiting to be finished; the one on screen is not among them while it is open.
  protected readonly drafts = computed(() => this.unfinished().filter((d) => d.id !== this.currentId()));

  protected readonly total = this.editor.total;
  // The total split: what goes into stock, and what is expenses.
  protected readonly totals = this.editor.totals;

  // The lines ready; a supplier name typed but not chosen or added would be lost, so it holds the
  // save too; and the payment chosen with what it needs.
  protected readonly ready = computed(() => {
    this.supplier.version();
    return this.editor.ready() && !this.supplierUnresolved() && this.paymentReady();
  });

  constructor() {
    void this.load();
  }

  protected supplierMatches(): Supplier[] {
    return this.supplier.matches();
  }

  protected typedNewSupplier(): string | null {
    return this.supplier.typedNew();
  }

  protected readonly supplierLabel = this.supplier.label;

  protected supplierUnresolved(): boolean {
    return this.supplier.unresolved();
  }

  protected supplierEntered(value: string | Supplier | AddNew | null): void {
    this.supplier.entered(value);
    this.editor.touch();
  }

  protected async addSupplier(name: string): Promise<void> {
    await this.supplier.add(name);
    this.editor.touch();
  }

  protected material(line: InvoiceLine): MaterialCost | undefined {
    return this.editor.material(line);
  }

  protected expense(line: InvoiceLine): ExpenseItem | undefined {
    return this.editor.expense(line);
  }

  protected matches(line: InvoiceLine): { materials: MaterialCost[]; expenses: ExpenseItem[] } {
    return this.editor.matches(line);
  }

  protected typedNew(line: InvoiceLine): string | null {
    return this.editor.typedNew(line);
  }

  protected entered(line: InvoiceLine, value: string | MaterialCost | ExpenseItem | AddNew | null): void {
    this.editor.entered(line, value);
  }

  protected addExpense(line: InvoiceLine, name: string): Promise<void> {
    return this.editor.addExpense(line, name);
  }

  protected addMaterial(line: InvoiceLine, name: string): Promise<void> {
    return this.editor.addMaterial(line, name);
  }

  protected baseQuantity(line: InvoiceLine): number | null {
    return this.editor.baseQuantity(line);
  }

  protected stockAdded(line: InvoiceLine): string | null {
    return this.editor.stockAdded(line);
  }

  protected unitPrice(line: InvoiceLine): number | null {
    return this.editor.unitPrice(line);
  }

  protected touch(): void {
    this.editor.touch();
    this.supplier.touch();
  }

  protected add(): void {
    this.editor.add();
  }

  protected remove(index: number): void {
    this.editor.remove(index);
  }

  // Chosen, and what it needs: on credit, a supplier; from a day's takings, the day.
  protected paymentReady(): boolean {
    return (
      this.paymentMethod !== null &&
      (this.paymentMethod !== 'Credit' || this.supplierId !== null) &&
      (this.paymentMethod !== 'DayRevenue' || !!this.revenueDate)
    );
  }

  // A new, empty invoice. The one on screen stays a draft if it was saved.
  protected reset(): void {
    this.editor.reset();
    this.supplier.set(null);
    this.supplierInvoiceNumber = '';
    this.invoiceDate = PurchasesPage.today();
    this.paymentMethod = null;
    this.revenueDate = this.today;
    this.paper.set(null);
    this.review.set(null);
    this.invoiceId = crypto.randomUUID();
  }

  // Step one done: saved as a draft, and shown for review. No stock moves.
  protected async save(): Promise<void> {
    if (!this.ready()) {
      return;
    }
    this.saving.set(true);
    try {
      this.review.set(await firstValueFrom(this.api.savePurchaseInvoice(this.invoiceId, this.draft())));
    } catch (error) {
      this.notify.error(error, 'The invoice could not be saved.');
    } finally {
      this.saving.set(false);
    }
  }

  protected backToEdit(): void {
    this.review.set(null);
  }

  // Step two: the stock goes in, at the units each line comes to, and the average costs move.
  protected async confirm(): Promise<void> {
    this.saving.set(true);
    try {
      const posted = await firstValueFrom(this.api.postPurchaseInvoice(this.invoiceId));
      const stock = posted.lines.filter((l) => l.itemType === 'Inventory').length;
      const expenses = posted.lines.length - stock;
      const parts = [
        stock ? `${stock} ${stock === 1 ? 'line' : 'lines'} into stock` : null,
        expenses ? `${expenses} ${expenses === 1 ? 'expense' : 'expenses'} recorded` : null,
      ].filter(Boolean);
      const paid =
        posted.paymentStatus === 'Pending'
          ? ' Its payment waits for a manager’s approval: no cash moves until then.'
          : posted.paymentStatus === 'OnCredit'
            ? ` On credit: owed to ${posted.supplierName ?? 'the supplier'}.`
            : posted.paymentStatus === 'Paid' && posted.paymentMethod === 'DayRevenue'
              ? ' Paid from the day’s takings.'
              : posted.paymentStatus === 'Paid'
                ? ' Paid from the safe.'
                : '';
      this.notify.info(`Posted: ${parts.join(', ')}, ${posted.total.toFixed(2)} L.E.${paid}`);
      this.reset();
      void this.approvals.refresh();
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The invoice could not be posted.');
    } finally {
      this.saving.set(false);
    }
  }

  // The paper needs an invoice to belong to: the one on screen is saved as a draft first.
  protected async attach(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }
    if (file.size > 10 * 1024 * 1024) {
      this.notify.error(null, 'The file must be under 10 MB.');
      return;
    }
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.savePurchaseInvoice(this.invoiceId, this.draft(true)));
      const saved = await firstValueFrom(this.api.attachPurchaseInvoiceFile(this.invoiceId, file));
      this.paper.set(saved.attachment?.fileName ?? file.name);
      this.notify.info('The paper is attached.');
    } catch (error) {
      this.notify.error(error, 'The paper could not be attached.');
    } finally {
      this.saving.set(false);
    }
  }

  protected readonly icon = paperIcon;

  protected viewPaper(invoiceId: string, fileName: string): void {
    viewInvoicePaper(this.dialog, invoiceId, fileName);
  }

  // A draft from earlier, back on screen as it was entered.
  protected async continueDraft(id: string): Promise<void> {
    const invoice = await firstValueFrom(this.api.purchaseInvoice(id));
    if (!invoice) {
      await this.load();
      return;
    }
    this.invoiceId = invoice.id;
    // Its supplier, archived since or not: a draft keeps the one it had.
    this.supplier.set(invoice.supplierId, invoice.supplierName);
    this.supplierInvoiceNumber = invoice.supplierInvoiceNumber ?? '';
    this.invoiceDate = invoice.invoiceDate ?? PurchasesPage.today();
    this.paymentMethod = invoice.paymentMethod === 'BeforeTreasury' ? null : invoice.paymentMethod;
    this.revenueDate = invoice.revenueDate ?? this.today;
    this.paper.set(invoice.attachment?.fileName ?? null);
    this.review.set(null);
    this.editor.fill(invoice.lines);
    this.touch();
  }

  protected async discardDraft(id: string): Promise<void> {
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.discardPurchaseInvoice(id));
      if (id === this.invoiceId) {
        this.reset();
      }
      this.notify.info('Draft discarded.');
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The draft could not be discarded.');
    } finally {
      this.saving.set(false);
    }
  }

  // The invoice on screen, for the till. Lines not finished yet are left out of a draft saved only
  // to hold its paper.
  private draft(partial = false): SavePurchaseInvoice {
    return {
      supplierId: this.supplierId,
      supplierInvoiceNumber: this.supplierInvoiceNumber.trim() || null,
      invoiceDate: this.invoiceDate || null,
      paymentMethod: this.paymentMethod,
      revenueDate: this.paymentMethod === 'DayRevenue' ? this.revenueDate || null : null,
      lines: this.editor.toSave(partial),
    };
  }

  private async load(): Promise<void> {
    this.error.set(null);
    void this.businessDay.today().then((today) => {
      // Not yet chosen: the business day.
      if (this.revenueDate === this.today) {
        this.revenueDate = today;
      }
      this.today = today;
    });
    try {
      const [drafts] = await Promise.all([firstValueFrom(this.api.purchaseInvoices('Draft')), this.catalog.load()]);
      this.unfinished.set(drafts);
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
