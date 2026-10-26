import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { EmployeeIdPhoto, EmploymentType, PayrollSlip, WeekDay } from '../hr/hr.api';
import { LeaveBalance, LeaveDays, LeaveRequest } from '../hr/leaves.api';
import { EmployeePerformance } from '../hr/performance.api';

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

// Their leaves this year: the balance of each paid type, their requests (the latest first), and
// the types they may ask for, paid and unpaid.
export interface EmployeeLeaves {
  year: number;
  balances: LeaveBalance[];
  requests: LeaveRequest[];
  types: { id: string; name: string; isPaid: boolean }[];
}

export interface PortalLeaveInput {
  leaveTypeId: string;
  startDate: string;
  endDate: string;
  reason: string | null;
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

  leaves(): Observable<EmployeeLeaves> {
    return this.http.get<EmployeeLeaves>('/api/portal/leaves');
  }

  // The working days a leave of theirs would take, before asking.
  leaveDays(startDate: string, endDate: string): Observable<LeaveDays> {
    return this.http.get<LeaveDays>('/api/portal/leaves/days', { params: { startDate, endDate } });
  }

  // Asked for, to wait for a manager; the id made here, so a retry finds the one made.
  requestLeave(input: PortalLeaveInput, requestId: string = crypto.randomUUID()): Observable<LeaveRequest> {
    return this.http.post<LeaveRequest>('/api/portal/leaves', { requestId, ...input });
  }

  // Their evaluations and disciplinary actions, withdrawn ones left out.
  performance(): Observable<EmployeePerformance> {
    return this.http.get<EmployeePerformance>('/api/portal/performance');
  }
}
