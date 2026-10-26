import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PendingLeaves } from './pending-leaves';

// Leaves & Holidays (الإجازات والعطلات), HR's tab for time off: the leave requests waiting for a
// manager and this year's, everyone's balances a year, the leave types, and the public holidays.
// Seen by whoever holds the tab; what is decided on it is a manager's alone, but the holidays,
// which an employee holding the tab enters too.
@Component({
  selector: 'app-leaves-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <nav class="sub no-print" aria-label="Leaves and holidays">
      @for (tab of tabs; track tab.path) {
        <a [routerLink]="tab.path" routerLinkActive="active" [attr.aria-current]="active.isActive ? 'page' : null" #active="routerLinkActive">
          {{ tab.en }}<span class="inline-ar">{{ tab.ar }}</span>
          @if (tab.path === 'requests' && pending.count()) {
            <span class="count" [attr.aria-label]="pending.count() + ' waiting'">{{ pending.count() }}</span>
          }
        </a>
      }
    </nav>
    <router-outlet />
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .sub {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: 16px;

      a {
        display: inline-flex;
        align-items: center;
        padding: 6px 14px;
        border-radius: 999px;
        border: 1px solid var(--mat-sys-outline-variant);
        color: var(--mat-sys-on-surface);
        font: var(--mat-sys-label-large);
        text-decoration: none;

        &:hover {
          background: var(--mat-sys-surface-container-high);
        }
        &.active {
          border-color: transparent;
          background: var(--mat-sys-secondary-container);
          color: var(--mat-sys-on-secondary-container);
        }
      }
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
  `,
})
export class LeavesShell {
  protected readonly pending = inject(PendingLeaves);
  protected readonly tabs = [
    { path: 'requests', en: 'Pending approvals', ar: 'طلبات الإجازات' },
    { path: 'balances', en: 'Balances', ar: 'الأرصدة' },
    { path: 'types', en: 'Leave types', ar: 'أنواع الإجازات' },
    { path: 'holidays', en: 'Public holidays', ar: 'العطلات الرسمية' },
  ];
}
