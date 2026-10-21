import { Component, inject } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SuspendedCount } from './suspended-count';

// HR & Payroll (الموارد البشرية والرواتب): the employees, their attendance and its corrections, the
// monthly payroll, the public holidays, and the attendance devices that send their punches. The Corrections tab counts
// the days waiting for a manager's punch-out. Managers and accountants (HrView); an
// accountant's changes go to a manager. Advances are paid in Financials and in the daily batch,
// from the safe or a day's takings.
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
          @if (tab.path === 'corrections' && suspended.count()) {
            <span class="count" [attr.aria-label]="suspended.count() + ' suspended'">{{ suspended.count() }}</span>
          }
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
    .count {
      margin-left: 8px;
      min-width: 20px;
      padding: 0 6px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      line-height: 20px;
      text-align: center;
      background: var(--till-warn);
      color: #000;
    }
    @media print {
      .head {
        display: none;
      }
    }
  `,
})
export class HrShell {
  protected readonly suspended = inject(SuspendedCount);
  protected readonly tabs = [
    { path: 'employees', en: 'Employees', ar: 'الموظفين' },
    { path: 'attendance', en: 'Attendance', ar: 'الحضور والانصراف' },
    { path: 'corrections', en: 'Corrections', ar: 'مراجعة وجزاءات' },
    { path: 'payroll', en: 'Payroll', ar: 'مسير الرواتب' },
    { path: 'holidays', en: 'Holidays', ar: 'العطلات الرسمية' },
    { path: 'devices', en: 'Attendance devices', ar: 'أجهزة البصمة' },
  ];

  constructor() {
    void this.suspended.refresh();
  }
}
