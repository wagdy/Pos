import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

// Leaves (الإجازات), as the till's API serves them (/api/leaves): the types, everyone's balances a
// year, and the requests a manager approves or rejects. Seen on HR's Leaves & Holidays tab;
// approving, rejecting, cancelling, a balance's days and the types are a manager's alone.
// Employees ask for their own in the portal (PortalApi).

// Paid: taken from a balance of days a year. Unpaid: no balance, a day's pay off each day.
export interface LeaveType {
  id: string;
  name: string;
  isPaid: boolean;
  defaultDaysPerYear: number;
  // A new hire's first year: their share of the days, by the days of the year left.
  proratedForNewHires: boolean;
  // Asked for or kept a balance of: not deleted, nor made paid or unpaid.
  inUse: boolean;
}

// An employee's days of a paid type in a year, in half days. Opened: kept, a manager's or a
// request's; otherwise what the year would open at. Pending: asked for, waiting for a manager.
export interface LeaveBalance {
  employeeId: string;
  employeeName: string;
  leaveTypeId: string;
  leaveTypeName: string;
  year: number;
  allocatedDays: number;
  usedDays: number;
  remainingDays: number;
  pendingDays: number;
  opened: boolean;
  allocatedBy: string | null;
}

export type LeaveRequestStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled';

export const leaveStatusLabels: Record<LeaveRequestStatus, { en: string; ar: string }> = {
  Pending: { en: 'Pending', ar: 'قيد الانتظار' },
  Approved: { en: 'Approved', ar: 'موافق عليها' },
  Rejected: { en: 'Rejected', ar: 'مرفوضة' },
  Cancelled: { en: 'Cancelled', ar: 'ملغاة' },
};

// Days: its working days; DaysTaken: those left standing once it was cut short (CancelledFrom
// on, CancelledDays given back).
export interface LeaveRequest {
  id: string;
  employeeId: string;
  employeeName: string;
  leaveTypeId: string;
  leaveTypeName: string;
  isPaid: boolean;
  startDate: string;
  endDate: string;
  days: number;
  daysTaken: number;
  status: LeaveRequestStatus;
  reason: string | null;
  managerNotes: string | null;
  requestedBy: string;
  requestedAtUtc: string;
  decidedBy: string | null;
  decidedAtUtc: string | null;
  cancelledFrom: string | null;
  cancelledDays: number;
  cancelledBy: string | null;
  cancelledAtUtc: string | null;
  cancellationNotes: string | null;
}

// The working days a leave's dates hold for an employee: not their weekly days off, not public
// holidays, only while employed.
export interface LeaveDays {
  days: number;
  dates: string[];
}

export interface LeaveTypeInput {
  name: string;
  isPaid: boolean;
  defaultDaysPerYear: number;
  proratedForNewHires: boolean;
}

export interface LeaveRequestInput {
  employeeId: string;
  leaveTypeId: string;
  startDate: string;
  endDate: string;
  reason: string | null;
}

// A request's status as its badge says it: an approved one cut short says so.
export function leaveStatus(r: Pick<LeaveRequest, 'status' | 'cancelledFrom'>): { key: string; en: string; ar: string } {
  if (r.status === 'Approved' && r.cancelledFrom) {
    return { key: 'CutShort', en: 'Cut short', ar: 'قُطعت' };
  }
  return { key: r.status, ...leaveStatusLabels[r.status] };
}

// "1 day", "10.5 days".
export function leaveDays(days: number): string {
  return `${days} ${days === 1 ? 'day' : 'days'}`;
}

@Injectable({ providedIn: 'root' })
export class LeavesApi {
  private readonly http = inject(HttpClient);

  types(): Observable<LeaveType[]> {
    return this.http.get<LeaveType[]>('/api/leaves/types');
  }

  // Added under this id, or changed: a manager's.
  saveType(id: string, input: LeaveTypeInput): Observable<LeaveType> {
    return this.http.put<LeaveType>(`/api/leaves/types/${id}`, input);
  }

  deleteType(id: string): Observable<void> {
    return this.http.delete<void>(`/api/leaves/types/${id}`);
  }

  // A year's: everyone employed in it, or one; a row each paid type.
  balances(year: number, employeeId?: string): Observable<LeaveBalance[]> {
    return this.http.get<LeaveBalance[]>('/api/leaves/balances', { params: employeeId ? { year, employeeId } : { year } });
  }

  // A balance's days, in half days: a manager's.
  setBalance(employeeId: string, leaveTypeId: string, year: number, days: number): Observable<LeaveBalance> {
    return this.http.put<LeaveBalance>(`/api/leaves/balances/${employeeId}/${leaveTypeId}/${year}`, { days });
  }

  // The waiting ones (oldest first), or a year's (this year's unless given).
  requests(filter: { status?: LeaveRequestStatus; employeeId?: string; year?: number } = {}): Observable<LeaveRequest[]> {
    const params: Record<string, string | number> = {};
    if (filter.status) {
      params['status'] = filter.status;
    }
    if (filter.employeeId) {
      params['employeeId'] = filter.employeeId;
    }
    if (filter.year) {
      params['year'] = filter.year;
    }
    return this.http.get<LeaveRequest[]>('/api/leaves', { params });
  }

  days(employeeId: string, startDate: string, endDate: string): Observable<LeaveDays> {
    return this.http.get<LeaveDays>('/api/leaves/days', { params: { employeeId, startDate, endDate } });
  }

  // Asked for an employee by someone of HR; the id made here, so a retry finds the one made.
  submit(input: LeaveRequestInput, requestId: string = crypto.randomUUID()): Observable<LeaveRequest> {
    return this.http.post<LeaveRequest>('/api/leaves', { requestId, ...input });
  }

  approve(id: string, notes: string | null): Observable<LeaveRequest> {
    return this.http.post<LeaveRequest>(`/api/leaves/${id}/approve`, { notes });
  }

  reject(id: string, notes: string | null): Observable<LeaveRequest> {
    return this.http.post<LeaveRequest>(`/api/leaves/${id}/reject`, { notes });
  }

  // An approved one, from that day on: those days given back.
  cancel(id: string, from: string, notes: string | null): Observable<LeaveRequest> {
    return this.http.post<LeaveRequest>(`/api/leaves/${id}/cancel`, { from, notes });
  }
}
