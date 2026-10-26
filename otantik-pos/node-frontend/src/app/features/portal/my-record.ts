import { Component, inject, signal } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { EmployeePerformance } from '../../core/hr/performance.api';
import { errorMessage } from '../../core/http/error-message';
import { PortalApi } from '../../core/portal/portal.api';
import { PerformanceTimeline } from '../hr/performance-timeline';

// The portal's My record (سجلي), read-only: their evaluations, their warnings and penalty
// deductions (a deduction said to be still to come off a payslip or taken off), the latest first.
// Withdrawn ones are never sent here.
@Component({
  selector: 'app-my-record',
  imports: [MatProgressBarModule, PerformanceTimeline],
  template: `
    <h2>My record <span class="ar">سجلي</span></h2>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as e) {
      <p class="error">{{ e }}</p>
    } @else if (performance(); as p) {
      <app-performance-timeline [performance]="p" [showNext]="false" emptyText="Nothing on your record: no evaluations or warnings." />
    }
  `,
  styles: `
    :host {
      display: block;
    }
    h2 {
      margin: 0 0 10px;
      font: var(--mat-sys-title-medium);
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class MyRecord {
  private readonly api = inject(PortalApi);
  protected readonly performance = signal<EmployeePerformance | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.performance.set(await firstValueFrom(this.api.performance()));
    } catch (error) {
      this.error.set(errorMessage(error, 'Your record could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
