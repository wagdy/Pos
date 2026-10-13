import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, Supplier, SupplierBalance, SupplierStatement } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { Treasury } from './financials';

// Supplier debts (مديونيات الموردين): what the restaurant owes each supplier, from invoices bought
// on credit and what was owed before the treasury, less what was paid them. A supplier chosen
// shows their statement of account (كشف حساب): the credit invoices, the payments and the opening
// debt with the balance after each, and the invoices still owed (payments go to the oldest first).
// A payment voucher is issued from here.
@Component({
  selector: 'app-supplier-debts-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, MoneyPipe],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
    <div class="tiles">
      <div class="tile key">
        <span class="label">Owed to suppliers · إجمالي المديونيات</span>
        <strong>{{ totalOwed() | money }}</strong>
      </div>
      <div class="tile">
        <span class="label">Suppliers owed · موردون دائنون</span>
        <strong>{{ owing().length }}</strong>
      </div>
    </div>
    <div class="table-wrap">
      <table class="grid">
        <thead>
          <tr>
            <th>Supplier<span class="ar">المورد</span></th>
            <th class="num">Credit purchases<span class="ar">إجمالي الفواتير الآجلة</span></th>
            <th class="num">Owed before<span class="ar">رصيد سابق</span></th>
            <th class="num">Paid<span class="ar">إجمالي المدفوعات</span></th>
            <th class="num">Owed now<span class="ar">الرصيد المتبقي</span></th>
            <th>Last paid<span class="ar">آخر سداد</span></th>
          </tr>
        </thead>
        <tbody>
          @for (b of balances(); track b.supplierId) {
            <tr class="clickable" [class.chosen]="b.supplierId === supplierId()" (click)="choose(b.supplierId)">
              <td>{{ b.supplier }}@if (b.isArchived) {<span class="muted"> · archived</span>}</td>
              <td class="num">{{ b.creditPurchases | money }}</td>
              <td class="num">{{ b.openingDebt ? (b.openingDebt | money) : '—' }}</td>
              <td class="num">{{ b.payments | money }}</td>
              <td class="num strong" [class.owed]="b.balance > 0">{{ b.balance | money }}</td>
              <td class="nowrap">{{ b.lastPaidOn ? (b.lastPaidOn | date: 'd MMM y') : '—' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="muted">No supplier is owed anything. Invoices bought on credit show here.</td></tr>
          }
        </tbody>
      </table>
    </div>

    <div class="toolbar statement-bar">
      <mat-form-field subscriptSizing="dynamic" class="supplier">
        <mat-label>Statement of account for · كشف حساب</mat-label>
        <mat-select [ngModel]="supplierId()" (ngModelChange)="choose($event)" aria-label="Supplier">
          @for (s of suppliers(); track s.id) {
            <mat-option [value]="s.id">{{ s.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      @if (statement(); as st) {
        <span class="spacer"></span>
        <button mat-stroked-button (click)="opening(st)"><mat-icon>history</mat-icon> Owed from before</button>
        <button mat-flat-button (click)="pay(st)" [disabled]="st.balance <= 0"><mat-icon>payments</mat-icon> Pay {{ st.supplier }}</button>
      }
    </div>

    @if (statement(); as st) {
      <div class="tiles">
        <div class="tile">
          <span class="label">Credit purchases · إجمالي الفواتير الآجلة</span>
          <strong>{{ st.creditPurchases | money }}</strong>
          @if (st.openingDebt) {
            <small class="muted">and {{ st.openingDebt | money }} owed from before</small>
          }
        </div>
        <div class="tile">
          <span class="label">Payments made · إجمالي المدفوعات</span>
          <strong>{{ st.payments | money }}</strong>
        </div>
        <div class="tile key">
          <span class="label">Owed now · الرصيد المتبقي</span>
          <strong>{{ st.balance | money }}</strong>
          @if (st.balance < 0) {
            <small>{{ st.supplier }} owes the restaurant</small>
          }
        </div>
      </div>

      <h3>Invoices not fully paid <span class="inline-ar">فواتير غير مسددة</span></h3>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Invoice no.<span class="ar">رقم الفاتورة</span></th>
              <th class="num">Total<span class="ar">الإجمالي</span></th>
              <th class="num">Paid<span class="ar">المسدد</span></th>
              <th class="num">Remaining<span class="ar">المتبقي</span></th>
              <th>Status<span class="ar">الحالة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (i of st.openInvoices; track i.invoiceId) {
              <tr>
                <td class="nowrap">{{ i.date | date: 'd MMM y' }}</td>
                <td>{{ i.supplierInvoiceNumber ?? '—' }}</td>
                <td class="num">{{ i.total | money }}</td>
                <td class="num">{{ i.paid | money }}</td>
                <td class="num strong">{{ i.remaining | money }}</td>
                <td><span class="state" [class.partly]="i.status === 'PartlyPaid'">{{ i.status === 'PartlyPaid' ? 'Partly paid · مسددة جزئيًا' : 'Unpaid · غير مسددة' }}</span></td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="muted">Every credit invoice is paid.</td></tr>
            }
          </tbody>
        </table>
      </div>

      <h3>Statement of account <span class="inline-ar">كشف الحساب</span></h3>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Date<span class="ar">التاريخ</span></th>
              <th>Entry<span class="ar">البيان</span></th>
              <th class="num">Owed<span class="ar">مدين</span></th>
              <th class="num">Paid<span class="ar">دائن</span></th>
              <th class="num">Balance<span class="ar">الرصيد</span></th>
            </tr>
          </thead>
          <tbody>
            @for (l of st.lines; track l.id) {
              <tr [class.reversal]="l.isReversal">
                <td class="nowrap">{{ l.date | date: 'd MMM y' }}</td>
                <td>{{ l.description }}</td>
                <td class="num">{{ l.owed ? (l.owed | money) : '' }}</td>
                <td class="num">{{ l.paid ? (l.paid | money) : '' }}</td>
                <td class="num strong">{{ l.balance | money }}</td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="muted">Nothing on {{ st.supplier }}'s account yet.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    h3 {
      margin: 20px 0 8px;
      font: var(--mat-sys-title-medium);
    }
    .statement-bar {
      margin-top: 20px;
    }
    .supplier {
      min-width: 280px;
    }
    tr.chosen td {
      background: var(--mat-sys-secondary-container);
    }
    tr.reversal td {
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }
    .owed {
      color: var(--mat-sys-error);
    }
    .strong {
      font-weight: 600;
    }
    .nowrap {
      white-space: nowrap;
    }
    .state {
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);

      &.partly {
        background: color-mix(in srgb, var(--till-warn) 22%, transparent);
      }
    }
  `,
})
export class SupplierDebtsPage {
  private readonly api = inject(CostingApi);
  private readonly treasury = inject(Treasury);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly balances = signal<SupplierBalance[]>([]);
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly statement = signal<SupplierStatement | null>(null);
  protected readonly supplierId = signal<string | null>(this.route.snapshot.queryParamMap.get('supplier'));
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly owing = computed(() => this.balances().filter((b) => b.balance > 0));
  protected readonly totalOwed = computed(() => this.owing().reduce((sum, b) => sum + b.balance, 0));

  constructor() {
    void this.load();
  }

  protected choose(supplierId: string): void {
    this.supplierId.set(supplierId);
    void this.router.navigate([], { queryParams: { supplier: supplierId }, replaceUrl: true });
    void this.loadStatement();
  }

  protected async pay(statement: SupplierStatement): Promise<void> {
    if (await this.treasury.enter('SupplierPayment', { supplierId: statement.supplierId, amount: statement.balance })) {
      await this.load();
    }
  }

  protected async opening(statement: SupplierStatement): Promise<void> {
    if (await this.treasury.supplierOpening(statement.supplierId, statement.supplier)) {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [balances, suppliers] = await Promise.all([firstValueFrom(this.api.supplierBalances()), firstValueFrom(this.api.suppliers(true))]);
      this.balances.set(balances);
      this.suppliers.set(suppliers);
      await this.loadStatement();
    } catch (error) {
      this.error.set(errorMessage(error, 'The supplier debts could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private async loadStatement(): Promise<void> {
    const id = this.supplierId();
    if (!id) {
      this.statement.set(null);
      return;
    }
    try {
      this.statement.set(await firstValueFrom(this.api.supplierStatement(id)));
    } catch (error) {
      this.error.set(errorMessage(error, 'The statement could not be loaded.'));
    }
  }
}
