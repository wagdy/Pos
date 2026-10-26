import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { HrAccess } from '../../core/hr/hr-access';
import { Employee } from '../../core/hr/hr.api';
import { DisciplinaryAction, EmployeePerformance, PerformanceApi, PerformanceKpi, severityLabels } from '../../core/hr/performance.api';
import { errorMessage } from '../../core/http/error-message';
import { NotifyService } from '../../core/ui/notify.service';
import { BusinessDay } from '../costing/financials';
import { DisciplinaryData, DisciplinaryDialog, EvaluationData, EvaluationDialog, KpiDialog, WithdrawActionDialog } from './performance-dialogs';
import { PerformanceTimeline } from './performance-timeline';

// The profile's Performance & Disciplinary (التقييم والإنذارات): the employee's record, their
// evaluations and disciplinary actions, the latest first. A manager evaluates them, issues a
// warning (the next level in their record to start with) or a penalty deduction, withdraws one
// issued by mistake, and keeps the KPIs evaluations score.
@Component({
  selector: 'app-performance-section',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, PerformanceTimeline],
  template: `
    <div class="head">
      <h3>Performance &amp; Disciplinary <span class="inline-ar">التقييم والإنذارات</span></h3>
      <span class="spacer"></span>
      @if (manager()) {
        <button mat-button (click)="manageKpis()"><mat-icon>tune</mat-icon> KPIs</button>
        @if (employee().isActive) {
          <button mat-stroked-button (click)="evaluate()"><mat-icon>add</mat-icon> New evaluation <span class="inline-ar">تقييم</span></button>
          <button mat-stroked-button class="warn" (click)="issue()"><mat-icon>add</mat-icon> Issue warning <span class="inline-ar">إنذار</span></button>
        }
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as e) {
      <p class="error">{{ e }}</p>
    } @else if (performance(); as p) {
      <app-performance-timeline [performance]="p" [allowWithdraw]="manager()" (withdraw)="withdraw($event)" />
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin-bottom: 10px;

      h3 {
        margin: 4px 0;
        font: var(--mat-sys-title-small);
        color: var(--mat-sys-on-surface-variant);
      }
    }
    .spacer {
      flex: 1;
    }
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class PerformanceSection {
  private readonly api = inject(PerformanceApi);
  private readonly access = inject(HrAccess);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);

  readonly employee = input.required<Employee>();
  // A disciplinary action issued or withdrawn: their pay may have changed with it.
  readonly changed = output<void>();
  // Evaluating, issuing and withdrawing are a manager's alone, even for an employee holding the tab.
  protected readonly manager = this.access.manager;
  protected readonly performance = signal<EmployeePerformance | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.employee().id;
      untracked(() => void this.load(id));
    });
  }

  protected async manageKpis(): Promise<void> {
    await firstValueFrom(this.dialog.open<KpiDialog, void, boolean>(KpiDialog, { width: '520px', maxWidth: '95vw' }).afterClosed());
  }

  protected async evaluate(): Promise<void> {
    const e = this.employee();
    let kpis: PerformanceKpi[];
    try {
      kpis = (await firstValueFrom(this.api.kpis())).filter((k) => k.isActive);
    } catch (error) {
      this.notify.error(error, 'The KPIs could not be loaded.');
      return;
    }
    if (!kpis.length) {
      this.notify.error(null, 'No KPI is in use yet: add them under KPIs first.');
      return;
    }
    const data: EvaluationData = {
      employeeName: e.name,
      kpis,
      today: await this.businessDay.today(),
      hiredOn: e.hiredOn,
      submit: (input) => this.api.recordEvaluation(e.id, input),
    };
    const done = await firstValueFrom(this.dialog.open<EvaluationDialog, EvaluationData, boolean>(EvaluationDialog, { data, width: '560px', maxWidth: '95vw' }).afterClosed());
    if (done) {
      this.notify.info(`${e.name}’s evaluation recorded.`);
      await this.load(e.id);
    }
  }

  protected async issue(): Promise<void> {
    const e = this.employee();
    const data: DisciplinaryData = {
      employeeName: e.name,
      next: this.performance()?.record.next ?? 'VerbalWarning',
      today: await this.businessDay.today(),
      hiredOn: e.hiredOn,
      submit: (input) => this.api.issueAction(e.id, input),
    };
    const done = await firstValueFrom(this.dialog.open<DisciplinaryDialog, DisciplinaryData, boolean>(DisciplinaryDialog, { data, width: '560px', maxWidth: '95vw' }).afterClosed());
    if (done) {
      this.notify.info(`Issued to ${e.name}.`);
      this.changed.emit();
      await this.load(e.id);
    }
  }

  protected async withdraw(action: DisciplinaryAction): Promise<void> {
    const reason = await firstValueFrom(
      this.dialog.open<WithdrawActionDialog, DisciplinaryAction, string>(WithdrawActionDialog, { data: action, width: '480px' }).afterClosed(),
    );
    if (!reason) {
      return;
    }
    try {
      await firstValueFrom(this.api.withdraw(action.id, reason));
      this.changed.emit();
      this.notify.info(
        `The ${severityLabels[action.severityLevel].en.toLowerCase()} is withdrawn${action.penaltyAmount !== null ? ': its penalty is off Corrections and the pay' : ''}.`,
      );
      await this.load(this.employee().id);
    } catch (error) {
      this.notify.error(error, 'It could not be withdrawn.');
    }
  }

  private async load(id: string): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const p = await firstValueFrom(this.api.employee(id));
      if (id === this.employee().id) {
        this.performance.set(p);
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'Their performance record could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
