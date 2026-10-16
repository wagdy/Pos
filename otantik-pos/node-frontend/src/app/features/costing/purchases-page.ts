import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  CostingApi,
  ExpenseItem,
  InvoiceCorrectionInput,
  InvoiceCorrectionPreview,
  MaterialCost,
  PurchaseInvoice,
  PurchaseInvoiceSummary,
  SavePurchaseInvoice,
  Supplier,
  SupplierBalance,
  PurchasePaymentMethod,
  isRequested,
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
import { SupplierPaymentEntry, SupplierPaymentFields, isOverOwed, newSupplierPayment } from './supplier-payment-fields';

// A supplier's invoice, in two steps. Entered as it is written (who from, their number and date,
// each line in the unit billed, and a photo of the paper), then saved as a draft and reviewed;
// only "Confirm & post" takes the stock in and moves the average costs. A draft survives a
// reload, and can be finished on another tablet. A line is a raw material or an expense item
// (cleaning, a repair, a bill), on the same invoice or one of its own: an expense is only its
// cost, under its category, and never goes into stock.
//
// Or, switched at the top, a supplier paid with no invoice (سداد مورد): entered as in the daily
// batch, against what they are owed, from the safe or a day's takings, and no stock moves.
//
// With ?correct=, a posted invoice corrected (تعديل فاتورة): it opens here as entered, and once its
// changes are reviewed, it is voided and posted again as it should have been, a new invoice in its
// place. A manager's at once; anyone else's waits for a manager's approval, the invoice as it was.
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
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MoneyPipe,
    RouterLink,
    SupplierPaymentFields,
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
    } @else if (correctionPreview(); as cp) {
      <!-- A correction, reviewed: what changes, as the manager will see it. -->
      <section class="review">
        <h2>Review the changes <span class="inline-ar">مراجعة التعديل</span></h2>
        <p class="note">{{ cp.summary }}</p>
        <div class="table-wrap">
          <table class="grid changes">
            <thead>
              <tr>
                <th>What<span class="ar">البند</span></th>
                <th>Was<span class="ar">قبل</span></th>
                <th>Now<span class="ar">بعد</span></th>
              </tr>
            </thead>
            <tbody>
              @for (c of cp.changes; track c.label) {
                <tr>
                  <td>{{ c.label }}</td>
                  <td [class.muted]="!c.before">{{ c.before ?? 'Not on it' }}</td>
                  <td [class.muted]="!c.after">{{ c.after ?? 'Taken off' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <p class="note">
          {{ approvals.canApprove() ? 'Saved, the invoice is voided and this one posted in its place: its stock, cash and supplier’s account are redone.'
            : 'A manager approves it first: until then the invoice stays as it is.' }}
        </p>
        @if (!approvals.canApprove()) {
          <mat-form-field class="reason" subscriptSizing="dynamic">
            <mat-label>Why · السبب</mat-label>
            <input matInput [(ngModel)]="reason" maxlength="300" placeholder="Typed 5 for 6" aria-label="Why it is corrected" />
          </mat-form-field>
        }
        <div class="toolbar actions">
          <button mat-button (click)="correctionPreview.set(null)" [disabled]="saving()"><mat-icon>edit</mat-icon> Back to edit</button>
          <span class="spacer"></span>
          <button mat-flat-button (click)="sendCorrection()" [disabled]="saving()">
            <mat-icon>{{ approvals.canApprove() ? 'published_with_changes' : 'send' }}</mat-icon>
            {{ approvals.canApprove() ? 'Save correction' : 'Send for approval' }}
            <span class="inline-ar">{{ approvals.canApprove() ? 'حفظ التعديل' : 'إرسال للموافقة' }}</span>
          </button>
        </div>
      </section>
    } @else {
      @if (correcting(); as original) {
        <div class="correcting" role="status">
          <mat-icon>edit_note</mat-icon>
          <span>
            Correcting <a [routerLink]="['/costing/purchases/invoices', original.id]">{{ invoiceName(original) }}</a>,
            {{ original.total | money }}. Saved, it is voided and this one posted in its place.
            <span class="inline-ar">تعديل فاتورة</span>
          </span>
        </div>
      } @else {
      <mat-button-toggle-group class="mode" [value]="mode()" (change)="mode.set($event.value)" aria-label="What to enter" hideSingleSelectionIndicator>
        <mat-button-toggle value="invoice"><mat-icon>receipt_long</mat-icon> Purchase invoice · فاتورة مشتريات</mat-button-toggle>
        <mat-button-toggle value="payment"><mat-icon>payments</mat-icon> Supplier payment · سداد مورد</mat-button-toggle>
      </mat-button-toggle-group>
      }
      @if (mode() === 'invoice' || correcting()) {
      <!-- Step one: the invoice as written. -->
      @if (drafts().length && !correcting()) {
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
          <input matInput [(ngModel)]="supplierInvoiceNumber" maxlength="64" data-western-digits />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Invoice date · تاريخ الفاتورة</mat-label>
          <input matInput type="date" [(ngModel)]="invoiceDate" />
        </mat-form-field>
        @if (paymentMethod === 'BeforeTreasury') {
          <span class="muted before-treasury">Before the treasury · قبل الخزنة: no cash, no supplier's account</span>
        } @else {
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
        }
        @if (paymentMethod === 'DayRevenue') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Takings of · إيراد يوم</mat-label>
            <input matInput type="date" [(ngModel)]="revenueDate" (ngModelChange)="touch()" [max]="today" aria-label="Takings of" />
          </mat-form-field>
        }
        <div class="paper" [hidden]="!!correcting()">
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
        @if (correcting(); as original) {
          <a mat-button [routerLink]="['/costing/purchases/invoices', original.id]">Cancel</a>
          <button mat-flat-button (click)="reviewCorrection()" [disabled]="!ready() || saving()">
            <mat-icon>fact_check</mat-icon> Review changes <span class="inline-ar">مراجعة التعديل</span>
          </button>
        } @else {
          <button mat-button (click)="reset()" [disabled]="saving()">Start again</button>
          <button mat-flat-button (click)="save()" [disabled]="!ready() || saving()">
            <mat-icon>fact_check</mat-icon> Save &amp; review
          </button>
        }
      </div>
      } @else {
        <!-- A supplier paid with no invoice: no lines, no stock. -->
        <p class="note">
          A supplier paid against what they are owed, with no invoice: from the safe or a day's takings. What they are owed goes down
          by it, and no stock moves. <span class="inline-ar">سداد مورد بدون فاتورة.</span>
        </p>
        <div class="header">
          <app-supplier-payment-fields [payment]="payment()" [suppliers]="owedSuppliers()" [owed]="owedNow()" (changed)="paymentEdited()" />
          <mat-form-field subscriptSizing="dynamic" class="payment">
            <mat-label>Paid · طريقة الدفع</mat-label>
            <mat-select [(ngModel)]="payMethod" (ngModelChange)="paymentEdited()" aria-label="How the supplier is paid" required>
              @for (m of cashMethods; track m) {
                <mat-option [value]="m">{{ paymentLabels[m].en }} · {{ paymentLabels[m].ar }}</mat-option>
              }
            </mat-select>
            <mat-hint>A payment is cash: never on credit.</mat-hint>
          </mat-form-field>
          @if (payMethod === 'DayRevenue') {
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Takings of · إيراد يوم</mat-label>
              <input matInput type="date" [(ngModel)]="payRevenueDate" (ngModelChange)="paymentEdited()" [max]="today" aria-label="Paid from the takings of" />
            </mat-form-field>
          }
        </div>
        @if (!approvals.canApprove()) {
          <p class="ask">A manager approves supplier payments: no cash moves until then. <span class="inline-ar">يُرسل للمدير للموافقة.</span></p>
        }
        <div class="toolbar actions">
          <span class="spacer"></span>
          <button mat-button (click)="newPayment()" [disabled]="saving()">Start again</button>
          <button mat-flat-button (click)="payNow()" [disabled]="!supplierPaymentReady() || saving()">
            <mat-icon>payments</mat-icon> Pay the supplier <span class="inline-ar">سداد</span>
          </button>
        </div>
      }
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
    .mode {
      margin-bottom: 12px;
    }
    .correcting {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 12px;
      padding: 10px 12px;
      border-radius: 12px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    .before-treasury {
      align-self: center;
    }
    .reason {
      width: min(100%, 420px);
    }
    .changes td:first-child {
      font-weight: 500;
    }
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
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
  private readonly router = inject(Router);
  protected readonly approvals = inject(Approvals);
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

  // ?correct=: the posted invoice to correct.
  readonly correct = input<string>();
  // The posted invoice being corrected, opened here as it was entered; null for a new invoice.
  protected readonly correcting = signal<PurchaseInvoice | null>(null);
  // Its changes, reviewed before they are sent: the same lines the manager sees.
  protected readonly correctionPreview = signal<InvoiceCorrectionPreview | null>(null);
  // Why, for the manager who approves it.
  protected reason = '';
  private correctionRequestId = crypto.randomUUID();

  // What is entered: a purchase invoice, or a supplier paid with no invoice (سداد مورد). Each keeps
  // what was typed into it while the other is on screen.
  protected readonly mode = signal<'invoice' | 'payment'>('invoice');
  protected readonly payment = signal<SupplierPaymentEntry>(newSupplierPayment());
  // How the supplier is paid: cash, from the safe or a day's takings, never on credit.
  protected payMethod: PurchasePaymentMethod | null = null;
  protected payRevenueDate: string = PurchasesPage.today();
  protected readonly cashMethods: PurchasePaymentMethod[] = ['Safe', 'DayRevenue'];
  // What each supplier is owed: only those owed something can be paid.
  private readonly balances = signal<SupplierBalance[]>([]);
  protected readonly owedSuppliers = computed(() => this.balances().filter((b) => b.balance > 0));
  // The payment's fields are plain; this bumps so what reads them follows.
  private readonly paymentVersion = signal(0);

  // A supplier owed, an amount no more than that, and how it is paid.
  protected readonly supplierPaymentReady = computed(() => {
    this.paymentVersion();
    const p = this.payment();
    return (
      !!p.supplierId &&
      (p.amount ?? 0) > 0 &&
      !isOverOwed(p, this.owedNow()) &&
      this.payMethod !== null &&
      (this.payMethod !== 'DayRevenue' || !!this.payRevenueDate)
    );
  });

  constructor() {
    void this.load();
    effect(() => {
      const id = this.correct();
      untracked(() => void this.openCorrection(id));
    });
  }

  // "Invoice F-1 · Nile Farms of 12 Oct".
  protected invoiceName(invoice: PurchaseInvoice): string {
    const date = invoice.invoiceDate ? new Date(`${invoice.invoiceDate}T00:00`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' }) : null;
    return (
      (invoice.supplierInvoiceNumber ? `Invoice ${invoice.supplierInvoiceNumber}` : 'The invoice') +
      (invoice.supplierName ? ` · ${invoice.supplierName}` : '') +
      (date ? ` of ${date}` : '')
    );
  }

  // The changes, checked by the server as they will be when sent.
  protected async reviewCorrection(): Promise<void> {
    const original = this.correcting();
    if (!original || !this.ready()) {
      return;
    }
    this.saving.set(true);
    try {
      this.correctionPreview.set(await firstValueFrom(this.api.previewInvoiceCorrection(original.id, this.correction())));
    } catch (error) {
      this.notify.error(error, 'The changes could not be checked.');
    } finally {
      this.saving.set(false);
    }
  }

  // Done at once for a manager, to the corrected invoice; asked of one for anyone else, back to the
  // invoice as it is.
  protected async sendCorrection(): Promise<void> {
    const original = this.correcting();
    if (!original) {
      return;
    }
    this.saving.set(true);
    try {
      const result = await firstValueFrom(this.api.correctPurchaseInvoice(original.id, this.correction()));
      if (result.outcome === 'Requested') {
        await this.approvals.requested(result);
        await this.router.navigate(['/costing/purchases/invoices', original.id]);
      } else {
        this.notify.info(`Corrected: ${result.request.summary}.`);
        await this.router.navigate(['/costing/purchases/invoices', this.invoiceId]);
      }
    } catch (error) {
      this.notify.error(error, 'The correction could not be saved.');
    } finally {
      this.saving.set(false);
    }
  }

  // The invoice as entered, opened to be corrected, with a new id for the invoice replacing it.
  private async openCorrection(id: string | undefined): Promise<void> {
    if (!id) {
      // Left for a new invoice: an empty one.
      if (this.correcting()) {
        this.correcting.set(null);
        this.correctionPreview.set(null);
        this.reset();
      }
      return;
    }
    try {
      const invoice = await firstValueFrom(this.api.purchaseInvoice(id));
      if (!invoice || invoice.status !== 'Posted') {
        this.notify.error(null, invoice?.replacedBy ? 'This invoice has already been corrected: correct the invoice that replaced it.' : 'Only a posted invoice is corrected.');
        await this.router.navigate(['/costing/purchases/new']);
        return;
      }
      this.reset();
      this.supplier.set(invoice.supplierId, invoice.supplierName);
      this.supplierInvoiceNumber = invoice.supplierInvoiceNumber ?? '';
      this.invoiceDate = invoice.invoiceDate ?? PurchasesPage.today();
      this.paymentMethod = invoice.paymentMethod ?? 'BeforeTreasury';
      this.revenueDate = invoice.revenueDate ?? this.today;
      this.editor.fill(invoice.lines);
      this.correctionRequestId = crypto.randomUUID();
      this.correcting.set(invoice);
      this.touch();
    } catch (error) {
      this.notify.error(error, 'The invoice could not be opened.');
    }
  }

  private correction(): InvoiceCorrectionInput {
    return { requestId: this.correctionRequestId, newInvoiceId: this.invoiceId, ...this.draft(), reason: this.reason.trim() || null };
  }

  // What the chosen supplier is owed now.
  protected owedNow(): number | null {
    const id = this.payment().supplierId;
    return id ? (this.balances().find((b) => b.supplierId === id)?.balance ?? null) : null;
  }

  protected paymentEdited(): void {
    this.paymentVersion.update((v) => v + 1);
  }

  protected newPayment(): void {
    this.payment.set(newSupplierPayment());
    this.payMethod = null;
    this.payRevenueDate = this.today;
    this.paymentEdited();
  }

  // Paid at once for a manager, asked of one for anyone else, as Pay a supplier is; its id made
  // here, so a retry after a lost answer pays once.
  protected async payNow(): Promise<void> {
    const p = this.payment();
    if (!this.supplierPaymentReady() || !p.supplierId || p.amount === null) {
      return;
    }
    this.saving.set(true);
    try {
      const result = await firstValueFrom(
        this.api.recordVoucher({
          requestId: p.id,
          transactionId: p.id,
          kind: 'SupplierPayment',
          amount: p.amount,
          date: this.today,
          source: null,
          revenueDate: this.payMethod === 'DayRevenue' ? this.payRevenueDate : null,
          supplierId: p.supplierId,
          expenseItemId: null,
          notes: p.notes.trim() || null,
          reason: null,
          includesTax: false,
        }),
      );
      if (isRequested(result)) {
        await this.approvals.requested(result);
      } else {
        const name = this.balances().find((b) => b.supplierId === p.supplierId)?.supplier ?? 'The supplier';
        this.notify.info(`${name} paid ${new MoneyPipe().transform(p.amount)}.`);
      }
      this.newPayment();
      await this.loadBalances();
    } catch (error) {
      this.notify.error(error, 'The payment could not be saved.');
    } finally {
      this.saving.set(false);
    }
  }

  private async loadBalances(): Promise<void> {
    this.balances.set(await firstValueFrom(this.api.supplierBalances()));
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
      (this.paymentMethod !== 'BeforeTreasury' || !!this.correcting()) &&
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
      if (this.payRevenueDate === this.today) {
        this.payRevenueDate = today;
      }
      this.today = today;
    });
    try {
      const [drafts] = await Promise.all([firstValueFrom(this.api.purchaseInvoices('Draft')), this.catalog.load(), this.loadBalances()]);
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
