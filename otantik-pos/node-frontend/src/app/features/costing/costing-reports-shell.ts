import { Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// Reports (التقارير): where the money went, stock and expenses, then the food cost reports, the
// month's KPIs and the break-even worksheet.
@Component({
  selector: 'app-costing-reports-shell',
  imports: [MatButtonModule, MatIconModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <nav class="sub-nav no-print" aria-label="Reports">
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
export class CostingReportsShell {
  protected readonly tabs = [
    { path: 'spending', icon: 'pie_chart', en: 'Spending analysis', ar: 'تحليل الإنفاق' },
    { path: 'theoretical', icon: 'calculate', en: 'Theoretical cost', ar: 'التكلفة النظرية' },
    { path: 'variance', icon: 'compare_arrows', en: 'Variance', ar: 'الانحراف' },
    { path: 'kpis', icon: 'insights', en: 'KPIs', ar: 'المؤشرات' },
    { path: 'break-even', icon: 'balance', en: 'Break-even', ar: 'نقطة التعادل' },
  ];
}
