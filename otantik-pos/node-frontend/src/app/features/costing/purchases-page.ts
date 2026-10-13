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
  PurchaseItemType,
  SavePurchaseInvoice,
  SavePurchaseLine,
  Supplier,
  purchaseUnits,
  PurchasePaymentMethod,
  paymentMethodLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from './approvals';
import { BusinessDay } from './financials';
import { ExpenseItemDialog, ExpenseItemDialogData } from './expense-item-dialog';
import { paperIcon, viewInvoicePaper } from './invoice-paper';
import { InvoiceView } from './invoice-view';
import { QuickMaterialData, QuickMaterialDialog, QuickMaterialResult } from './quick-material-dialog';
import { SupplierDialog, SupplierDialogData } from './supplier-dialog';

// The last options when what was typed matches nothing: add it, from the invoice, as a raw
// material or an expense item; or, in the supplier field, as a supplier.
interface AddNew {
  addNew: string;
  as?: PurchaseItemType;
}

interface Line {
  // What the item field holds: text being typed, or the material or expense chosen.
  entry?: string | MaterialCost | ExpenseItem | AddNew | null;
  // Stock (a raw material) or an expense, as the item chosen is; null until one is.
  itemType: PurchaseItemType | null;
  rawMaterialId: string | null;
  expenseItemId: string | null;
  quantity: number | null;
  // How many of the material's own units the chosen unit holds: 1000 for kg of a material kept
  // in grams. Null until a material is chosen, then what it is bought by. An expense's is 1.
  unitSize: number | null;
  // An expense line's unit, as written (bottle, visit); none for a bill.
  unitLabel: string;
  cost: number | null;
}

const baseLabels = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' } as const;

// A supplier's invoice, in two steps. Entered as it is written (who from, their number and date,
// each line in the unit billed, and a photo of the paper), then saved as a draft and reviewed;
// only "Confirm & post" takes the stock in and moves the average costs. A draft survives a
// reload, and can be finished on another tablet. A line is a raw material or an expense item
// (cleaning, a repair, a bill), on the same invoice or one of its own: an expense is only its
// cost, under its category, and never goes into stock.
@Component({
  selector: 'app-purchases-page',
  imports: [
    DatePipe,
    FormsModule,
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
            placeholder="Choose or type a name" />
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
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Item<span class="ar">الصنف</span></th>
              <th class="num">Quantity<span class="ar">الكمية</span></th>
              <th class="num">Line total<span class="ar">إجمالي السطر</span></th>
              <th class="num">AP$ per purchase unit<span class="ar">سعر وحدة الشراء</span></th>
              <th class="num">Average now<span class="ar">المتوسط الحالي</span></th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (line of lines(); track $index; let i = $index) {
              <tr>
                <td>
                  <mat-form-field subscriptSizing="dynamic" class="material">
                    <input matInput [matAutocomplete]="auto" [(ngModel)]="line.entry" (ngModelChange)="entered(line, $event)"
                      placeholder="Type to find…" [attr.aria-label]="'Item, line ' + (i + 1)" />
                    <mat-autocomplete #auto="matAutocomplete" [displayWith]="display">
                      @if (matches(line); as found) {
                        @if (found.materials.length) {
                          <mat-optgroup label="Stock items · خامات">
                            @for (m of found.materials; track m.id) {
                              <mat-option [value]="m">{{ label(m) }}</mat-option>
                            }
                          </mat-optgroup>
                        }
                        @if (found.expenses.length) {
                          <mat-optgroup label="Expenses · مصروفات">
                            @for (e of found.expenses; track e.id) {
                              <mat-option [value]="e">{{ e.name }} <span class="muted">· {{ e.category }}</span></mat-option>
                            }
                          </mat-optgroup>
                        }
                      }
                      @if (typedNew(line); as name) {
                        <mat-option [value]="{ addNew: name, as: 'Inventory' }" class="add-new">
                          <mat-icon>add</mat-icon> Add new material: "{{ name }}"
                        </mat-option>
                        <mat-option [value]="{ addNew: name, as: 'Expense' }" class="add-new">
                          <mat-icon>add</mat-icon> Add new expense: "{{ name }}"
                        </mat-option>
                      }
                    </mat-autocomplete>
                  </mat-form-field>
                  @if (expense(line); as e) {
                    <span class="badge expense">Expense · مصروف</span>
                  }
                </td>
                <td class="num">
                  <div class="quantity">
                    <mat-form-field subscriptSizing="dynamic" class="small">
                      <input matInput type="number" min="0" step="any" [(ngModel)]="line.quantity" (ngModelChange)="touch()" [attr.aria-label]="'Quantity, line ' + (i + 1)" />
                    </mat-form-field>
                    @if (material(line); as m) {
                      <mat-form-field subscriptSizing="dynamic" class="unit">
                        <mat-select [(ngModel)]="line.unitSize" (ngModelChange)="touch()" [aria-label]="'Unit, line ' + (i + 1)">
                          @for (u of units(m); track u.size) {
                            <mat-option [value]="u.size">{{ u.label }}</mat-option>
                          }
                        </mat-select>
                      </mat-form-field>
                    } @else if (expense(line)) {
                      <mat-form-field subscriptSizing="dynamic" class="unit">
                        <input matInput [(ngModel)]="line.unitLabel" maxlength="32" placeholder="unit" [attr.aria-label]="'Unit, line ' + (i + 1)" />
                      </mat-form-field>
                    }
                  </div>
                  @if (stockAdded(line); as added) {
                    <small class="muted">+ {{ added }} to stock</small>
                  } @else if (expense(line)) {
                    <small class="muted">Not stock</small>
                  }
                </td>
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="small">
                    <span matTextPrefix>L.E&nbsp;</span>
                    <input matInput type="number" min="0" step="any" [(ngModel)]="line.cost" (ngModelChange)="touch()" [attr.aria-label]="'Line total, line ' + (i + 1)"
                      [required]="!!expense(line)" />
                  </mat-form-field>
                  @if (expense(line) && line.cost === null) {
                    <small class="warn-text">Its amount</small>
                  }
                </td>
                <td class="num">{{ unitPrice(line) === null ? '' : (unitPrice(line) | money) }}</td>
                <td class="num muted">
                  @if (expense(line); as e) {
                    <span class="category">{{ e.category }}</span>
                  } @else {
                    {{ material(line)?.costPerPurchaseUnit == null ? '' : (material(line)!.costPerPurchaseUnit | money) }}
                  }
                </td>
                <td>
                  <button mat-icon-button (click)="remove(i)" [disabled]="lines().length === 1" aria-label="Remove line">
                    <mat-icon>delete</mat-icon>
                  </button>
                </td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td>
                <button mat-button (click)="add()"><mat-icon>add</mat-icon> Add line</button>
              </td>
              <td class="num">
                @if (totals().expenses > 0) {
                  <span class="split">Stock · مخزون</span>
                  <span class="split">Expenses · مصروفات</span>
                }
                Total · الإجمالي
              </td>
              <td class="num">
                @if (totals().expenses > 0) {
                  <span class="split">{{ totals().stock | money }}</span>
                  <span class="split">{{ totals().expenses | money }}</span>
                }
                {{ total() | money }}
              </td>
              <td colspan="3"></td>
            </tr>
          </tfoot>
        </table>
      </div>
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

  protected readonly materials = signal<MaterialCost[]>([]);
  // The expense items new lines can name: the archived ones are not offered.
  protected readonly expenses = signal<ExpenseItem[]>([]);
  // The suppliers new invoices can name: the archived ones are not offered.
  protected readonly suppliers = signal<Supplier[]>([]);
  private readonly unfinished = signal<PurchaseInvoiceSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly lines = signal<Line[]>([this.blank()]);

  // The saved draft being reviewed; null while the invoice is being entered.
  protected readonly review = signal<PurchaseInvoice | null>(null);
  // The attached paper's name, once one is.
  protected readonly paper = signal<string | null>(null);

  // What the supplier field holds: text being typed, a supplier chosen, or one to add.
  protected supplierEntry: string | Supplier | AddNew | null = '';
  protected supplierId: string | null = null;
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

  // The lines are changed in place by the form; this bumps the signal so totals follow.
  private readonly version = signal(0);

  protected readonly total = computed(() => {
    this.version();
    return this.lines().reduce((sum, l) => sum + (l.cost ?? 0), 0);
  });

  // The total split: what goes into stock, and what is expenses.
  protected readonly totals = computed(() => {
    this.version();
    const sum = (type: PurchaseItemType) => this.lines().filter((l) => l.itemType === type).reduce((s, l) => s + (l.cost ?? 0), 0);
    return { stock: sum('Inventory'), expenses: sum('Expense') };
  });

  // Every line names an item and a quantity; nothing twice; costs, where given, not negative, and
  // always given for an expense, which is nothing else. A supplier name typed but not chosen or
  // added would be lost, so it holds the save too.
  protected readonly ready = computed(() => {
    this.version();
    const lines = this.lines();
    const ids = lines.map((l) => l.rawMaterialId ?? l.expenseItemId);
    return (
      lines.every(
        (l) =>
          (l.rawMaterialId || l.expenseItemId) &&
          (l.quantity ?? 0) > 0 &&
          (l.cost === null ? l.itemType !== 'Expense' : l.cost >= 0),
      ) &&
      new Set(ids).size === ids.length &&
      !this.supplierUnresolved() &&
      this.paymentReady()
    );
  });

  constructor() {
    void this.load();
  }

  // The suppliers matching what is typed; all of them before anything is.
  protected supplierMatches(): Supplier[] {
    const text = typeof this.supplierEntry === 'string' ? this.supplierEntry.trim().toLowerCase() : '';
    return this.suppliers().filter((s) => !text || s.name.toLowerCase().includes(text));
  }

  // The typed name, when no supplier is called that (capitals and extra spaces aside): it can be added.
  protected typedNewSupplier(): string | null {
    const text = typeof this.supplierEntry === 'string' ? this.supplierEntry.trim() : '';
    return text && !this.supplierNamed(text) ? text : null;
  }

  protected readonly supplierLabel = (value: string | Supplier | AddNew | null | undefined): string =>
    !value ? '' : typeof value === 'string' ? value : 'addNew' in value ? value.addNew : value.name;

  // A name typed that is no supplier yet: neither chosen nor added.
  protected supplierUnresolved(): boolean {
    this.version();
    return typeof this.supplierEntry === 'string' && this.supplierEntry.trim() !== '' && this.supplierId === null;
  }

  // Typing, choosing a supplier, or choosing to add one. A name typed in full is that supplier.
  protected supplierEntered(value: string | Supplier | AddNew | null): void {
    if (value && typeof value === 'object' && 'addNew' in value) {
      void this.addSupplier(value.addNew);
      return;
    }
    this.supplierId = !value ? null : typeof value === 'string' ? (this.supplierNamed(value)?.id ?? null) : value.id;
    this.touch();
  }

  // The quick dialog over the invoice: the supplier is added and chosen, the lines untouched.
  // Cancelled, the field keeps what was typed.
  protected async addSupplier(name: string): Promise<void> {
    const created = await firstValueFrom(
      this.dialog
        .open<SupplierDialog, SupplierDialogData, Supplier>(SupplierDialog, { data: { name, fromInvoice: true }, width: '520px' })
        .afterClosed(),
    );
    if (!created) {
      this.supplierEntry = name;
      this.supplierId = null;
      this.touch();
      return;
    }
    this.suppliers.set(await firstValueFrom(this.api.suppliers()));
    this.supplierEntry = this.suppliers().find((s) => s.id === created.id) ?? created;
    this.supplierId = created.id;
    this.touch();
    this.notify.info(`${created.name} added, and on the invoice.`);
  }

  private supplierNamed(name: string): Supplier | undefined {
    const key = PurchasesPage.normalize(name);
    return this.suppliers().find((s) => PurchasesPage.normalize(s.name) === key);
  }

  protected material(line: Line): MaterialCost | undefined {
    return line.rawMaterialId ? this.materials().find((m) => m.id === line.rawMaterialId) : undefined;
  }

  protected expense(line: Line): ExpenseItem | undefined {
    return line.expenseItemId ? this.expenses().find((e) => e.id === line.expenseItemId) : undefined;
  }

  protected units(material: MaterialCost): { label: string; size: number }[] {
    return purchaseUnits(material);
  }

  protected label(material: MaterialCost): string {
    return material.code ? `${material.code} · ${material.name}` : material.name;
  }

  // What the field shows for a choice: the material, the expense, or the name being added.
  protected readonly display = (value: string | MaterialCost | ExpenseItem | AddNew | null | undefined): string =>
    !value ? '' : typeof value === 'string' ? value : 'addNew' in value ? value.addNew : PurchasesPage.isMaterial(value) ? this.label(value) : value.name;

  // The materials and expenses matching what is typed; all of them before anything is.
  protected matches(line: Line): { materials: MaterialCost[]; expenses: ExpenseItem[] } {
    const text = typeof line.entry === 'string' ? line.entry.trim().toLowerCase() : '';
    return {
      materials: this.materials().filter((m) => !text || this.label(m).toLowerCase().includes(text)),
      expenses: this.expenses().filter((e) => !text || [e.name, e.nameAr, e.category].some((v) => v?.toLowerCase().includes(text))),
    };
  }

  // The typed name, when nothing is called exactly that: it can be added, as either.
  protected typedNew(line: Line): string | null {
    const text = typeof line.entry === 'string' ? line.entry.trim() : '';
    const key = text.toLowerCase();
    return text && !this.materials().some((m) => m.name.toLowerCase() === key) && !this.expenses().some((e) => e.name.toLowerCase() === key)
      ? text
      : null;
  }

  // Typing, choosing a material or an expense, or choosing to add one. A material chosen is taken
  // in what it is bought by until another unit is picked; an expense in its usual unit, one of it.
  protected entered(line: Line, value: string | MaterialCost | ExpenseItem | AddNew | null): void {
    if (value && typeof value === 'object' && 'addNew' in value) {
      void (value.as === 'Expense' ? this.addExpense(line, value.addNew) : this.addMaterial(line, value.addNew));
      return;
    }
    this.choose(line, value && typeof value === 'object' ? value : null);
    this.touch();
  }

  private choose(line: Line, item: MaterialCost | ExpenseItem | null): void {
    const material = item && PurchasesPage.isMaterial(item) ? item : null;
    const expense = item && !PurchasesPage.isMaterial(item) ? item : null;
    line.itemType = material ? 'Inventory' : expense ? 'Expense' : null;
    line.rawMaterialId = material?.id ?? null;
    line.expenseItemId = expense?.id ?? null;
    line.unitSize = material?.purchaseUnitSize ?? (expense ? 1 : null);
    if (expense) {
      line.unitLabel = expense.unit ?? '';
      line.quantity ??= 1;
    }
  }

  // The quick dialog over the invoice for an expense: added, and on this line.
  protected async addExpense(line: Line, name: string): Promise<void> {
    const created = await firstValueFrom(
      this.dialog
        .open<ExpenseItemDialog, ExpenseItemDialogData, ExpenseItem>(ExpenseItemDialog, { data: { name, fromInvoice: true }, width: '480px' })
        .afterClosed(),
    );
    if (!created) {
      line.entry = name;
      this.touch();
      return;
    }
    this.expenses.set(await firstValueFrom(this.api.expenseItems()));
    const item = this.expenses().find((e) => e.id === created.id) ?? created;
    line.entry = item;
    this.choose(line, item);
    this.touch();
    this.notify.info(`${created.name} added, and on the invoice.`);
  }

  // The quick dialog over the invoice. Nothing on the invoice changes but this line's material;
  // cancelled, the line keeps what was typed.
  protected async addMaterial(line: Line, name: string): Promise<void> {
    const created = await firstValueFrom(
      this.dialog
        .open<QuickMaterialDialog, QuickMaterialData, QuickMaterialResult>(QuickMaterialDialog, { data: { name }, width: '520px' })
        .afterClosed(),
    );
    if (!created) {
      line.entry = name;
      this.touch();
      return;
    }
    await this.loadMaterials();
    const material = this.materials().find((m) => m.id === created.id);
    line.entry = material ?? created.name;
    if (material) {
      this.choose(line, material);
    } else {
      Object.assign(line, { itemType: 'Inventory', rawMaterialId: created.id, expenseItemId: null, unitSize: null });
    }
    this.touch();
    this.notify.info(`${created.name} added, and on the invoice.`);
  }

  // The line's quantity in the material's own unit, as stock is kept.
  protected baseQuantity(line: Line): number | null {
    const material = this.material(line);
    if (!material || !line.quantity || line.quantity <= 0) {
      return null;
    }
    // To the thousandth, as stock is stored: 0.1 kg is 100 g, not 100.00000000000001.
    return Math.round(line.quantity * (line.unitSize ?? material.purchaseUnitSize) * 1000) / 1000;
  }

  protected stockAdded(line: Line): string | null {
    const material = this.material(line);
    const base = this.baseQuantity(line);
    if (!material || base === null || (line.unitSize ?? material.purchaseUnitSize) === 1) {
      return null;
    }
    return `${base.toLocaleString('en-EG', { maximumFractionDigits: 3 })} ${baseLabels[material.unit]}`;
  }

  // What the line paid per purchase unit, whatever unit it was entered in: comparable with the
  // average beside it. An expense's: per one of what it is counted in.
  protected unitPrice(line: Line): number | null {
    if (this.expense(line)) {
      return line.cost !== null && line.quantity ? line.cost / line.quantity : null;
    }
    const material = this.material(line);
    const base = this.baseQuantity(line);
    return material && base && line.cost !== null ? (line.cost / base) * material.purchaseUnitSize : null;
  }

  protected touch(): void {
    this.version.update((v) => v + 1);
  }

  protected add(): void {
    this.lines.update((lines) => [...lines, this.blank()]);
  }

  protected remove(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
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
    this.lines.set([this.blank()]);
    this.supplierEntry = '';
    this.supplierId = null;
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
    this.supplierId = invoice.supplierId;
    this.supplierEntry = !invoice.supplierId
      ? ''
      : (this.suppliers().find((s) => s.id === invoice.supplierId) ??
        ({ id: invoice.supplierId, name: invoice.supplierName ?? 'A supplier' } as Supplier));
    this.supplierInvoiceNumber = invoice.supplierInvoiceNumber ?? '';
    this.invoiceDate = invoice.invoiceDate ?? PurchasesPage.today();
    this.paymentMethod = invoice.paymentMethod === 'BeforeTreasury' ? null : invoice.paymentMethod;
    this.revenueDate = invoice.revenueDate ?? this.today;
    this.paper.set(invoice.attachment?.fileName ?? null);
    this.review.set(null);
    this.lines.set(
      invoice.lines.length
        ? invoice.lines.map((l) => ({
            entry:
              (l.itemType === 'Expense'
                ? this.expenses().find((e) => e.id === l.expenseItemId)
                : this.materials().find((m) => m.id === l.rawMaterialId)) ?? l.material,
            itemType: l.itemType,
            rawMaterialId: l.rawMaterialId,
            expenseItemId: l.expenseItemId,
            quantity: l.enteredQuantity,
            unitSize: l.unitSize,
            unitLabel: l.itemType === 'Expense' && l.unitLabel !== 'unit' ? l.unitLabel : '',
            cost: l.cost,
          }))
        : [this.blank()],
    );
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
      lines: this.lines()
        .filter((l) => !partial || ((l.rawMaterialId || (l.expenseItemId && l.cost !== null)) && (l.quantity ?? 0) > 0))
        .map((l): SavePurchaseLine => {
          if (l.itemType === 'Expense') {
            return {
              itemType: 'Expense',
              rawMaterialId: null,
              expenseItemId: l.expenseItemId,
              enteredQuantity: l.quantity!,
              unitLabel: l.unitLabel.trim() || null,
              unitSize: 1,
              cost: l.cost,
            };
          }
          const material = this.material(l)!;
          const size = l.unitSize ?? material.purchaseUnitSize;
          return {
            itemType: 'Inventory',
            rawMaterialId: l.rawMaterialId!,
            expenseItemId: null,
            enteredQuantity: l.quantity!,
            unitLabel: purchaseUnits(material).find((u) => u.size === size)?.label ?? material.purchaseUnit,
            unitSize: size,
            cost: l.cost,
          };
        }),
    };
  }

  private blank(): Line {
    return { itemType: null, rawMaterialId: null, expenseItemId: null, quantity: null, unitSize: null, unitLabel: '', cost: null };
  }

  private static isMaterial(item: MaterialCost | ExpenseItem): item is MaterialCost {
    return 'purchaseUnitSize' in item;
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
      const [materials, drafts, suppliers, expenses] = await Promise.all([
        firstValueFrom(this.api.materials()),
        firstValueFrom(this.api.purchaseInvoices('Draft')),
        firstValueFrom(this.api.suppliers()),
        firstValueFrom(this.api.expenseItems()),
      ]);
      // Prep items are made in the kitchen, not bought.
      this.materials.set(materials.filter((m) => m.kind !== 'Prep'));
      this.unfinished.set(drafts);
      this.suppliers.set(suppliers);
      this.expenses.set(expenses);
    } catch (error) {
      this.error.set(errorMessage(error, 'The purchases could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private async loadMaterials(): Promise<void> {
    this.materials.set((await firstValueFrom(this.api.materials())).filter((m) => m.kind !== 'Prep'));
  }

  // As the till compares supplier names: capitals and extra spaces aside.
  private static normalize(name: string): string {
    return name.trim().split(/\s+/).join(' ').toUpperCase();
  }

  private static today(): string {
    const now = new Date();
    return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  }
}
