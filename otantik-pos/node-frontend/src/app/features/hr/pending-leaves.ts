import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HrAccess } from '../../core/hr/hr-access';
import { LeavesApi } from '../../core/hr/leaves.api';

// The leave requests waiting for a manager: the count on the Leaves & Holidays tab, read again
// whenever one is asked for or decided. Only for whoever holds the tab.
@Injectable({ providedIn: 'root' })
export class PendingLeaves {
  private readonly api = inject(LeavesApi);
  private readonly access = inject(HrAccess);
  readonly count = signal(0);

  async refresh(): Promise<void> {
    if (!this.access.has('Holidays')) {
      this.count.set(0);
      return;
    }
    try {
      this.count.set((await firstValueFrom(this.api.requests({ status: 'Pending' }))).length);
    } catch {
      // Only a reminder: the tabs work without it.
    }
  }
}
