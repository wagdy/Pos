import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, StockCountSummary } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';

// Physical counts of the stores: the one being filled in, if any, and the posted ones. The
// variance report measures between two posted counts, so the stores are counted at the same
// point each week, with the kitchen closed.
@Component({
  selector: 'app-counts-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, RouterLink],
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
          <a mat-button routerLink="/costing/variance"><mat-icon>compare_arrows</mat-icon> Variance since the last count</a>
        }
      </div>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Posted<span class="ar">تاريخ الترحيل</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num">Materials counted<span class="ar">الخامات المجرودة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (count of posted(); track count.id) {
              <tr class="clickable" (click)="open(count)">
                <td>{{ count.postedAtUtc | date: 'EEE d MMM y, HH:mm' }}</td>
                <td>{{ count.postedBy }}</td>
                <td class="num">{{ count.materials }}</td>
              </tr>
            } @empty {
              <tr><td colspan="3" class="muted">No counts posted yet. The first one is the opening stock the next is measured from.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
})
export class CountsPage {
  private readonly api = inject(CostingApi);
  private readonly router = inject(Router);

  protected readonly counts = signal<StockCountSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly draft = computed(() => this.counts().find((c) => c.status === 'Draft') ?? null);
  protected readonly posted = computed(() => this.counts().filter((c) => c.status === 'Posted'));

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

  private async load(): Promise<void> {
    try {
      this.counts.set(await firstValueFrom(this.api.stockCounts()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The stock counts could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
