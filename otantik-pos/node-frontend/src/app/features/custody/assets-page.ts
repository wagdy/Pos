import { ComponentType } from '@angular/cdk/portal';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { AssetCategory, AssetItem, CustodyApi, isGone, statusLabels } from '../../core/custody/custody.api';
import { Employee, HrApi } from '../../core/hr/hr.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import {
  AssetDialog,
  AssetDialogData,
  AssetHistoryDialog,
  CheckInDialog,
  CheckInDialogData,
  CheckOutDialog,
  CheckOutDialogData,
  CustodyAnswer,
  wasAsked,
} from './custody-dialogs';

const WithFinancialKey = 'otantik.custody.with-financial';

function readWithFinancial(): boolean {
  try {
    return localStorage.getItem(WithFinancialKey) === 'true';
  } catch {
    return false;
  }
}

// The assets (الأصول): what there is of each, a piece's cost and the whole's, whose custody it is
// in or where it is, and its vouchers. Link with Financials (ربط بالمالية) adds those bought on
// purchase invoices to the ones entered in HR, and is remembered in this browser. What is gone
// (all broken or discarded) is shown when asked. Whoever manages HR adds, checks in and out, an
// accountant's going to a manager, its row locked until then.
@Component({
  selector: 'app-assets-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, MatSlideToggleModule, MatTooltipModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <mat-slide-toggle [ngModel]="withFinancial()" (ngModelChange)="link($event)" aria-label="Link with Financials">
        Link with Financials · ربط بالمالية
      </mat-slide-toggle>
      <mat-form-field subscriptSizing="dynamic" class="filter">
        <mat-label>Category · الفئة</mat-label>
        <mat-select [ngModel]="category()" (ngModelChange)="category.set($event)" aria-label="Category">
          <mat-option value="">All categories</mat-option>
          @for (c of categories(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [ngModel]="showGone()" (ngModelChange)="showGone.set($event)">Show what is gone · عرض المستبعد</mat-slide-toggle>
      <span class="spacer"></span>
      <span class="muted">{{ shown().length }} {{ shown().length === 1 ? 'asset' : 'assets' }}@if (costsShown()) { · {{ worth() | money }}}</span>
      @if (canManage()) {
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add an asset <span class="inline-ar">إضافة أصل</span></button>
      }
    </div>
    <p class="muted small">
      @if (withFinancial()) {
        Entered in HR, and bought on purchase invoices (of expenses marked as assets): those are in the store until handed out.
      } @else {
        Entered in HR. Turn on Link with Financials to add those bought on purchase invoices.
      }
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as problem) {
      <p class="error">{{ problem }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Asset<span class="ar">الأصل</span></th>
              <th class="num">How many<span class="ar">العدد</span></th>
              @if (costsShown()) {
                <th class="num">Cost a piece<span class="ar">تكلفة القطعة</span></th>
                <th class="num">Total<span class="ar">الإجمالي</span></th>
              }
              <th>Custody<span class="ar">العهدة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (a of shown(); track a.id) {
              @let asked = approvals.pendingFor(a.id);
              <tr [class.gone]="gone(a)" [class.pending-deletion]="!!asked">
                <td>
                  <strong>{{ a.name }}</strong>
                  @if (a.origin === 'FinancialPurchase') {
                    <span class="origin" matTooltip="Bought on a posted purchase invoice">Bought · مشتريات</span>
                  }
                  <div class="muted small">
                    {{ a.categoryName ?? 'No category' }}@if (a.isConsumable) { · consumable }@if (a.notes) { · {{ a.notes }} }
                  </div>
                  @if (asked) {
                    <div><span class="pending-badge">{{ pendingLabel(asked).en }}<span class="ar">{{ pendingLabel(asked).ar }}</span></span></div>
                  }
                </td>
                <td class="num">{{ a.quantity }}</td>
                @if (costsShown()) {
                  <td class="num">{{ a.unitCost === null ? '—' : (a.unitCost | money) }}</td>
                  <td class="num">{{ a.totalCost === null ? '—' : (a.totalCost | money) }}</td>
                }
                <td>
                  @if (a.employeeName) {
                    <strong>{{ a.employeeName }}</strong>
                  }
                  <div><span class="status" [class]="a.status">{{ statusLabels[a.status].en }}<span class="ar">{{ statusLabels[a.status].ar }}</span></span></div>
                </td>
                <td class="num nowrap">
                  <button mat-icon-button (click)="history(a)" matTooltip="Its vouchers" [attr.aria-label]="a.name + '’s vouchers'"><mat-icon>history</mat-icon></button>
                  @if (canManage()) {
                    @if (asked) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval">lock_clock</mat-icon>
                    } @else if (!gone(a)) {
                      @if (a.status !== 'InCustody') {
                        <button mat-stroked-button (click)="checkIn(a)" [attr.aria-label]="'Check in ' + a.name"><mat-icon>login</mat-icon> In</button>
                      }
                      <button mat-stroked-button (click)="checkOut(a)" [attr.aria-label]="'Check out ' + a.name"><mat-icon>logout</mat-icon> Out</button>
                      <button mat-icon-button (click)="edit(a)" matTooltip="Edit" [attr.aria-label]="'Edit ' + a.name"><mat-icon>edit</mat-icon></button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td [attr.colspan]="costsShown() ? 6 : 4" class="muted">
                  @if (assets().length) {
                    Nothing to show here: try another category, or show what is gone.
                  } @else {
                    No assets yet. Add the plates, the tools and the devices, then check each into someone's custody.
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .filter {
      min-width: 190px;
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;

      button {
        margin-left: 4px;
      }
    }
    tr.gone td {
      color: var(--mat-sys-on-surface-variant);
    }
    .origin {
      display: inline-block;
      margin-left: 6px;
      padding: 0 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: color-mix(in srgb, var(--mat-sys-tertiary) 16%, transparent);
    }
    .status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);

      &.InCustody {
        background: color-mix(in srgb, var(--till-ok, #2e7d32) 16%, transparent);
      }
      &.Maintenance {
        background: color-mix(in srgb, var(--till-warn) 22%, transparent);
      }
      &.BrokenWaste,
      &.Discarded {
        background: color-mix(in srgb, var(--mat-sys-error) 12%, transparent);
      }
    }
  `,
})
export class AssetsPage {
  private readonly api = inject(CustodyApi);
  private readonly hr = inject(HrApi);
  private readonly auth = inject(AuthService);
  // An employee working the tab in the portal is never sent what an asset cost.
  protected readonly costsShown = computed(() => !this.auth.employeeActing());
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;
  protected readonly statusLabels = statusLabels;
  protected readonly gone = isGone;

  protected readonly assets = signal<AssetItem[]>([]);
  protected readonly categories = signal<AssetCategory[]>([]);
  protected readonly withFinancial = signal(readWithFinancial());
  protected readonly category = signal('');
  protected readonly showGone = signal(false);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly canManage = computed(() => this.auth.can(Permissions.HrManage));

  protected readonly shown = computed(() =>
    this.assets().filter((a) => (this.showGone() || !isGone(a)) && (!this.category() || a.categoryId === this.category())),
  );
  protected readonly worth = computed(() => this.shown().reduce((sum, a) => sum + (a.totalCost ?? 0), 0));

  constructor() {
    void this.load();
    void this.approvals.refresh();
  }

  protected link(on: boolean): void {
    this.withFinancial.set(on);
    try {
      localStorage.setItem(WithFinancialKey, String(on));
    } catch {
      // Only remembered when the browser lets it be.
    }
    void this.load();
  }

  protected async add(): Promise<void> {
    await this.open(AssetDialog, { categories: this.categories(), canApprove: this.approvals.canApprove() } satisfies AssetDialogData, 'Asset added.');
  }

  protected async edit(asset: AssetItem): Promise<void> {
    await this.open(
      AssetDialog,
      { asset, categories: this.categories(), canApprove: this.approvals.canApprove(), costHidden: !this.costsShown() } satisfies AssetDialogData,
      `${asset.name} saved.`,
    );
  }

  protected async checkIn(asset: AssetItem): Promise<void> {
    let employees: Employee[];
    try {
      employees = (await firstValueFrom(this.hr.employees())).filter((e) => e.isActive);
    } catch (error) {
      this.notify.error(error, 'The employees could not be loaded.');
      return;
    }
    const data: CheckInDialogData = { asset, employees, today: await this.businessDay.today(), canApprove: this.approvals.canApprove() };
    await this.open(CheckInDialog, data, `${asset.name} checked in.`);
  }

  protected async checkOut(asset: AssetItem): Promise<void> {
    const data: CheckOutDialogData = { asset, today: await this.businessDay.today(), canApprove: this.approvals.canApprove() };
    await this.open(CheckOutDialog, data, `${asset.name} checked out.`);
  }

  protected history(asset: AssetItem): void {
    this.dialog.open(AssetHistoryDialog, { data: { assetId: asset.id }, width: '720px', maxWidth: '95vw' });
  }

  private async open<D>(component: ComponentType<unknown>, data: D, done: string): Promise<void> {
    const result = await firstValueFrom(
      this.dialog.open<unknown, D, CustodyAnswer>(component, { data, width: '620px', maxWidth: '95vw' }).afterClosed(),
    );
    if (!result) {
      return;
    }
    if (wasAsked(result)) {
      await this.approvals.requested(result);
    } else {
      this.notify.info(result.voucher?.routedToWaste ? `${done} Its breakage is in Costing → Waste.` : done);
    }
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [assets, categories] = await Promise.all([firstValueFrom(this.api.assets(this.withFinancial())), firstValueFrom(this.api.categories())]);
      this.assets.set(assets);
      this.categories.set(categories);
    } catch (error) {
      this.error.set(errorMessage(error, 'The assets could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
