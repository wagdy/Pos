import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { EmployeeIdPhoto, EmploymentType, PayrollSlip, WeekDay } from '../hr/hr.api';

// What the portal shows an employee of themselves: read-only, and only ever their own, from their
// token.
export interface PortalProfile {
  id: string;
  name: string;
  jobTitle: string;
  department: string;
  employmentType: EmploymentType;
  hiredOn: string;
  nationalId: string | null;
  mobileNumber: string | null;
  idPhoto: EmployeeIdPhoto | null;
  shiftStartsAt: string | null;
  shiftEndsAt: string | null;
  daysOff: WeekDay[];
}

// A line of their custody: what it is, its category and how many, those of one name and category
// added up. Never what it cost: the API sends no cost to the portal.
export interface EmployeeCustody {
  name: string;
  category: string | null;
  quantity: number;
}

@Injectable({ providedIn: 'root' })
export class PortalApi {
  private readonly http = inject(HttpClient);

  me(): Observable<PortalProfile> {
    return this.http.get<PortalProfile>('/api/portal/me');
  }

  idPhoto(): Observable<Blob> {
    return this.http.get('/api/portal/id-photo', { responseType: 'blob' });
  }

  // The months there is a payslip for, the latest first.
  months(): Observable<string[]> {
    return this.http.get<string[]>('/api/portal/payslip-months');
  }

  payslip(month: string): Observable<PayrollSlip> {
    return this.http.get<PayrollSlip>('/api/portal/payslip', { params: { month } });
  }

  custody(): Observable<EmployeeCustody[]> {
    return this.http.get<EmployeeCustody[]>('/api/portal/custody');
  }
}
