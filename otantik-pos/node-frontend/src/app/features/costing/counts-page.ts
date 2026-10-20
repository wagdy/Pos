import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, ApprovalRequest, StockCountSummary } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { Approvals, pendingLabel } from './approvals';

// Physical counts of the stores: the one being filled in, if any, and the posted ones. The
// variance report measures between two posted counts, so the stores are counted at the same
// point each week, with the kitchen closed.
@Component({
  selector: 'app-counts-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, RouterLink],
  template: `
    <p class="note">
      Count the stores at the same point each week, with the kitchen closed. The variance report measures between two posted counts.
      <span class="inline-ar">الجرد أسبوعيًا في نفس التوقيت والمطبخ مغلق.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        @if (draft(); as d) {
          <span>
            <strong>{{ d.startedBy || 'Someone' }}</strong> started a count at {{ d.startedAtUtc | date: 'd MMM, HH:mm' }}:
            {{ d.materials }} {{ d.materials === 1 ? 'material' : 'materials' }} counted so far.
          </span>
          <a mat-flat-button [routerLink]="['/costing/counts', d.id]"><mat-icon>edit_note</mat-icon> Continue the count</a>
        } @else {
          <button mat-flat-button (click)="start()"><mat-icon>add</mat-icon> Start a count</button>
        }
        <span class="spacer"></span>
        @if (posted().length >= 2) {
          <a mat-button routerLink="/costing/reports/variance"><mat-icon>compare_arrows</mat-icon> Variance since the last count</a>
        }
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Posted<span class="ar">تاريخ الترحيل</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num">Materials counted<span class="ar">الخامات المجرودة</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (count of done(); track count.id) {
              <tr class="clickable" [class.voided]="count.status === 'Voided'" [class.pending-deletion]="!!pendingOf(count)" (click)="open(count)">
                <td>
                  {{ count.postedAtUtc | date: 'EEE d MMM y, HH:mm' }}
                  @if (count.status === 'Voided') {
                    <div><span class="voided-badge">Voided<span class="ar">ملغى</span></span>
                      <span class="muted small"> by {{ count.voidedBy }}, {{ count.voidedAtUtc | date: 'd MMM' }}</span></div>
                  } @else if (pendingOf(count); as p) {
                    <div><span class="pending-badge">{{ pendingLabel(p).en }}<span class="ar">{{ pendingLabel(p).ar }}</span></span>
                      <span class="muted small"> by {{ p.requestedBy }}</span></div>
                  }
                </td>
                <td>{{ count.postedBy }}</td>
                <td class="num">{{ count.materials }}</td>
                <td class="num" (click)="$event.stopPropagation()">
                  @if (count.status === 'Posted') {
                    @if (pendingOf(count)) {
                      <mat-icon class="locked" matTooltip="Waiting for a manager's approval" aria-label="Locked: deletion requested">lock_clock</mat-icon>
                    } @else {
                      <button mat-icon-button class="danger" (click)="remove(count)" matTooltip="Delete (void)" aria-label="Delete this count">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="muted">No counts posted yet. The first one is the opening stock the next is measured from.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .danger {
      color: var(--mat-sys-error);
    }
    .small {
      font-size: 0.85em;
    }
    tr.voided td {
      color: var(--mat-sys-on-surface-variant);
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class CountsPage {
  protected readonly pendingLabel = pendingLabel;
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);
  private readonly approvals = inject(Approvals);

  protected readonly counts = signal<StockCountSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly draft = computed(() => this.counts().find((c) => c.status === 'Draft') ?? null);
  protected readonly posted = computed(() => this.counts().filter((c) => c.status === 'Posted'));
  // Posted, and voided since: kept, marked so.
  protected readonly done = computed(() => this.counts().filter((c) => c.status !== 'Draft'));

  constructor() {
    void this.load();
  }

  // A new count's id is made here; it is saved on its first entry.
  protected start(): void {
    void this.router.navigate(['/costing/counts', crypto.randomUUID()]);
  }

  protected open(count: StockCountSummary): void {
    void this.router.navigate(['/costing/counts', count.id]);
  }

  protected pendingOf(count: StockCountSummary): ApprovalRequest | undefined {
    return this.approvals.pendingFor(count.id);
  }

  // Voided for a manager (its adjustments undone); asked of one for anyone else.
  protected async remove(count: StockCountSummary): Promise<void> {
    const what = `the count of ${new Date(count.postedAtUtc!).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}`;
    if ((await this.approvals.delete('StockCount', count.id, what)) === 'Applied') {
      await this.load();
    }
  }

  private async load(): Promise<void> {
    void this.approvals.refresh();
    try {
      this.counts.set(await firstValueFrom(this.api.stockCounts()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The stock counts could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
