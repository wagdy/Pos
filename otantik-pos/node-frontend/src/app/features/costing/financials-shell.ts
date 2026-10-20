import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';

// Financials (الماليات): the safe, where its money comes from, each day's cash reconciled with its
// sales, the expense vouchers paid out of the safe, what is owed to suppliers and the payments made
// to them, employees' advances (for whoever sees HR), and the tax and service settings.
@Component({
  selector: 'app-financials-shell',
  imports: [MatButtonModule, MatIconModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <nav class="sub-nav no-print" aria-label="Financials">
      @for (tab of tabs(); track tab.path) {
        <a mat-button [routerLink]="tab.path" routerLinkActive="active" ariaCurrentWhenActive="page">
          <mat-icon>{{ tab.icon }}</mat-icon> {{ tab.en }}<span class="inline-ar">{{ tab.ar }}</span>
        </a>
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
    }
  `,
})
export class FinancialsShell {
  private readonly auth = inject(AuthService);

  private readonly allTabs = [
    { path: 'safe', icon: 'account_balance_wallet', en: 'Safe', ar: 'الخزنة' },
    { path: 'funding', icon: 'savings', en: 'Funding sources', ar: 'مصادر التمويل' },
    { path: 'reconciliation', icon: 'rule', en: 'Reconciliation', ar: 'مطابقة الإيرادات' },
    { path: 'daily-cash', icon: 'calendar_view_day', en: 'Daily cash', ar: 'يومية الإيراد' },
    { path: 'expenses', icon: 'receipt', en: 'Expense vouchers', ar: 'إذن صرف مصروفات' },
    { path: 'suppliers', icon: 'account_balance', en: 'Supplier debts', ar: 'مديونيات الموردين' },
    { path: 'payments', icon: 'payments', en: 'Supplier payments', ar: 'أذونات صرف الموردين' },
    { path: 'advances', icon: 'request_quote', en: 'Advances', ar: 'السلفيات' },
    { path: 'tax-service', icon: 'percent', en: 'Tax & service', ar: 'الضريبة والخدمة' },
  ];

  // Advances say who is paid what: only for whoever sees HR.
  protected readonly tabs = computed(() => this.allTabs.filter((t) => t.path !== 'advances' || this.auth.can(Permissions.HrView)));
}
