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

  // A tab of HR & Payroll's own held (Custody & Assets is a section of its own).
  readonly any = computed(() => hrTabs.some((t) => this.tabs().has(t.tab)));

  // A manager of the till: who gives the tabs, and decides what stays a manager's even for an
  // employee holding the tab (leaves, balances and their types; evaluations and disciplinary
  // actions).
  readonly manager = computed(() => !this.auth.employeeActing() && this.auth.can(Permissions.ApprovalDecide));

  // Who gives the tabs: a manager of the till.
  readonly canGive = this.manager;

  has(tab: HrTab): boolean {
    return this.tabs().has(tab);
  }

  // What only a manager does otherwise, in a tab: a manager, or an employee holding it in the
  // portal, who does it at once as the manager would. An accountant does not.
  canDecide(tab: HrTab): boolean {
    return this.auth.employeeActing() ? this.has(tab) : this.auth.can(Permissions.ApprovalDecide);
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
