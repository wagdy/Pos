import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HrApi } from '../../core/hr/hr.api';

// How many days wait for a manager's punch-out, since the first of last month: the count on the
// Corrections tab. Read again whenever attendance is put right.
@Injectable({ providedIn: 'root' })
export class SuspendedCount {
  private readonly api = inject(HrApi);
  readonly count = signal(0);

  async refresh(): Promise<void> {
    try {
      this.count.set((await firstValueFrom(this.api.suspendedDays())).length);
    } catch {
      // Only a reminder: the tabs work without it.
    }
  }
}
