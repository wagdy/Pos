import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { Me, SignInOption, SignInResponse } from '../api/models';
import { Permission } from './permissions';

const TokenKey = 'otantik-till.token';

// Who is at this till, and what they may do.
//
// The role and permissions come from GET /api/auth/me and nowhere else: not from the sign-in
// response, not from the token, not from anything stored in the browser. The stored token is
// only a candidate until the API has said whose it is, so a role changed or revoked in the
// delivery system takes effect here the next time /me is asked.
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly _token = signal<string | null>(readStoredToken());
  private readonly _me = signal<Me | null>(null);
  private readonly permissions = computed(() => new Set(this._me()?.permissions ?? []));

  readonly token = this._token.asReadonly();
  readonly me = this._me.asReadonly();
  readonly isSignedIn = computed(() => this._me() !== null);

  can(permission: Permission): boolean {
    return this.permissions().has(permission);
  }

  canAny(permissions: readonly Permission[]): boolean {
    return permissions.some((permission) => this.can(permission));
  }

  // At start-up: confirm a stored token with the API. A 401 means it is no good; anything else
  // (the till server down) leaves it for the sign-in screen to try again.
  async restore(): Promise<void> {
    if (this._token() === null) {
      return;
    }
    try {
      await this.loadMe();
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.clear();
      }
    }
  }

  // The sign-in screen: active cashiers, managers and admins who have a PIN.
  signInOptions(): Observable<SignInOption[]> {
    return this.http.get<SignInOption[]>('/api/auth/staff');
  }

  async signIn(staffId: string, pin: string): Promise<Me> {
    const response = await firstValueFrom(
      this.http.post<SignInResponse>('/api/auth/sign-in', { staffId, pin }),
    );
    this.store(response.token);
    return this.loadMe();
  }

  // `expired` when the API has stopped accepting the token: the shift ran out, or the account
  // was deactivated or given another role in the delivery system.
  signOut(reason?: 'expired'): void {
    this.clear();
    void this.router.navigate(['/sign-in'], { queryParams: reason ? { reason } : {} });
  }

  private async loadMe(): Promise<Me> {
    const me = await firstValueFrom(this.http.get<Me>('/api/auth/me'));
    this._me.set(me);
    return me;
  }

  private store(token: string): void {
    this._token.set(token);
    try {
      localStorage.setItem(TokenKey, token);
    } catch {
      // Private browsing or storage switched off: signed in for this tab only.
    }
  }

  private clear(): void {
    this._token.set(null);
    this._me.set(null);
    try {
      localStorage.removeItem(TokenKey);
    } catch {
      // Nothing stored to remove.
    }
  }
}

function readStoredToken(): string | null {
  try {
    return localStorage.getItem(TokenKey);
  } catch {
    return null;
  }
}
