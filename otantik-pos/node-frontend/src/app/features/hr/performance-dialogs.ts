import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import {
  DisciplinaryAction,
  DisciplinaryInput,
  EvaluationInput,
  PerformanceApi,
  PerformanceKpi,
  SeverityLevel,
  severityLabels,
  severityLevels,
  violationTypes,
} from '../../core/hr/performance.api';
import { MoneyPipe } from '../../core/ui/money.pipe';

const dialogStyles = `
  .wide {
    width: 100%;
  }
  .row {
    display: flex;
    gap: 12px;

    > * {
      flex: 1;
      min-width: 0;
    }
  }
  .ar {
    direction: rtl;
    unicode-bidi: isolate;
    margin-left: 6px;
    opacity: 0.75;
    font-size: 0.8em;
  }
  .muted {
    color: var(--mat-sys-on-surface-variant);
  }
  .small {
    font-size: 0.85em;
  }
  .problem {
    color: var(--mat-sys-error);
  }
  .facts {
    margin: 0 0 12px;
    padding: 8px 12px;
    border-radius: 8px;
    background: var(--mat-sys-surface-container);
  }
`;

function validDay(day: string | null | undefined): day is string {
  return /^\d{4}-\d{2}-\d{2}$/.test(day ?? '');
}

// ---- An evaluation ----

export interface EvaluationData {
  employeeName: string;
  // The KPIs in use, to score.
  kpis: PerformanceKpi[];
  today: string;
  hiredOn: string;
  submit: (input: EvaluationInput) => Observable<unknown>;
}

// A new evaluation (تقييم جديد), a manager's: each KPI in use scored 1 to 5, or left out, its
// total their average as a percent; dated today or before, while they were employed. Closes with
// true once recorded.
@Component({
  selector: 'app-evaluation-dialog',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Evaluate {{ data.employeeName }} <span class="ar">تقييم جديد</span></h2>
    <mat-dialog-content>
      <mat-form-field class="date">
        <mat-label>Day · التاريخ</mat-label>
        <input matInput type="date" [(ngModel)]="date" [max]="data.today" [min]="data.hiredOn" aria-label="Day" />
      </mat-form-field>
      @for (k of data.kpis; track k.id) {
        <div class="kpi">
          <span class="name">{{ k.name }}</span>
          <mat-button-toggle-group [ngModel]="scores()[k.id] ?? null" (ngModelChange)="score(k.id, $event)" [attr.aria-label]="k.name + ' score'" hideSingleSelectionIndicator>
            <mat-button-toggle [value]="null" class="skip" [attr.aria-label]="'Leave ' + k.name + ' out'">—</mat-button-toggle>
            @for (n of [1, 2, 3, 4, 5]; track n) {
              <mat-button-toggle [value]="n">{{ n }}</mat-button-toggle>
            }
          </mat-button-toggle-group>
        </div>
      } @empty {
        <p class="problem">No KPI is in use: add them under KPIs first.</p>
      }
      <p class="total" aria-live="polite">
        @if (total() !== null) {
          Total <strong>{{ total() }}%</strong><span class="muted small"> · the average of {{ scored() }} {{ scored() === 1 ? 'score' : 'scores' }}, out of 5</span>
        } @else {
          <span class="muted">Score at least one KPI: 1 is poor, 5 excellent.</span>
        }
      </p>
      <mat-form-field class="wide">
        <mat-label>Notes · ملاحظات</mat-label>
        <textarea matInput [(ngModel)]="notes" maxlength="1000" rows="3" aria-label="Notes"></textarea>
      </mat-form-field>
      <p class="muted small">They see it in the portal, read-only.</p>
      @if (problem(); as why) {
        <p class="problem" role="alert">{{ why }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="saving()">Record the evaluation</button>
    </mat-dialog-actions>
  `,
  styles: `
    ${dialogStyles}
    .kpi {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 6px 12px;
      padding: 6px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);

      .name {
        font: var(--mat-sys-body-large);
      }
    }
    .skip {
      color: var(--mat-sys-on-surface-variant);
    }
    .total {
      margin: 12px 0;
    }
  `,
})
export class EvaluationDialog {
  protected readonly data = inject<EvaluationData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<EvaluationDialog, boolean>);
  protected date = this.data.today;
  protected notes = '';
  protected readonly scores = signal<Record<string, number | null>>({});
  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  private readonly given = computed(() =>
    Object.entries(this.scores())
      .filter((entry): entry is [string, number] => entry[1] !== null)
      .map(([kpiId, score]) => ({ kpiId, score })),
  );
  protected readonly scored = computed(() => this.given().length);
  // As the till works it out: the average over 5, as a percent to one decimal place.
  protected readonly total = computed(() => {
    const given = this.given();
    return given.length ? Math.round((given.reduce((t, s) => t + s.score, 0) / given.length / 5) * 1000) / 10 : null;
  });

  protected score(kpiId: string, value: number | null): void {
    this.scores.update((all) => ({ ...all, [kpiId]: value }));
  }

  protected async save(): Promise<void> {
    if (!validDay(this.date) || this.date > this.data.today) {
      this.problem.set('Date it today or before.');
      return;
    }
    if (!this.given().length) {
      this.problem.set('Score at least one KPI.');
      return;
    }
    this.problem.set(null);
    this.saving.set(true);
    try {
      await firstValueFrom(this.data.submit({ date: this.date, scores: this.given(), notes: this.notes.trim() || null }));
      this.ref.close(true);
    } catch (error) {
      this.problem.set(errorMessage(error, 'The evaluation could not be recorded.'));
    } finally {
      this.saving.set(false);
    }
  }
}

// ---- A disciplinary action ----

export interface DisciplinaryData {
  employeeName: string;
  // The level that comes next in their record, chosen to start with.
  next: SeverityLevel;
  today: string;
  hiredOn: string;
  submit: (input: DisciplinaryInput) => Observable<unknown>;
}

// A warning or penalty issued (إنذار أو جزاء), a manager's: the violation, the level (the next in
// their record to start with), a penalty deduction's amount, taken off that month's pay through
// Corrections. Closes with true once issued.
@Component({
  selector: 'app-disciplinary-dialog',
  imports: [DatePipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Issue a warning to {{ data.employeeName }} <span class="ar">إصدار إنذار</span></h2>
    <mat-dialog-content>
      <div class="row">
        <mat-form-field>
          <mat-label>Day · التاريخ</mat-label>
          <input matInput type="date" [(ngModel)]="date" [max]="data.today" [min]="data.hiredOn" aria-label="Day" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Violation · المخالفة</mat-label>
          <input matInput [(ngModel)]="violation" maxlength="100" list="violation-types" placeholder="Absence" aria-label="Violation" />
          <datalist id="violation-types">
            @for (v of violations; track v.en) {
              <option [value]="v.en">{{ v.ar }}</option>
            }
          </datalist>
        </mat-form-field>
      </div>
      <mat-form-field class="wide">
        <mat-label>Severity · الدرجة</mat-label>
        <mat-select [ngModel]="level()" (ngModelChange)="level.set($event)" aria-label="Severity">
          @for (l of levels; track l) {
            <mat-option [value]="l">{{ labels[l].en }} · {{ labels[l].ar }}{{ l === data.next ? ' (next in line)' : '' }}</mat-option>
          }
        </mat-select>
        <mat-hint>Next in their record: {{ labels[data.next].en }}</mat-hint>
      </mat-form-field>
      @if (level() === 'PenaltyDeduction') {
        <mat-form-field class="wide amount">
          <mat-label>Penalty amount · مبلغ الجزاء (L.E)</mat-label>
          <input matInput type="number" min="0.01" step="0.01" [(ngModel)]="amount" aria-label="Penalty amount" />
          <mat-hint>Taken off their {{ (validDay(date) ? date : data.today) | date: 'MMMM' }} pay, and listed on Corrections</mat-hint>
        </mat-form-field>
      } @else if (level() === 'Termination') {
        <p class="facts small">Recorded here. Their leaving is the profile’s Deactivate: paid to their last day, nothing erased.</p>
      }
      <mat-form-field class="wide notes">
        <mat-label>Notes · ملاحظات</mat-label>
        <textarea matInput [(ngModel)]="notes" maxlength="1000" rows="3" aria-label="Notes"></textarea>
      </mat-form-field>
      <p class="muted small">They see it in the portal, read-only. Issued by mistake, it is withdrawn while its month’s pay is open.</p>
      @if (problem(); as why) {
        <p class="problem" role="alert">{{ why }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="saving()">Issue it</button>
    </mat-dialog-actions>
  `,
  styles: `
    ${dialogStyles}
    .amount,
    .notes {
      margin-top: 12px;
    }
  `,
})
export class DisciplinaryDialog {
  protected readonly data = inject<DisciplinaryData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<DisciplinaryDialog, boolean>);
  protected readonly labels = severityLabels;
  protected readonly levels = severityLevels;
  protected readonly violations = violationTypes;
  protected readonly validDay = validDay;
  protected date = this.data.today;
  protected violation = '';
  protected readonly level = signal<SeverityLevel>(this.data.next);
  protected amount: number | null = null;
  protected notes = '';
  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected async save(): Promise<void> {
    const penalty = this.level() === 'PenaltyDeduction';
    const amount = Number(this.amount);
    if (!validDay(this.date) || this.date > this.data.today) {
      this.problem.set('Date it today or before.');
    } else if (!this.violation.trim()) {
      this.problem.set('Say what the violation was.');
    } else if (penalty && (this.amount === null || !Number.isFinite(amount) || amount <= 0)) {
      this.problem.set('A penalty deduction has its amount.');
    } else if (penalty && !Number.isInteger(Math.round(amount * 1000) / 10)) {
      this.problem.set('Give the penalty’s amount to the piastre at most: 150.50.');
    } else {
      this.problem.set(null);
      this.saving.set(true);
      try {
        await firstValueFrom(
          this.data.submit({
            date: this.date,
            violationType: this.violation.trim(),
            severityLevel: this.level(),
            notes: this.notes.trim() || null,
            penaltyAmount: penalty ? amount : null,
          }),
        );
        this.ref.close(true);
      } catch (error) {
        this.problem.set(errorMessage(error, 'It could not be issued.'));
      } finally {
        this.saving.set(false);
      }
    }
  }
}

// ---- Withdrawing ----

// An action issued by mistake withdrawn (سحب الإنذار), with why: kept, marked, counting for
// nothing; a penalty deduction's penalty taken off Corrections and the pay. Closes with the reason.
@Component({
  selector: 'app-withdraw-action-dialog',
  imports: [DatePipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MoneyPipe],
  template: `
    <h2 mat-dialog-title>Withdraw the {{ labels[data.severityLevel].en.toLowerCase() }}? <span class="ar">سحب الإنذار</span></h2>
    <mat-dialog-content>
      <p class="facts">{{ data.violationType }} · {{ data.date | date: 'd MMM y' }}</p>
      @if (data.penaltyAmount !== null) {
        <p>Its penalty of <strong>{{ data.penaltyAmount | money }}</strong> comes off Corrections and the {{ data.date | date: 'MMMM' }} pay.</p>
      }
      <p class="muted">It stays on their record, marked withdrawn, and counts for nothing; their portal no longer shows it.</p>
      <mat-form-field class="wide">
        <mat-label>Why · السبب</mat-label>
        <textarea matInput [(ngModel)]="reason" maxlength="1000" rows="2" placeholder="Issued to the wrong employee" aria-label="Why"></textarea>
      </mat-form-field>
      @if (problem()) {
        <p class="problem" role="alert">Say why it is withdrawn.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Keep it</button>
      <button mat-flat-button (click)="save()">Withdraw it</button>
    </mat-dialog-actions>
  `,
  styles: dialogStyles,
})
export class WithdrawActionDialog {
  protected readonly data = inject<DisciplinaryAction>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<WithdrawActionDialog, string>);
  protected readonly labels = severityLabels;
  protected reason = '';
  protected readonly problem = signal(false);

  protected save(): void {
    if (!this.reason.trim()) {
      this.problem.set(true);
      return;
    }
    this.ref.close(this.reason.trim());
  }
}

// ---- The KPIs ----

// The KPIs (مؤشرات الأداء), a manager's: added and renamed; one that evaluations scored is
// retired, keeping its name in them, and brought back; one never scored deleted. Closes with
// true when anything changed.
@Component({
  selector: 'app-kpi-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <h2 mat-dialog-title>KPIs <span class="ar">مؤشرات الأداء</span></h2>
    <mat-dialog-content>
      <p class="muted">What an evaluation scores, 1 to 5 each: punctuality, hygiene, service.</p>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      <ul class="kpis">
        @for (k of kpis(); track k.id) {
          <li [class.retired]="!k.isActive">
            @if (editing() === k.id) {
              <mat-form-field subscriptSizing="dynamic" class="name-field">
                <input matInput [(ngModel)]="editName" maxlength="100" (keydown.enter)="rename(k)" aria-label="KPI name" />
              </mat-form-field>
              <button mat-icon-button (click)="rename(k)" aria-label="Save the name"><mat-icon>check</mat-icon></button>
              <button mat-icon-button (click)="editing.set(null)" aria-label="Keep the name"><mat-icon>close</mat-icon></button>
            } @else {
              <span class="name">{{ k.name }}@if (!k.isActive) { <span class="muted small"> · retired</span> }</span>
              <button mat-icon-button (click)="startRename(k)" matTooltip="Rename" [attr.aria-label]="'Rename ' + k.name"><mat-icon>edit</mat-icon></button>
              @if (k.isActive) {
                @if (k.inUse) {
                  <button mat-button (click)="act(api.retireKpi(k.id))" [attr.aria-label]="'Retire ' + k.name">Retire</button>
                } @else {
                  <button mat-button class="danger" (click)="act(api.deleteKpi(k.id))" [attr.aria-label]="'Delete ' + k.name">Delete</button>
                }
              } @else {
                <button mat-button (click)="act(api.restoreKpi(k.id))" [attr.aria-label]="'Bring back ' + k.name">Bring back</button>
              }
            }
          </li>
        } @empty {
          @if (!loading()) {
            <li class="muted">None yet.</li>
          }
        }
      </ul>
      <div class="add">
        <mat-form-field subscriptSizing="dynamic" class="name-field">
          <mat-label>New KPI · مؤشر جديد</mat-label>
          <input matInput [(ngModel)]="newName" maxlength="100" placeholder="Punctuality" (keydown.enter)="add()" aria-label="New KPI" />
        </mat-form-field>
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add</button>
      </div>
      @if (problem(); as why) {
        <p class="problem" role="alert">{{ why }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button [mat-dialog-close]="changed">Done</button>
    </mat-dialog-actions>
  `,
  styles: `
    ${dialogStyles}
    .kpis {
      list-style: none;
      margin: 0 0 12px;
      padding: 0;

      li {
        display: flex;
        align-items: center;
        gap: 4px;
        min-height: 48px;
        border-bottom: 1px solid var(--mat-sys-outline-variant);
      }
      .name {
        flex: 1;
      }
      .retired .name {
        color: var(--mat-sys-on-surface-variant);
      }
    }
    .name-field {
      flex: 1;
    }
    .add {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .danger {
      color: var(--mat-sys-error);
    }
  `,
})
export class KpiDialog {
  protected readonly api = inject(PerformanceApi);
  protected readonly kpis = signal<PerformanceKpi[]>([]);
  protected readonly loading = signal(true);
  protected readonly editing = signal<string | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected newName = '';
  protected editName = '';
  protected changed = false;

  constructor() {
    void this.load();
  }

  protected startRename(k: PerformanceKpi): void {
    this.editName = k.name;
    this.editing.set(k.id);
  }

  protected async add(): Promise<void> {
    const name = this.newName.trim();
    if (!name) {
      this.problem.set('Give the KPI its name.');
      return;
    }
    if (await this.act(this.api.saveKpi(crypto.randomUUID(), name))) {
      this.newName = '';
    }
  }

  protected async rename(k: PerformanceKpi): Promise<void> {
    const name = this.editName.trim();
    if (!name) {
      this.problem.set('Give the KPI its name.');
      return;
    }
    if (name === k.name || (await this.act(this.api.saveKpi(k.id, name)))) {
      this.editing.set(null);
    }
  }

  // Does it and reads the list again; a refusal is said as the till says it.
  protected async act(call: Observable<unknown>): Promise<boolean> {
    this.problem.set(null);
    try {
      await firstValueFrom(call);
      this.changed = true;
      await this.load();
      return true;
    } catch (error) {
      this.problem.set(errorMessage(error, 'It could not be saved.'));
      return false;
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.kpis.set(await firstValueFrom(this.api.kpis()));
    } catch (error) {
      this.problem.set(errorMessage(error, 'The KPIs could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
