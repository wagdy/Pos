import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { Treasury } from './financials';

// Purchases: entering a supplier's invoice, the invoices entered, the suppliers they are from, and
// the categories everything is filed under. What was spent is under Reports. An employee's advance
// is paid from here too, as in Financials: out of the safe or a day's takings, and off their pay
// (or, naming no one, a lump sum given out on HR → Advances).
@Component({
  selector: 'app-purchases-shell',
  imports: [MatButtonModule, MatIconModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <nav class="sub-nav no-print" aria-label="Purchases">
      @for (tab of tabs; track tab.path) {
        <a mat-button [routerLink]="tab.path" routerLinkActive="active" ariaCurrentWhenActive="page">
          <mat-icon>{{ tab.icon }}</mat-icon> {{ tab.en }}<span class="inline-ar">{{ tab.ar }}</span>
        </a>
      }
      @if (canAdvance()) {
        <span class="spacer"></span>
        <button mat-stroked-button (click)="advance()"><mat-icon>request_quote</mat-icon> New advance <span class="inline-ar">سلفة جديدة</span></button>
      }
    </nav>
    <router-outlet />
  `,
  styleUrl: './costing.scss',
  styles: `
    .sub-nav {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 8px;
      margin-bottom: 16px;

      a.active {
        background: var(--mat-sys-secondary-container);
        color: var(--mat-sys-on-secondary-container);
      }
      .spacer {
        flex: 1;
      }
    }
  `,
})
export class PurchasesShell {
  private readonly auth = inject(AuthService);
  private readonly treasury = inject(Treasury);
  // Who may pay an advance: whoever manages HR, as in the daily batch.
  protected readonly canAdvance = computed(() => this.auth.can(Permissions.HrManage));

  protected async advance(): Promise<void> {
    await this.treasury.enter('EmployeeAdvance');
  }

  protected readonly tabs = [
    { path: 'new', icon: 'add_shopping_cart', en: 'New invoice', ar: 'فاتورة جديدة' },
    { path: 'batch', icon: 'library_add', en: 'Daily batch', ar: 'يومية المشتريات' },
    { path: 'invoices', icon: 'history', en: 'Invoices', ar: 'أرشيف الفواتير' },
    { path: 'suppliers', icon: 'local_shipping', en: 'Suppliers', ar: 'إدارة الموردين' },
    { path: 'categories', icon: 'category', en: 'Categories', ar: 'الأقسام' },
  ];
}
