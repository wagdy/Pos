import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PortalAuth } from '../../core/portal/portal-auth';

// The HR tabs a manager gave an employee (صلاحيات الموظفين), in the portal: the portal's bar, back
// to their own profile, and HR & Payroll's own screens under it, only their tabs, nothing else of
// the till. They work them as an accountant does: what they change waits for a manager.
@Component({
  selector: 'app-portal-hr-frame',
  imports: [MatButtonModule, MatIconModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <header class="bar">
      <mat-icon>badge</mat-icon>
      <nav aria-label="Portal">
        <a mat-button routerLink="/my-profile">My profile <span class="ar">ملفي</span></a>
        <a mat-button routerLink="/hr" routerLinkActive="active">HR &amp; Payroll <span class="ar">الموارد البشرية</span></a>
      </nav>
      <span class="spacer"></span>
      <span class="who">{{ auth.session()?.name }}</span>
      <button mat-button (click)="auth.signOut()"><mat-icon>logout</mat-icon> Sign out</button>
    </header>
    <main class="page">
      <p class="muted small note">What you change here goes to a manager to approve. <span class="ar">ما تغيّره هنا يُرسل للمدير للموافقة</span></p>
      <router-outlet />
    </main>
  `,
  styles: `
    :host {
      display: block;
      min-height: 100dvh;
      background: var(--mat-sys-surface);
    }
    .bar {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 4px 8px;
      padding: 6px 12px;
      background: var(--mat-sys-primary);
      color: var(--mat-sys-on-primary);

      a,
      button {
        color: inherit;
      }
      a.active {
        background: color-mix(in srgb, var(--mat-sys-on-primary) 16%, transparent);
      }
      .who {
        font: var(--mat-sys-label-large);
      }
    }
    nav {
      display: flex;
      flex-wrap: wrap;
    }
    .spacer {
      flex: 1;
    }
    .page {
      max-width: 1400px;
      margin: 0 auto;
      padding: 12px 16px 24px;
      box-sizing: border-box;
    }
    .note {
      margin: 0 0 8px;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class PortalHrFrame {
  protected readonly auth = inject(PortalAuth);
}
