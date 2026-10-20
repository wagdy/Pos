import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApprovalResult } from '../costing/costing.api';

// HR & Payroll (الموارد البشرية والرواتب), as the till's API serves it (/api/hr). Times of day are
// the restaurant's wall clock, sent and read without a zone: '2026-10-12T10:00:00', '10:00:00'.

export type WeekDay = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';

// Saturday first, as the restaurant's week starts.
export const weekDays: { day: WeekDay; en: string; ar: string }[] = [
  { day: 'Saturday', en: 'Sat', ar: 'السبت' },
  { day: 'Sunday', en: 'Sun', ar: 'الأحد' },
  { day: 'Monday', en: 'Mon', ar: 'الاثنين' },
  { day: 'Tuesday', en: 'Tue', ar: 'الثلاثاء' },
  { day: 'Wednesday', en: 'Wed', ar: 'الأربعاء' },
  { day: 'Thursday', en: 'Thu', ar: 'الخميس' },
  { day: 'Friday', en: 'Fri', ar: 'الجمعة' },
];

// Someone on the payroll. A nine-hour shift from shiftStartsAt; a day's pay is the salary over 30,
// an hour's a day's over 9.
export interface Employee {
  id: string;
  name: string;
  jobTitle: string;
  monthlySalary: number;
  shiftStartsAt: string;
  shiftEndsAt: string;
  daysOff: WeekDay[];
  deviceUserId: string | null;
  hiredOn: string;
  leftOn: string | null;
  isActive: boolean;
  dailyRate: number;
  hourlyRate: number;
}

// An employee as entered. requestId is made here, once per save.
export interface EmployeeInput {
  requestId: string;
  name: string;
  jobTitle: string;
  monthlySalary: number;
  shiftStartsAt: string;
  daysOff: WeekDay[];
  hiredOn: string;
  deviceUserId: string | null;
  reason: string | null;
}

// Suspended: a punch-in left 18 hours without its punch-out, waiting for a manager.
export type DayStatus = 'Open' | 'Worked' | 'Suspended' | 'Absent' | 'DayOff' | 'Leave' | 'WorkedOnDayOff' | 'NotEmployed';

export const dayStatusLabels: Record<DayStatus, { en: string; ar: string }> = {
  Open: { en: 'Today so far', ar: 'جارٍ' },
  Worked: { en: 'Worked', ar: 'حضور' },
  Suspended: { en: 'Suspended: no punch-out', ar: 'موقوف للمراجعة' },
  Absent: { en: 'Absent', ar: 'غياب' },
  DayOff: { en: 'Day off', ar: 'راحة' },
  Leave: { en: 'Leave', ar: 'إجازة' },
  WorkedOnDayOff: { en: 'Worked a day off', ar: 'عمل يوم راحة' },
  NotEmployed: { en: 'Not employed', ar: 'غير معيّن' },
};

// WorkingDay: a weekly day off worked in exchange for another, counted as a normal shift.
export type DayMarkKind = 'Leave' | 'DayOff' | 'WorkingDay';

export interface Punch {
  id: string;
  employeeId: string | null;
  source: 'Device' | 'Manual';
  deviceSerialNumber: string | null;
  deviceUserId: string | null;
  punchedAt: string;
  deviceStatus: number | null;
  verifyMode: number | null;
  reason: string | null;
  recordedBy: string | null;
  isIgnored: boolean;
  ignoreReason: string | null;
  ignoredBy: string | null;
}

export interface AttendanceDay {
  date: string;
  status: DayStatus;
  shiftStartsAt: string;
  shiftEndsAt: string;
  firstIn: string | null;
  lastOut: string | null;
  workedMinutes: number;
  lateMinutes: number;
  earlyLeaveMinutes: number;
  awayMinutes: number;
  overtimeMinutes: number;
  shortMinutes: number;
  isWeeklyDayOff: boolean;
  mark: DayMarkKind | null;
  markNotes: string | null;
  punches: Punch[];
  // A manager's administrative penalty on the day (جزاء إداري), in hours, and why.
  penaltyHours: number;
  penaltyReason: string | null;
  penaltySetBy: string | null;
}

export interface AttendancePolicy {
  graceMinutes: number;
  doubleScanMinutes: number;
}

// One employee's day, as everyone's day and the suspended days list them.
export interface EmployeeDay {
  employee: Employee;
  day: AttendanceDay;
}

export interface AttendanceDayReport {
  date: string;
  policy: AttendancePolicy;
  totals: { present: number; late: number; absent: number; missingPunch: number; offOrOnLeave: number; overtimeMinutes: number };
  employees: EmployeeDay[];
  unassigned: Punch[];
}

// A manager's administrative penalty (جزاء إداري) as the month's list shows it: hours, and what
// they take off the pay at the employee's hourly rate.
export interface AdministrativePenalty {
  employeeId: string;
  employeeName: string;
  date: string;
  hours: number;
  hourlyRate: number;
  amount: number;
  reason: string;
  setBy: string;
  setAtUtc: string;
}

export interface Timesheet {
  employee: Employee;
  month: string;
  policy: AttendancePolicy;
  totals: {
    daysWorked: number;
    workedMinutes: number;
    lateMinutes: number;
    earlyLeaveMinutes: number;
    awayMinutes: number;
    overtimeMinutes: number;
    absentDays: number;
    missingPunchDays: number;
  };
  days: AttendanceDay[];
}

// A ZKTeco device that has called the till: refused until a manager approves it by name.
export interface AttendanceDevice {
  id: string;
  serialNumber: string;
  name: string | null;
  isApproved: boolean;
  approvedBy: string | null;
  approvedAtUtc: string | null;
  firstSeenAtUtc: string;
  lastSeenAtUtc: string;
  lastAddress: string | null;
}

export type AdjustmentKind = 'Incentive' | 'LeaveAllowance';

export const adjustmentLabels: Record<AdjustmentKind, { en: string; ar: string }> = {
  Incentive: { en: 'Incentive', ar: 'حوافز' },
  LeaveAllowance: { en: 'Leave allowance', ar: 'بدل إجازات' },
};

// Every figure of a month's slip. Net = base + incentives + leave allowances + days off worked +
// overtime − advances − last month's shortfall − lateness − absence. Days off worked earn their
// allowance by themselves: a day's pay from half a shift, their hours before.
export interface PayrollFigures {
  monthlySalary: number;
  daysInMonth: number;
  daysEmployed: number;
  baseSalary: number;
  incentives: number;
  leaveAllowances: number;
  overtimeMinutes: number;
  overtimePay: number;
  shortMinutes: number;
  latenessPenalty: number;
  absentDays: number;
  absencePenalty: number;
  advances: number;
  broughtForward: number;
  daysWorked: number;
  daysOffWorked: number;
  missingPunchDays: number;
  openDays: number;
  daysOffWorkedPay: number;
  // A manager's administrative penalties, in hours, and their deduction at the hourly rate.
  administrativePenaltyHours: number;
  administrativePenalty: number;
  gross: number;
  deductions: number;
  net: number;
  netPayable: number;
  carriedForward: number;
  wagesCost: number;
}

export interface PayrollAdjustment {
  id: string;
  employeeId: string;
  month: string;
  kind: AdjustmentKind;
  amount: number;
  reason: string | null;
  source: 'Manual' | 'Automatic';
  forDay: string | null;
  recordedBy: string;
  recordedAtUtc: string;
  voidedAtUtc: string | null;
  voidedBy: string | null;
}

export interface PayrollCash {
  id: string;
  kind: 'EmployeeAdvance' | 'SalaryPayment';
  date: string;
  amount: number;
  fromTakingsOf: string | null;
  notes: string | null;
  recordedBy: string;
  approvedBy: string | null;
  voidedAtUtc: string | null;
  voidedBy: string | null;
}

export interface PayrollSlip {
  employee: Employee;
  month: string;
  // Null: a draft worked out now, not saved yet.
  status: 'Draft' | 'Finalized' | null;
  figures: PayrollFigures;
  salaryPaid: number;
  salaryOwed: number;
  calculatedAtUtc: string | null;
  finalizedAtUtc: string | null;
  finalizedBy: string | null;
  adjustments: PayrollAdjustment[];
  advances: PayrollCash[];
  salaries: PayrollCash[];
}

export interface PayrollMonth {
  month: string;
  totals: {
    baseSalaries: number;
    incentives: number;
    leaveAllowances: number;
    overtimePay: number;
    advances: number;
    broughtForward: number;
    latenessPenalties: number;
    absencePenalties: number;
    netPayable: number;
    carriedForward: number;
    wagesCost: number;
    salaryPaid: number;
    daysOffWorkedPay: number;
  };
  slips: PayrollSlip[];
}

@Injectable({ providedIn: 'root' })
export class HrApi {
  private readonly http = inject(HttpClient);

  employees(): Observable<Employee[]> {
    return this.http.get<Employee[]>('/api/hr/employees');
  }

  // Added under this id, or changed: made at once for a manager (the employee); asked of one for
  // anyone else (the request).
  saveEmployee(id: string, input: EmployeeInput): Observable<Employee | ApprovalResult> {
    return this.http.put<Employee | ApprovalResult>(`/api/hr/employees/${id}`, input);
  }

  leave(id: string, lastDay: string, reason: string | null): Observable<Employee | ApprovalResult> {
    return this.http.post<Employee | ApprovalResult>(`/api/hr/employees/${id}/leave`, { requestId: crypto.randomUUID(), lastDay, reason });
  }

  comeBack(id: string, returnsOn: string, reason: string | null): Observable<Employee | ApprovalResult> {
    return this.http.post<Employee | ApprovalResult>(`/api/hr/employees/${id}/return`, { requestId: crypto.randomUUID(), returnsOn, reason });
  }

  attendance(date: string): Observable<AttendanceDayReport> {
    return this.http.get<AttendanceDayReport>('/api/hr/attendance', { params: { date } });
  }

  timesheet(employeeId: string, month: string): Observable<Timesheet> {
    return this.http.get<Timesheet>(`/api/hr/employees/${employeeId}/timesheet`, { params: { month } });
  }

  addPunch(employeeId: string, punchedAt: string, reason: string): Observable<Punch> {
    return this.http.post<Punch>('/api/hr/punches', { punchId: crypto.randomUUID(), employeeId, punchedAt, reason });
  }

  ignorePunch(id: string, reason: string): Observable<Punch> {
    return this.http.post<Punch>(`/api/hr/punches/${id}/ignore`, { reason });
  }

  restorePunch(id: string): Observable<Punch> {
    return this.http.post<Punch>(`/api/hr/punches/${id}/restore`, {});
  }

  markDay(employeeId: string, date: string, kind: DayMarkKind | null, notes: string | null): Observable<void> {
    return this.http.put<void>(`/api/hr/employees/${employeeId}/days/${date}`, { kind, notes });
  }

  // The days suspended for a manager, a punch-in left 18 hours without its punch-out: everyone's,
  // from `from` (the first of last month unless given) to today, oldest first.
  suspendedDays(from?: string): Observable<EmployeeDay[]> {
    return this.http.get<EmployeeDay[]>('/api/hr/attendance/suspended', { params: from ? { from } : {} });
  }

  // A suspended day resolved by the manager's punch-out: the day as it is worked out from it.
  resolveDay(employeeId: string, date: string, punchedOutAt: string, reason: string): Observable<AttendanceDay> {
    return this.http.post<AttendanceDay>('/api/hr/attendance/resolve', { punchId: crypto.randomUUID(), employeeId, date, punchedOutAt, reason });
  }

  // A day's administrative penalty set, or taken off with no hours.
  setPenalty(employeeId: string, date: string, hours: number | null, reason: string | null): Observable<void> {
    return this.http.put<void>(`/api/hr/employees/${employeeId}/days/${date}/penalty`, { hours, reason });
  }

  penalties(month: string): Observable<AdministrativePenalty[]> {
    return this.http.get<AdministrativePenalty[]>('/api/hr/penalties', { params: { month } });
  }

  settings(): Observable<AttendancePolicy> {
    return this.http.get<AttendancePolicy>('/api/hr/settings');
  }

  saveSettings(policy: AttendancePolicy): Observable<AttendancePolicy> {
    return this.http.put<AttendancePolicy>('/api/hr/settings', policy);
  }

  devices(): Observable<AttendanceDevice[]> {
    return this.http.get<AttendanceDevice[]>('/api/hr/devices');
  }

  approveDevice(id: string, name: string): Observable<AttendanceDevice> {
    return this.http.post<AttendanceDevice>(`/api/hr/devices/${id}/approve`, { name });
  }

  revokeDevice(id: string): Observable<AttendanceDevice> {
    return this.http.post<AttendanceDevice>(`/api/hr/devices/${id}/revoke`, {});
  }

  payroll(month: string): Observable<PayrollMonth> {
    return this.http.get<PayrollMonth>('/api/hr/payroll', { params: { month } });
  }

  calculatePayroll(month: string): Observable<PayrollMonth> {
    return this.http.post<PayrollMonth>('/api/hr/payroll/calculate', {}, { params: { month } });
  }

  finalizeSlip(employeeId: string, month: string): Observable<PayrollSlip> {
    return this.http.post<PayrollSlip>(`/api/hr/payroll/${employeeId}/finalize`, {}, { params: { month } });
  }

  addAdjustment(employeeId: string, month: string, kind: AdjustmentKind, amount: number, reason: string | null, forDay: string | null = null): Observable<PayrollAdjustment> {
    return this.http.post<PayrollAdjustment>('/api/hr/payroll/adjustments', { adjustmentId: crypto.randomUUID(), employeeId, month, kind, amount, reason, forDay });
  }

  voidAdjustment(id: string): Observable<PayrollAdjustment> {
    return this.http.post<PayrollAdjustment>(`/api/hr/payroll/adjustments/${id}/void`, {});
  }
}

// Minutes as hours and minutes: "9h 08m", "45m", "—" for none.
export function duration(minutes: number): string {
  if (!minutes) {
    return '—';
  }
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h ? `${h}h ${String(m).padStart(2, '0')}m` : `${m}m`;
}

// A date ('2026-10-19') as its day: "Mon 19 Oct".
export function dayName(date: string): string {
  return new Date(`${date}T12:00:00`).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' });
}

// Minutes from one wall-clock time to another, read without a time zone, so no clock change can
// move them.
export function minutesBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}Z`) - Date.parse(`${from}Z`)) / 60_000);
}

// A wall-clock time as the till sends it ('2026-10-19T16:12:05'), as "16:12"; a day after `day`
// as "01:20 +1". Read from the text, never through Date, so no time zone can move it.
export function clock(time: string | null, day?: string): string {
  if (!time) {
    return '—';
  }
  const hm = time.slice(11, 16);
  return day && time.slice(0, 10) > day ? `${hm} +1` : hm;
}
