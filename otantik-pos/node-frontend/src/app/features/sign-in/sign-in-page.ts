import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { SignInOption } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessage } from '../../core/http/error-message';

// Tap your name, then your PIN. Works with the internet down: the accounts are the delivery
// system's, copied to the till server, and the PINs live only there.
@Component({
  selector: 'app-sign-in-page',
  imports: [MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule],
  templateUrl: './sign-in-page.html',
  styleUrl: './sign-in-page.scss',
  host: { '(document:keydown)': 'onKey($event)' },
})
export class SignInPage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  // ?reason=expired, when the API stopped accepting the last session.
  readonly reason = input<string>();

  protected readonly staff = signal<SignInOption[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly selected = signal<SignInOption | null>(null);
  protected readonly pin = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly dots = computed(() => Array.from({ length: Math.max(4, this.pin().length) }, (_, i) => i < this.pin().length));
  protected readonly digits = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];

  ngOnInit(): void {
    void this.loadStaff();
  }

  protected async loadStaff(): Promise<void> {
    this.loadError.set(null);
    try {
      this.staff.set(await firstValueFrom(this.auth.signInOptions()));
    } catch (error) {
      this.loadError.set(errorMessage(error, 'The staff list could not be loaded.'));
    }
  }

  protected choose(person: SignInOption): void {
    this.selected.set(person);
    this.pin.set('');
    this.error.set(null);
  }

  protected back(): void {
    this.selected.set(null);
    this.pin.set('');
    this.error.set(null);
  }

  protected press(digit: string): void {
    if (this.pin().length < 8) {
      this.pin.update((pin) => pin + digit);
      this.error.set(null);
    }
  }

  protected erase(): void {
    this.pin.update((pin) => pin.slice(0, -1));
  }

  protected async submit(): Promise<void> {
    const person = this.selected();
    if (!person || this.pin().length < 4 || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.signIn(person.id, this.pin());
      await this.router.navigate(['/orders']);
    } catch (error) {
      this.error.set(errorMessage(error, 'Sign-in failed.'));
      this.pin.set('');
    } finally {
      this.busy.set(false);
    }
  }

  protected initials(name: string): string {
    return name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]!.toUpperCase())
      .join('');
  }

  // A keyboard works as well as the keypad.
  protected onKey(event: KeyboardEvent): void {
    if (!this.selected()) {
      return;
    }
    if (/^[0-9]$/.test(event.key)) {
      this.press(event.key);
    } else if (event.key === 'Backspace') {
      this.erase();
    } else if (event.key === 'Enter') {
      void this.submit();
    } else if (event.key === 'Escape') {
      this.back();
    }
  }
}
