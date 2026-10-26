import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

// Performance & Disciplinary (تقييم الأداء والإنذارات), as the till's API serves it
// (/api/performance): the KPIs, and each employee's evaluations and disciplinary actions with
// their record. Seen by whoever holds the Employees tab; recorded, issued and withdrawn by a
// manager alone. Employees see their own in the portal (PortalApi), withdrawn ones left out.

// A KPI a manager keeps (Punctuality, Hygiene, Service): scored 1 to 5. InUse: scored by an
// evaluation, so retired rather than deleted.
export interface PerformanceKpi {
  id: string;
  name: string;
  isActive: boolean;
  inUse: boolean;
}

export interface KpiScore {
  kpiId: string;
  // As it was named when scored.
  kpiName: string;
  score: number;
}

// TotalScore: the scores' average as a percent, to one decimal place (5, 4 and 3 is 80.0).
export interface Evaluation {
  id: string;
  employeeId: string;
  date: string;
  scores: KpiScore[];
  totalScore: number;
  notes: string | null;
  reviewerName: string;
  recordedAtUtc: string;
}

// In the order they escalate.
export type SeverityLevel = 'VerbalWarning' | 'FirstWritten' | 'SecondWritten' | 'PenaltyDeduction' | 'Termination';

export const severityLevels: readonly SeverityLevel[] = ['VerbalWarning', 'FirstWritten', 'SecondWritten', 'PenaltyDeduction', 'Termination'];

export const severityLabels: Record<SeverityLevel, { en: string; ar: string }> = {
  VerbalWarning: { en: 'Verbal warning', ar: 'إنذار شفهي' },
  FirstWritten: { en: 'First written warning', ar: 'إنذار كتابي أول' },
  SecondWritten: { en: 'Second written warning', ar: 'إنذار كتابي ثانٍ' },
  PenaltyDeduction: { en: 'Penalty deduction', ar: 'خصم جزاء' },
  Termination: { en: 'Termination', ar: 'فصل' },
};

// What a disciplinary action is usually for; any other is written as it is.
export const violationTypes: readonly { en: string; ar: string }[] = [
  { en: 'Absence', ar: 'غياب' },
  { en: 'Lateness', ar: 'تأخير' },
  { en: 'Negligence', ar: 'إهمال' },
  { en: 'Behaviour', ar: 'سلوك' },
  { en: 'Hygiene', ar: 'نظافة' },
];

// PenaltyAmount: a penalty deduction's alone. PenaltyPending: its penalty standing, the month's pay
// not finalized yet; once finalized it is on the payslip and the action is not withdrawn.
export interface DisciplinaryAction {
  id: string;
  employeeId: string;
  date: string;
  violationType: string;
  severityLevel: SeverityLevel;
  notes: string | null;
  penaltyAmount: number | null;
  issuerName: string;
  issuedAtUtc: string;
  isWithdrawn: boolean;
  withdrawnBy: string | null;
  withdrawnAtUtc: string | null;
  withdrawalReason: string | null;
  penaltyPending: boolean;
}

// Each level's count, withdrawn ones left out; their warnings in all, and the level that comes
// next: verbal, then first written, then second written, then a penalty deduction.
export interface DisciplinaryRecord {
  verbalWarnings: number;
  firstWritten: number;
  secondWritten: number;
  penaltyDeductions: number;
  terminations: number;
  warnings: number;
  next: SeverityLevel;
}

export interface EmployeePerformance {
  employeeId: string;
  employeeName: string;
  evaluations: Evaluation[];
  actions: DisciplinaryAction[];
  record: DisciplinaryRecord;
}

export interface EvaluationInput {
  date: string;
  scores: { kpiId: string; score: number }[];
  notes: string | null;
}

export interface DisciplinaryInput {
  date: string;
  violationType: string;
  severityLevel: SeverityLevel;
  notes: string | null;
  penaltyAmount: number | null;
}

// Withdrawn by a manager, with why: not once withdrawn, nor a penalty deduction whose month's pay
// is finalized, as it is on the payslip.
export function canWithdraw(a: DisciplinaryAction): boolean {
  return !a.isWithdrawn && (a.severityLevel !== 'PenaltyDeduction' || a.penaltyPending);
}

// The record's counts, those there are, in the order they escalate.
export function recordCounts(r: DisciplinaryRecord): { level: SeverityLevel; count: number }[] {
  return [
    { level: 'VerbalWarning' as const, count: r.verbalWarnings },
    { level: 'FirstWritten' as const, count: r.firstWritten },
    { level: 'SecondWritten' as const, count: r.secondWritten },
    { level: 'PenaltyDeduction' as const, count: r.penaltyDeductions },
    { level: 'Termination' as const, count: r.terminations },
  ];
}

// An employee's evaluations and actions in one list, the latest first.
export type PerformanceEntry = { kind: 'evaluation'; date: string; at: string; evaluation: Evaluation } | { kind: 'action'; date: string; at: string; action: DisciplinaryAction };

export function timeline(p: Pick<EmployeePerformance, 'evaluations' | 'actions'>): PerformanceEntry[] {
  return [
    ...p.evaluations.map((evaluation) => ({ kind: 'evaluation' as const, date: evaluation.date, at: evaluation.recordedAtUtc, evaluation })),
    ...p.actions.map((action) => ({ kind: 'action' as const, date: action.date, at: action.issuedAtUtc, action })),
  ].sort((a, b) => (a.date === b.date ? b.at.localeCompare(a.at) : b.date.localeCompare(a.date)));
}

@Injectable({ providedIn: 'root' })
export class PerformanceApi {
  private readonly http = inject(HttpClient);

  // Retired ones too.
  kpis(): Observable<PerformanceKpi[]> {
    return this.http.get<PerformanceKpi[]>('/api/performance/kpis');
  }

  // Added under this id, or renamed: a manager's.
  saveKpi(id: string, name: string): Observable<PerformanceKpi> {
    return this.http.put<PerformanceKpi>(`/api/performance/kpis/${id}`, { name });
  }

  retireKpi(id: string): Observable<PerformanceKpi> {
    return this.http.post<PerformanceKpi>(`/api/performance/kpis/${id}/retire`, {});
  }

  restoreKpi(id: string): Observable<PerformanceKpi> {
    return this.http.post<PerformanceKpi>(`/api/performance/kpis/${id}/restore`, {});
  }

  // While no evaluation scored it.
  deleteKpi(id: string): Observable<void> {
    return this.http.delete<void>(`/api/performance/kpis/${id}`);
  }

  employee(employeeId: string): Observable<EmployeePerformance> {
    return this.http.get<EmployeePerformance>(`/api/performance/employees/${employeeId}`);
  }

  // The id made here, so a retry finds the one made.
  recordEvaluation(employeeId: string, input: EvaluationInput, id: string = crypto.randomUUID()): Observable<Evaluation> {
    return this.http.post<Evaluation>(`/api/performance/employees/${employeeId}/evaluations`, { id, ...input });
  }

  issueAction(employeeId: string, input: DisciplinaryInput, id: string = crypto.randomUUID()): Observable<DisciplinaryAction> {
    return this.http.post<DisciplinaryAction>(`/api/performance/employees/${employeeId}/disciplinary`, { id, ...input });
  }

  withdraw(actionId: string, reason: string): Observable<DisciplinaryAction> {
    return this.http.post<DisciplinaryAction>(`/api/performance/disciplinary/${actionId}/withdraw`, { reason });
  }
}
