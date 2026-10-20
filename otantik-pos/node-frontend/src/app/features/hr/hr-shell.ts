import { Component } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// HR & Payroll (الموارد البشرية والرواتب): the employees, and the attendance devices that send their
// punches. Managers and accountants (HrView); an accountant's changes go to a manager. Advances are
// paid in Financials and in the daily batch, from the safe or a day's takings.
@Component({
  selector: 'app-hr-shell',
  imports: [MatTabsModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <header class="head">
      <h1>HR &amp; Payroll <span class="inline-ar">الموارد البشرية والرواتب</span></h1>
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
  styleUrl: '../costing/costing.scss',
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
export class HrShell {
  protected readonly tabs = [
    { path: 'employees', en: 'Employees', ar: 'الموظفين' },
    { path: 'devices', en: 'Attendance devices', ar: 'أجهزة البصمة' },
  ];
}
