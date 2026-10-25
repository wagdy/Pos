import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

const SessionKey = 'otantik-portal.session';

// An employee signed in to the self-service portal: kept apart from the till's staff sign-in, so
// the two never mix on one browser. The token reaches /api/portal alone (authInterceptor).
export interface PortalSession {
  token: string;
  expiresAtUtc: string;
  employeeId: string;
  name: string;
}

function readStored(): PortalSession | null {
  try {
    const stored = localStorage.getItem(SessionKey);
    return stored ? (JSON.parse(stored) as PortalSession) : null;
  } catch {
    return null;
  }
}

@Injectable({ providedIn: 'root' })
export class PortalAuth {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly _session = signal<PortalSession | null>(readStored());

  readonly session = this._session.asReadonly();
  readonly token = computed(() => this._session()?.token ?? null);
  // Until it runs out: the API says so too, and a 401 signs them out (authInterceptor).
  readonly isSignedIn = computed(() => {
    const session = this._session();
    return !!session && new Date(session.expiresAtUtc).getTime() > Date.now();
  });

  // Their mobile number and their PIN. Refused with the API's own words: a wrong PIN, or locked.
  async signIn(mobileNumber: string, pinCode: string): Promise<void> {
    const session = await firstValueFrom(
      this.http.post<PortalSession>('/api/auth/employee-login', { mobileNumber, pinCode }),
    );
    this._session.set(session);
    try {
      localStorage.setItem(SessionKey, JSON.stringify({ token: session.token, expiresAtUtc: session.expiresAtUtc, employeeId: session.employeeId, name: session.name }));
    } catch {
      // Signed in for this page only.
    }
  }

  // `expired` when the API has stopped taking the token: it ran out, their PIN changed, or they left.
  signOut(reason?: 'expired'): void {
    this._session.set(null);
    try {
      localStorage.removeItem(SessionKey);
    } catch {
      // Nothing was kept.
    }
    void this.router.navigate(['/portal'], { queryParams: reason ? { reason } : {} });
  }
}

// /my-profile: an employee signed in to the portal; anyone else to its sign-in.
export const portalGuard: CanActivateFn = () => (inject(PortalAuth).isSignedIn() ? true : inject(Router).createUrlTree(['/portal']));

// The portal's sign-in: one already signed in goes to their profile.
export const portalSignedOutGuard: CanActivateFn = () =>
  inject(PortalAuth).isSignedIn() ? inject(Router).createUrlTree(['/my-profile']) : true;
