import { Component } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// Custody & Assets (العهدة والأصول): what the restaurant owns and whose custody each thing is in.
// Its categories, the assets themselves (those bought on purchase invoices too, with Link with
// Financials on), and the vouchers that hand them out and take them back. Whoever sees HR sees
// it; an accountant's changes go to a manager.
@Component({
  selector: 'app-custody-shell',
  imports: [MatTabsModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <header class="head">
      <h1>Custody &amp; Assets <span class="inline-ar">العهدة والأصول</span></h1>
    </header>
    <nav mat-tab-nav-bar [tabPanel]="panel" mat-stretch-tabs="false" mat-align-tabs="start" class="no-print">
      @for (tab of tabs; track tab.path) {
        <a mat-tab-link [routerLink]="tab.path" routerLinkActive #active="routerLinkActive" [active]="active.isActive">
          {{ tab.en }}<span class="inline-ar">{{ tab.ar }}</span>
        </a>
      }
    </nav>
    <mat-tab-nav-panel #panel>
      <div class="body"><router-outlet /></div>
    </mat-tab-nav-panel>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .head h1 {
      margin: 0 0 8px;
      font: var(--mat-sys-headline-small);
    }
    .body {
      padding-top: 16px;
    }
  `,
})
export class CustodyShell {
  protected readonly tabs = [
    { path: 'categories', en: 'Categories', ar: 'الفئات' },
    { path: 'assets', en: 'Assets', ar: 'الأصول' },
    { path: 'vouchers', en: 'Vouchers', ar: 'الأذونات' },
  ];
}
