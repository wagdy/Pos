import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HrAccess } from '../../core/hr/hr-access';
import { HrApi } from '../../core/hr/hr.api';

// What waits for a manager on Corrections: the days suspended since the first of last month, and
// the days with punches that came after their month's pay was finalized. The count on its tab,
// read again whenever attendance is put right. Only for whoever holds the Corrections tab.
@Injectable({ providedIn: 'root' })
export class CorrectionsCount {
  private readonly api = inject(HrApi);
  private readonly access = inject(HrAccess);
  readonly count = signal(0);

  async refresh(): Promise<void> {
    if (!this.access.has('Corrections')) {
      this.count.set(0);
      return;
    }
    try {
      const [days, late] = await Promise.all([firstValueFrom(this.api.suspendedDays()), firstValueFrom(this.api.latePunches())]);
      this.count.set(days.length + new Set(late.map((l) => `${l.employee.id} ${l.day}`)).size);
    } catch {
      // Only a reminder: the tabs work without it.
    }
  }
}
