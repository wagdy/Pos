import { Injectable, computed, inject } from '@angular/core';
import { CanActivateFn, CanMatchFn, Router } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { homePath } from '../auth/guards';
import { Permissions } from '../auth/permissions';
import { PortalAuth } from '../portal/portal-auth';
import { HrTab, allHrTabs, hrTabs, hrTabsOf } from './hr-tabs';

// Which HR & Payroll tabs whoever is here holds (صلاحيات الموظفين): an employee's in the portal,
// from their token; someone of the till's, from /api/auth/me, every one for a manager, an
// accountant's as a manager ticked them. The tabs shown, and the screens a typed address opens,
// go by it; the API refuses the rest whatever is shown.
@Injectable({ providedIn: 'root' })
export class HrAccess {
  private readonly auth = inject(AuthService);
  private readonly portal = inject(PortalAuth);

  readonly tabs = computed<ReadonlySet<HrTab>>(() => {
    if (this.auth.employeeActing()) {
      return new Set(this.portal.hrTabs());
    }
    const me = this.auth.me();
    // An older API's /me says nothing of tabs: whoever sees HR sees it all, as before.
    return new Set(me?.hrTabs ? hrTabsOf(me.hrTabs) : this.auth.can(Permissions.HrView) ? allHrTabs : []);
  });

  readonly any = computed(() => this.tabs().size > 0);

  // Who gives the tabs: a manager of the till.
  readonly canGive = computed(() => !this.auth.employeeActing() && this.auth.can(Permissions.ApprovalDecide));

  has(tab: HrTab): boolean {
    return this.tabs().has(tab);
  }

  // The first tab held, under /hr: where HR opens.
  first(): string | null {
    return hrTabs.find((t) => this.has(t.tab))?.path ?? null;
  }

  // Out of HR, for one holding no tab: their profile in the portal, or their part of the till.
  outside(): string {
    return this.auth.employeeActing() ? '/my-profile' : homePath(this.auth);
  }
}

// /hr as the portal's: an employee signed in to it, and no one of the till. On the till, /hr is
// its own section, in its shell.
export const portalHrMatch: CanMatchFn = () => inject(AuthService).employeeActing();

// HR at all: a tab held; anyone else out of it.
export const hrGuard: CanActivateFn = () => {
  const access = inject(HrAccess);
  return access.any() ? true : inject(Router).parseUrl(access.outside());
};

// A tab of HR: whoever holds it; anyone else to the first tab they hold, or out of HR.
export const hrTabGuard =
  (tab: HrTab): CanActivateFn =>
  () => {
    const access = inject(HrAccess);
    if (access.has(tab)) {
      return true;
    }
    const first = access.first();
    return inject(Router).parseUrl(first ? `/hr/${first}` : access.outside());
  };

// The Permissions tab: a manager's.
export const hrGiveGuard: CanActivateFn = () => {
  const access = inject(HrAccess);
  return access.canGive() ? true : inject(Router).parseUrl(`/hr/${access.first() ?? 'now'}`);
};
