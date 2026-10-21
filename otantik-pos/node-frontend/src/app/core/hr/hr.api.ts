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

// How an employee is paid (نوع العمالة): a monthly salary, or a daily wage for each day worked.
export type EmploymentType = 'Monthly' | 'Daily';

// A department of the restaurant (الأقسام), and how many are in it: employed, and everyone
// including those who left. General is always there.
export interface Department {
  id: string;
  name: string;
  isGeneral: boolean;
  employed: number;
  everyone: number;
}

// Someone on the payroll. A nine-hour shift from shiftStartsAt, or flexible hours with none. Paid
// monthly (a day's pay the salary over 30) or daily (their wage a day worked); an hour's a day's
// over 9.
export interface Employee {
  id: string;
  name: string;
  jobTitle: string;
  employmentType: EmploymentType;
  monthlySalary: number;
  dailyWage: number;
  departmentId: string;
  shiftStartsAt: string | null;
  shiftEndsAt: string | null;
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
  employmentType: EmploymentType;
  monthlySalary: number;
  dailyWage: number;
  departmentId: string;
  shiftStartsAt: string | null;
  daysOff: WeekDay[];
  hiredOn: string;
  deviceUserId: string | null;
  reason: string | null;
}

// Suspended: a punch-in left 18 hours without its punch-out, waiting for a manager. Holiday: a
// public holiday, everyone's paid day off; WorkedOnHoliday earns a holiday allowance.
export type DayStatus =
  | 'Open'
  | 'Worked'
  | 'Suspended'
  | 'Absent'
  | 'DayOff'
  | 'Leave'
  | 'WorkedOnDayOff'
  | 'NotEmployed'
  | 'Holiday'
  | 'WorkedOnHoliday';

export const dayStatusLabels: Record<DayStatus, { en: string; ar: string }> = {
  Open: { en: 'Today so far', ar: 'جارٍ' },
  Worked: { en: 'Worked', ar: 'حضور' },
  Suspended: { en: 'Suspended: no punch-out', ar: 'موقوف للمراجعة' },
  Absent: { en: 'Absent', ar: 'غياب' },
  DayOff: { en: 'Day off', ar: 'راحة' },
  Leave: { en: 'Leave', ar: 'إجازة' },
  WorkedOnDayOff: { en: 'Worked a day off', ar: 'عمل يوم راحة' },
  NotEmployed: { en: 'Not employed', ar: 'غير معيّن' },
  Holiday: { en: 'Public holiday', ar: 'عطلة رسمية' },
  WorkedOnHoliday: { en: 'Worked a holiday', ar: 'عمل يوم عطلة' },
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
  // None for flexible hours.
  shiftStartsAt: string | null;
  shiftEndsAt: string | null;
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
  // The public holiday the day is, by its name.
  holidayName: string | null;
  // When the day ends, and the next one's punches start.
  dayEndsAt: string;
}

// A device's punch that came after its month's pay was finalized, for a manager to see to.
export interface LatePunch {
  employee: Employee;
  day: string;
  month: string;
  finalizedAtUtc: string;
  punch: Punch;
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

// A command left for a device: waiting until it asks, sent, then answered (0 is done).
export interface DeviceCommand {
  id: string;
  number: number;
  kind: 'ReadUsers' | 'ReadAttendance';
  text: string;
  queuedBy: string;
  queuedAtUtc: string;
  sentAtUtc: string | null;
  answeredAtUtc: string | null;
  returnCode: number | null;
}

// A ZKTeco device that has called the till: refused until a manager approves it by name. Its
// newest commands first.
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
  commands: DeviceCommand[];
}

// Someone enrolled on a device, as it listed them: who on the till has their number, if anyone,
// and how many of their punches wait for someone to.
export interface DeviceUser {
  id: string;
  deviceSerialNumber: string;
  deviceName: string | null;
  deviceUserId: string;
  name: string;
  lastSeenAtUtc: string;
  employeeId: string | null;
  employeeName: string | null;
  waitingPunches: number;
}

export type AdjustmentKind = 'Incentive' | 'LeaveAllowance';

export const adjustmentLabels: Record<AdjustmentKind, { en: string; ar: string }> = {
  Incentive: { en: 'Incentive', ar: 'حوافز' },
  LeaveAllowance: { en: 'Leave allowance', ar: 'بدل إجازات' },
};

// Every figure of a month's slip. Net = base + incentives + leave allowances + days off worked +
// holidays worked + overtime − advances − last month's shortfall − lateness − absence −
// penalties. Days off and holidays worked earn their allowance by themselves: a day's pay from
// half a shift, their hours before.
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
  // Public holidays worked, and their holiday allowance.
  holidaysWorked: number;
  holidaysWorkedPay: number;
  // How the base pay was made: a monthly salary's share, or a daily wage for each day worked.
  employmentType: EmploymentType;
  dailyWage: number;
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
    holidaysWorkedPay: number;
  };
  slips: PayrollSlip[];
}

// A public holiday (عطلة رسمية): everyone's paid day off, from its first day to its last.
export interface PublicHoliday {
  id: string;
  name: string;
  firstDay: string;
  lastDay: string;
  days: number;
  setBy: string;
  setAtUtc: string;
}

// Egypt's official holidays that fall on the same date each year, and Sham El-Nessim, the Monday
// after Coptic Easter: offered for a year, to be entered as the government announces them (it
// moves some to a Thursday). The Eids, Arafat, the Hijri New Year and the Prophet's Birthday
// follow the moon, and are entered once announced.
export function egyptFixedHolidays(year: number): { name: string; ar: string; day: string }[] {
  const on = (month: number, date: number) => `${year}-${String(month).padStart(2, '0')}-${String(date).padStart(2, '0')}`;
  return [
    { name: 'Coptic Christmas', ar: 'عيد الميلاد المجيد', day: on(1, 7) },
    { name: 'January 25 Revolution and Police Day', ar: 'ثورة 25 يناير وعيد الشرطة', day: on(1, 25) },
    { name: 'Sinai Liberation Day', ar: 'عيد تحرير سيناء', day: on(4, 25) },
    { name: 'Sham El-Nessim', ar: 'شم النسيم', day: shamElNessim(year) },
    { name: 'Labour Day', ar: 'عيد العمال', day: on(5, 1) },
    { name: 'June 30 Revolution', ar: 'ثورة 30 يونيو', day: on(6, 30) },
    { name: 'July 23 Revolution', ar: 'ثورة 23 يوليو', day: on(7, 23) },
    { name: 'Armed Forces Day', ar: 'عيد القوات المسلحة', day: on(10, 6) },
  ].sort((a, b) => a.day.localeCompare(b.day));
}

// The Monday after Orthodox Easter: Easter by the Julian calendar (Meeus), moved to the Gregorian
// one, 13 days on from 1900 to 2099.
export function shamElNessim(year: number): string {
  const a = year % 4;
  const b = year % 7;
  const c = year % 19;
  const d = (19 * c + 15) % 30;
  const e = (2 * a + 4 * b - d + 34) % 7;
  const month = Math.floor((d + e + 114) / 31);
  const date = ((d + e + 114) % 31) + 1;
  const monday = new Date(Date.UTC(year, month - 1, date + 13 + 1));
  return monday.toISOString().slice(0, 10);
}

@Injectable({ providedIn: 'root' })
export class HrApi {
  private readonly http = inject(HttpClient);

  employees(): Observable<Employee[]> {
    return this.http.get<Employee[]>('/api/hr/employees');
  }

  departments(): Observable<Department[]> {
    return this.http.get<Department[]>('/api/hr/departments');
  }

  // Added under this id, or renamed: a manager's.
  saveDepartment(id: string, name: string): Observable<Department> {
    return this.http.put<Department>(`/api/hr/departments/${id}`, { name });
  }

  // Deleted, its employees moved first to `moveTo` when it has any: a manager's.
  deleteDepartment(id: string, moveTo: string | null): Observable<void> {
    return this.http.delete<void>(`/api/hr/departments/${id}`, { params: moveTo ? { moveTo } : {} });
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

  holidays(year: number): Observable<PublicHoliday[]> {
    return this.http.get<PublicHoliday[]>('/api/hr/holidays', { params: { year } });
  }

  // Added under this id, or changed: a manager's.
  saveHoliday(id: string, name: string, firstDay: string, lastDay: string): Observable<PublicHoliday> {
    return this.http.put<PublicHoliday>(`/api/hr/holidays/${id}`, { name, firstDay, lastDay });
  }

  deleteHoliday(id: string): Observable<void> {
    return this.http.delete<void>(`/api/hr/holidays/${id}`);
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

  // A command left for the device, taken the next time it asks: its users, or its attendance
  // from a day read again.
  readDeviceUsers(id: string): Observable<DeviceCommand> {
    return this.http.post<DeviceCommand>(`/api/hr/devices/${id}/read-users`, {});
  }

  readDeviceAttendance(id: string, from: string, to: string): Observable<DeviceCommand> {
    return this.http.post<DeviceCommand>(`/api/hr/devices/${id}/read-attendance`, { from, to });
  }

  deviceUsers(): Observable<DeviceUser[]> {
    return this.http.get<DeviceUser[]>('/api/hr/device-users');
  }

  latePunches(): Observable<LatePunch[]> {
    return this.http.get<LatePunch[]>('/api/hr/attendance/late-punches');
  }

  settleLatePunches(punchIds: string[]): Observable<{ settled: number }> {
    return this.http.post<{ settled: number }>('/api/hr/attendance/late-punches/settle', { punchIds });
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

// A day's shift as "16:00–01:00 +1", or "Flexible" for flexible hours.
export function shift(day: Pick<AttendanceDay, 'date' | 'shiftStartsAt' | 'shiftEndsAt'>): string {
  return day.shiftStartsAt && day.shiftEndsAt ? `${clock(day.shiftStartsAt)}–${clock(day.shiftEndsAt, day.date)}` : 'Flexible';
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
