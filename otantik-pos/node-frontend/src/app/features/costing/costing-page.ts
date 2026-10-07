import { Component } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// The manager's costing section: what ingredients and dishes cost, and the food cost reports.
// Managers only (CostingView); a cashier neither sees the link nor gets the data.
@Component({
  selector: 'app-costing-page',
  imports: [MatTabsModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <header class="head">
      <h1>Costing <span class="inline-ar">التكاليف</span></h1>
    </header>
    <nav mat-tab-nav-bar [tabPanel]="panel" class="no-print">
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
  styleUrl: './costing.scss',
  styles: `
    .head h1 {
      margin: 0 0 8px;
      font: var(--mat-sys-headline-small);
    }
    .body {
      padding-top: 16px;
    }
    @media print {
      .head {
        display: none;
      }
    }
  `,
})
export class CostingPage {
  protected readonly tabs = [
    { path: 'materials', en: 'Raw materials', ar: 'الخامات' },
    { path: 'purchases', en: 'Purchases', ar: 'المشتريات' },
    { path: 'waste', en: 'Waste', ar: 'الهالك' },
    { path: 'counts', en: 'Stock counts', ar: 'الجرد' },
    { path: 'recipes', en: 'Recipes', ar: 'الوصفات' },
    { path: 'theoretical', en: 'Theoretical cost', ar: 'التكلفة النظرية' },
    { path: 'variance', en: 'Variance', ar: 'الانحراف' },
    { path: 'kpis', en: 'KPIs', ar: 'المؤشرات' },
    { path: 'break-even', en: 'Break-even', ar: 'نقطة التعادل' },
    { path: 'settings', en: 'Settings', ar: 'الإعدادات' },
  ];
}
