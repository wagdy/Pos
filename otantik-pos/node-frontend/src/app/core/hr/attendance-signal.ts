import { Injectable, signal } from '@angular/core';

// Raised when the till says punches came or a day was put right (the hub's AttendanceChanged):
// what shows who is in reads again. Counts up, so every change is a new value.
@Injectable({ providedIn: 'root' })
export class AttendanceSignal {
  readonly changes = signal(0);

  changed(): void {
    this.changes.update((n) => n + 1);
  }
}
