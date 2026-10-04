import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { errorMessage } from '../http/error-message';

// One place for the short messages at the bottom of the screen.
@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snackBar = inject(MatSnackBar);

  info(message: string): void {
    this.snackBar.open(message, 'OK', { duration: 3500 });
  }

  // Something arrived that the cashier did not do: kept up longer, and dismissed by hand.
  attention(message: string): void {
    this.snackBar.open(message, 'Got it', { duration: 8000, panelClass: 'snack-attention' });
  }

  error(error: unknown, fallback?: string): void {
    this.snackBar.open(errorMessage(error, fallback), 'Dismiss', { duration: 6000, panelClass: 'snack-error' });
  }
}
