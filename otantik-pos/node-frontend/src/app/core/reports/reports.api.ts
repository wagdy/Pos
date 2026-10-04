import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { DayReport } from '../api/models';

// The till's reports, from the till server's own database: there with or without the internet.
@Injectable({ providedIn: 'root' })
export class ReportsApi {
  private readonly http = inject(HttpClient);

  // Today's business day when no date is given; otherwise yyyy-MM-dd.
  day(date: string | null): Observable<DayReport> {
    return this.http.get<DayReport>('/api/reports/day', { params: date ? { date } : {} });
  }
}
