import { Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// Purchases: entering a supplier's invoice, the invoices entered, the suppliers they are from, and
// the categories everything is filed under. What was spent is under Reports.
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
export class PurchasesShell {
  protected readonly tabs = [
    { path: 'new', icon: 'add_shopping_cart', en: 'New invoice', ar: 'فاتورة جديدة' },
    { path: 'batch', icon: 'library_add', en: 'Daily batch', ar: 'يومية المشتريات' },
    { path: 'invoices', icon: 'history', en: 'Invoices', ar: 'أرشيف الفواتير' },
    { path: 'suppliers', icon: 'local_shipping', en: 'Suppliers', ar: 'إدارة الموردين' },
    { path: 'categories', icon: 'category', en: 'Categories', ar: 'الأقسام' },
  ];
}
