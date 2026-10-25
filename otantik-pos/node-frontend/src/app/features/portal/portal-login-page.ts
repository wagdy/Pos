import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { errorMessage } from '../../core/http/error-message';
import { PortalAuth } from '../../core/portal/portal-auth';

// The employee portal's sign-in (بوابة الموظف): their mobile number and the PIN a manager gave
// them. Apart from the till's: nothing of the till is here, and it opens nothing of the till.
@Component({
  selector: 'app-portal-login-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule],
  template: `
    <main class="page">
      <form class="card" (ngSubmit)="signIn()">
        <header>
          <mat-icon>badge</mat-icon>
          <div>
            <h1>My profile <span class="ar">ملفي</span></h1>
            <p class="muted">Your details, your payslips and your custody. <span class="ar">بياناتك وقسيمة راتبك وعهدتك</span></p>
          </div>
        </header>
        @if (reason() === 'expired') {
          <p class="notice">You were signed out. Sign in again. <span class="ar">سجّل الدخول مرة أخرى</span></p>
        }
        <mat-form-field class="wide">
          <mat-label>Mobile number · رقم الموبايل</mat-label>
          <input matInput name="mobile" type="tel" inputmode="tel" autocomplete="tel" [(ngModel)]="mobile" placeholder="01012345678" aria-label="Mobile number" />
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>PIN · الرقم السري</mat-label>
          <input matInput name="pin" type="password" inputmode="numeric" autocomplete="current-password" maxlength="6" [(ngModel)]="pin" aria-label="PIN" />
          <mat-hint>The one your manager gave you</mat-hint>
        </mat-form-field>
        @if (signingIn()) {
          <mat-progress-bar mode="indeterminate" />
        }
        @if (error(); as e) {
          <p class="error" role="alert">{{ e }}</p>
        }
        <!-- Not disabled while the fields fill: Enter straight after typing (or after the browser fills them in) still signs in. -->
        <button mat-flat-button type="submit" class="wide go" [disabled]="signingIn()">
          Sign in <span class="ar">دخول</span>
        </button>
      </form>
    </main>
  `,
  styles: `
    :host {
      display: block;
      min-height: 100dvh;
      background: var(--mat-sys-surface-container);
    }
    .page {
      display: grid;
      place-items: center;
      min-height: 100dvh;
      padding: 16px;
      box-sizing: border-box;
    }
    .card {
      display: grid;
      gap: 8px;
      width: min(100%, 400px);
      padding: 20px;
      box-sizing: border-box;
      border-radius: 16px;
      background: var(--mat-sys-surface);
      border: 1px solid var(--mat-sys-outline-variant);
    }
    header {
      display: flex;
      gap: 12px;
      align-items: flex-start;
      margin-bottom: 8px;

      mat-icon {
        width: 36px;
        height: 36px;
        font-size: 36px;
        color: var(--mat-sys-primary);
      }
      h1 {
        margin: 0;
        font: var(--mat-sys-headline-small);
      }
      p {
        margin: 4px 0 0;
      }
    }
    .wide {
      width: 100%;
    }
    .go {
      height: 48px;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.85em;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .notice {
      margin: 0;
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--mat-sys-secondary-container);
    }
    .error {
      margin: 0;
      color: var(--mat-sys-error);
    }
  `,
})
export class PortalLoginPage {
  private readonly auth = inject(PortalAuth);
  private readonly router = inject(Router);

  // ?reason=expired, when the portal signed them out.
  readonly reason = input<string | undefined>(undefined);

  protected mobile = '';
  protected pin = '';
  protected readonly signingIn = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async signIn(): Promise<void> {
    if (this.signingIn()) {
      return;
    }
    if (!this.mobile.trim() || !this.pin.trim()) {
      this.error.set('Enter your mobile number and your PIN.');
      return;
    }
    this.signingIn.set(true);
    this.error.set(null);
    try {
      await this.auth.signIn(this.mobile.trim(), this.pin.trim());
      await this.router.navigate(['/my-profile']);
    } catch (error) {
      this.pin = '';
      this.error.set(errorMessage(error, 'You could not be signed in. Try again.'));
    } finally {
      this.signingIn.set(false);
    }
  }
}
