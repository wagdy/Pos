import { DatePipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { DisciplinaryAction, DisciplinaryRecord, EmployeePerformance, canWithdraw, recordCounts, severityLabels, timeline } from '../../core/hr/performance.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

// An employee's record as it reads (السجل): the counts of their disciplinary actions by level and
// the one that comes next, then their evaluations and actions, the latest first. An evaluation
// with its total as a percent and each KPI's score; an action with its level, its violation, and
// a penalty deduction's amount, said to be still to come off a payslip or taken off. Withdrawn
// ones are kept, marked, and count for nothing (the portal never has them). Read-only, but for
// withdrawing (canWithdraw): the profile's and the portal's alike.
@Component({
  selector: 'app-performance-timeline',
  imports: [DatePipe, MatButtonModule, MatIconModule, MoneyPipe],
  template: `
    @let r = record();
    <div class="record" aria-label="Disciplinary record">
      @for (c of counts(); track c.level) {
        <span class="count" [class.zero]="!c.count" [class]="c.level">
          <strong>{{ c.count }}</strong> {{ labels[c.level].en }}<span class="ar">{{ labels[c.level].ar }}</span>
        </span>
      }
    </div>
    <p class="summary">
      <strong>{{ r.warnings }}</strong> {{ r.warnings === 1 ? 'warning' : 'warnings' }} in all
      @if (showNext()) {
        · next in line: <strong>{{ labels[r.next].en }}</strong> <span class="inline-ar">{{ labels[r.next].ar }}</span>
      }
    </p>
    <ol class="timeline">
      @for (entry of entries(); track entry.kind + (entry.kind === 'evaluation' ? entry.evaluation.id : entry.action.id)) {
        @if (entry.kind === 'evaluation') {
          @let e = entry.evaluation;
          <li class="entry evaluation">
            <mat-icon class="kind" aria-hidden="true">insights</mat-icon>
            <div class="body">
              <div class="line">
                <strong>Evaluation</strong> <span class="inline-ar">تقييم</span>
                <span class="score" [class.good]="e.totalScore >= 80" [class.low]="e.totalScore < 60">{{ e.totalScore }}%</span>
                <span class="spacer"></span>
                <span class="muted small">{{ e.date | date: 'd MMM y' }}</span>
              </div>
              <div class="scores">
                @for (s of e.scores; track s.kpiId) {
                  <span class="kpi">{{ s.kpiName }} <strong>{{ s.score }}</strong>/5</span>
                }
              </div>
              @if (e.notes) {
                <p class="notes">{{ e.notes }}</p>
              }
              <div class="muted small">by {{ e.reviewerName }}</div>
            </div>
          </li>
        } @else {
          @let a = entry.action;
          <li class="entry action" [class.withdrawn]="a.isWithdrawn" [class]="a.severityLevel">
            <mat-icon class="kind" aria-hidden="true">{{ a.severityLevel === 'PenaltyDeduction' ? 'payments' : a.severityLevel === 'Termination' ? 'person_off' : 'gavel' }}</mat-icon>
            <div class="body">
              <div class="line">
                <span class="level">{{ labels[a.severityLevel].en }}<span class="ar">{{ labels[a.severityLevel].ar }}</span></span>
                <strong class="violation">{{ a.violationType }}</strong>
                @if (a.isWithdrawn) {
                  <span class="withdrawn-badge">Withdrawn<span class="ar">مسحوب</span></span>
                }
                <span class="spacer"></span>
                <span class="muted small">{{ a.date | date: 'd MMM y' }}</span>
              </div>
              @if (a.penaltyAmount !== null) {
                <div class="penalty">
                  <strong>{{ a.penaltyAmount | money }}</strong>
                  @if (a.isWithdrawn) {
                    <span class="muted"> · not deducted</span>
                  } @else if (a.penaltyPending) {
                    <span class="muted"> · to come off the {{ a.date | date: 'MMMM' }} payslip</span>
                  } @else {
                    <span class="muted"> · taken off the {{ a.date | date: 'MMMM' }} payslip</span>
                  }
                </div>
              }
              @if (a.notes) {
                <p class="notes">{{ a.notes }}</p>
              }
              <div class="muted small">by {{ a.issuerName }}</div>
              @if (a.isWithdrawn) {
                <div class="small withdrawal">Withdrawn by {{ a.withdrawnBy }}, {{ a.withdrawnAtUtc | date: 'd MMM y' }}: {{ a.withdrawalReason }}</div>
              } @else if (allowWithdraw() && canWithdraw(a)) {
                <button mat-button class="withdraw" (click)="withdraw.emit(a)" [attr.aria-label]="'Withdraw the ' + labels[a.severityLevel].en.toLowerCase() + ' of ' + (a.date | date: 'd MMM')">
                  <mat-icon>undo</mat-icon> Withdraw <span class="inline-ar">سحب</span>
                </button>
              }
            </div>
          </li>
        }
      } @empty {
        <li class="muted empty">{{ emptyText() }}</li>
      }
    </ol>
  `,
  styles: `
    :host {
      display: block;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 4px;
      opacity: 0.75;
      font-size: 0.85em;
    }
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .spacer {
      flex: 1;
    }
    .record {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .count {
      padding: 2px 10px;
      border-radius: 999px;
      font: var(--mat-sys-label-medium);
      background: var(--mat-sys-surface-container-high);

      &.zero {
        opacity: 0.6;
      }
      &.PenaltyDeduction:not(.zero),
      &.Termination:not(.zero) {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
        color: var(--mat-sys-error);
      }
      &.SecondWritten:not(.zero) {
        background: color-mix(in srgb, var(--till-warn) 24%, transparent);
      }
    }
    .summary {
      margin: 8px 0 12px;
    }
    .timeline {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 8px;
    }
    .entry {
      display: flex;
      gap: 10px;
      padding: 10px 12px;
      border-radius: 10px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      .kind {
        flex: none;
        color: var(--mat-sys-on-surface-variant);
      }
      .body {
        flex: 1;
        min-width: 0;
      }
      .line {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 4px 8px;
      }
      &.action .kind {
        color: var(--till-warn);
      }
      &.PenaltyDeduction .kind,
      &.Termination .kind {
        color: var(--mat-sys-error);
      }
      &.withdrawn {
        background: var(--mat-sys-surface-container-low);

        .violation,
        .level,
        .penalty strong {
          text-decoration: line-through;
          opacity: 0.7;
        }
      }
    }
    .level {
      padding: 1px 8px;
      border-radius: 6px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);
    }
    .score {
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-large);
      background: var(--mat-sys-surface-container-high);

      &.good {
        background: color-mix(in srgb, var(--till-ok, #2e7d32) 18%, transparent);
      }
      &.low {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
        color: var(--mat-sys-error);
      }
    }
    .scores {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 14px;
      margin-top: 4px;
    }
    .kpi strong {
      font-variant-numeric: tabular-nums;
    }
    .penalty {
      margin-top: 4px;
      font-variant-numeric: tabular-nums;
    }
    .notes {
      margin: 4px 0;
      white-space: pre-line;
    }
    .withdrawn-badge {
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
    .withdrawal {
      margin-top: 4px;
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }
    .withdraw {
      margin: 4px 0 0 -12px;
    }
    .empty {
      padding: 8px 0;
    }
  `,
})
export class PerformanceTimeline {
  readonly performance = input.required<Pick<EmployeePerformance, 'evaluations' | 'actions' | 'record'>>();
  // A manager's, on the profile.
  readonly allowWithdraw = input(false);
  // The next level said: on the profile, where the next action is issued.
  readonly showNext = input(true);
  readonly emptyText = input('No evaluations or disciplinary actions yet.');
  readonly withdraw = output<DisciplinaryAction>();

  protected readonly labels = severityLabels;
  protected readonly canWithdraw = canWithdraw;
  protected readonly record = computed<DisciplinaryRecord>(() => this.performance().record);
  protected readonly counts = computed(() => recordCounts(this.record()));
  protected readonly entries = computed(() => timeline(this.performance()));
}
