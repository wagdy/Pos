import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CostingApi, MaterialCost, StockCount, countUnit } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

interface Section {
  category: string;
  materials: MaterialCost[];
}

// How long after the last entry the draft is saved: long enough to type a number, short enough
// that a tablet put down loses nothing.
const autosaveAfterMs = 1500;

// One count sheet. A draft is filled in shelf by shelf, in kg, litres and pieces, and saves
// itself as it goes; an empty field is not counted, which is not the same as 0. It does not
// show what the records expect: the counter counts the shelf rather than confirming a number.
// Posted, it shows what was counted against what the records said, and what the difference was worth.
@Component({
  selector: 'app-count-sheet-page',
  imports: [DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe, RouterLink],
  template: `
    <div class="toolbar no-print">
      <a mat-button routerLink="/costing/counts"><mat-icon>arrow_back</mat-icon> All counts</a>
      <span class="spacer"></span>
      @if (posted()) {
        <button mat-button (click)="print()"><mat-icon>print</mat-icon> Print</button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (posted(); as count) {
      <h2 class="title">
        Stock count, posted {{ count.postedAtUtc | date: 'EEE d MMM y, HH:mm' }}
        <span class="inline-ar">جرد مُرحّل</span>
      </h2>
      <p class="muted">Started by {{ count.startedBy }}, posted by {{ count.postedBy }}.</p>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Code<span class="ar">الكود</span></th>
              <th>Material<span class="ar">الخامة</span></th>
              <th class="num">Counted<span class="ar">المجرود</span></th>
              <th class="num">Records said<span class="ar">الرصيد الدفتري</span></th>
              <th class="num">Difference<span class="ar">الفرق</span></th>
              <th class="num">Worth<span class="ar">القيمة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (row of postedRows(); track row.id) {
              <tr [class.deficit]="row.difference < 0" [class.surplus]="row.difference > 0">
                <td class="code">{{ row.code ?? '' }}</td>
                <td>{{ row.name }}</td>
                <td class="num">{{ row.counted | number: '1.0-3' }} {{ row.unit }}</td>
                <td class="num">{{ row.book | number: '1.0-3' }} {{ row.unit }}</td>
                <td class="num">{{ row.difference > 0 ? '+' : '' }}{{ row.difference | number: '1.0-3' }} {{ row.unit }}</td>
                <td class="num">{{ row.value === null ? 'No price' : (row.value | money) }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td colspan="5">Net difference · صافي الفرق</td>
              <td class="num">{{ postedTotal() | money }}</td>
            </tr>
          </tfoot>
        </table>
      </div>
      <p class="note">
        Stock was set to what was counted. Why the records were out is in the
        <a routerLink="/costing/variance">variance report</a>, which sets it against sales, deliveries and waste.
      </p>
    } @else {
      <div class="toolbar sticky">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Find · بحث</mat-label>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
        </mat-form-field>
        <span class="progress">
          {{ countedCount() }} of {{ materials().length }} counted<span class="muted"> (empty is not counted, 0 is none left)</span>
          <span class="muted save-state">· {{ saveState() }}</span>
        </span>
        <span class="spacer"></span>
        @if (confirming() === 'post') {
          <span>
            Set stock to what was counted for {{ countedCount() }} {{ countedCount() === 1 ? 'material' : 'materials' }}?
            @if (materials().length - countedCount() > 0) {
              {{ materials().length - countedCount() }} not counted keep their stock.
            }
          </span>
          <button mat-button (click)="confirming.set(null)">Keep counting</button>
          <button mat-flat-button (click)="post()" [disabled]="busy()">Post the count</button>
        } @else if (confirming() === 'discard') {
          <span>Throw this count away?</span>
          <button mat-button (click)="confirming.set(null)">Keep it</button>
          <button mat-flat-button class="danger" (click)="discard()" [disabled]="busy()">Discard</button>
        } @else {
          @if (saved()) {
            <button mat-button (click)="confirming.set('discard')"><mat-icon>delete</mat-icon> Discard</button>
          }
          <button mat-flat-button (click)="confirming.set('post')" [disabled]="countedCount() === 0 || busy()">
            <mat-icon>fact_check</mat-icon> Post the count
          </button>
        }
      </div>
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
      @for (section of sections(); track section.category) {
        <h3 class="category">{{ section.category }}</h3>
        <div class="sheet">
          @for (m of section.materials; track m.id) {
            <label class="item" [class.done]="isCounted(m.id)">
              <span class="name">
                {{ m.name }}
                @if (m.code) {
                  <small class="muted">{{ m.code }}</small>
                }
              </span>
              <mat-form-field subscriptSizing="dynamic" class="qty">
                <input matInput type="number" min="0" step="any" inputmode="decimal" placeholder="—"
                  [ngModel]="counted()[m.id]" (ngModelChange)="enter(m.id, $event)" [attr.aria-label]="m.name + ', counted in ' + unitOf(m)" />
                <span matTextSuffix>&nbsp;{{ unitOf(m) }}</span>
              </mat-form-field>
            </label>
          }
        </div>
      } @empty {
        <p class="note">No raw materials match.</p>
      }
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .title {
      margin: 0 0 4px;
      font: var(--mat-sys-title-large);
    }
    .sticky {
      position: sticky;
      top: 64px;
      z-index: 2;
      padding: 8px 0;
      background: var(--mat-sys-surface);
    }
    .progress {
      font: var(--mat-sys-title-small);
    }
    .category {
      margin: 16px 0 8px;
      font: var(--mat-sys-title-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .sheet {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
      gap: 8px;
    }
    .item {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      padding: 6px 6px 6px 12px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      &.done {
        border-color: var(--mat-sys-primary);
      }
      .name {
        display: grid;
      }
    }
    .qty {
      width: 150px;
      flex: none;
    }
    .danger {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
    }
  `,
})
export class CountSheetPage {
  private readonly api = inject(CostingApi);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);

  // From the route: /costing/counts/:id. A new count's id, before its first save, finds nothing.
  readonly id = input.required<string>();

  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly count = signal<StockCount | null>(null);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly confirming = signal<'post' | 'discard' | null>(null);
  protected readonly search = signal('');

  // By material id, in kg, litres or pieces; null or absent is not counted.
  protected readonly counted = signal<Record<string, number | null>>({});
  // Whether the draft exists on the server yet.
  protected readonly saved = signal(false);
  protected readonly saveState = signal('Nothing entered yet');

  protected readonly posted = computed(() => (this.count()?.status === 'Posted' ? this.count() : null));

  protected readonly countedCount = computed(() => Object.values(this.counted()).filter((v) => this.valid(v)).length);

  protected readonly sections = computed<Section[]>(() => {
    const needle = this.search().trim().toLowerCase();
    const shown = this.materials().filter((m) => !needle || [m.name, m.code, m.category].some((v) => v?.toLowerCase().includes(needle)));
    const byCategory = new Map<string, MaterialCost[]>();
    for (const m of shown) {
      const category = m.category ?? 'Other · أخرى';
      byCategory.set(category, [...(byCategory.get(category) ?? []), m]);
    }
    return [...byCategory.entries()]
      .sort(([a], [b]) => (a.startsWith('Other') ? 1 : b.startsWith('Other') ? -1 : a.localeCompare(b)))
      .map(([category, materials]) => ({ category, materials }));
  });

  protected readonly postedRows = computed(() => {
    const count = this.posted();
    if (!count) {
      return [];
    }
    return count.lines
      .map((line) => {
        const m = this.materials().find((x) => x.id === line.rawMaterialId);
        const unit = m ? countUnit(m.unit, m.purchaseUnitSize) : { label: '', size: 1 };
        const difference = line.countedQuantity - (line.bookQuantity ?? 0);
        return {
          id: line.rawMaterialId,
          code: m?.code ?? null,
          name: m?.name ?? 'A removed material',
          unit: unit.label,
          counted: line.countedQuantity / unit.size,
          book: (line.bookQuantity ?? 0) / unit.size,
          difference: difference / unit.size,
          value: line.unitCost === null ? null : difference * line.unitCost,
        };
      })
      .sort((a, b) => Math.abs(b.value ?? 0) - Math.abs(a.value ?? 0));
  });

  protected readonly postedTotal = computed(() => this.postedRows().reduce((sum, r) => sum + (r.value ?? 0), 0));

  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  private saving: Promise<void> | null = null;
  private dirty = false;

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
    // Leaving with entries not yet saved: saved on the way out.
    inject(DestroyRef).onDestroy(() => {
      if (this.dirty) {
        void this.saveNow();
      }
    });
  }

  protected unitOf(material: MaterialCost): string {
    return countUnit(material.unit, material.purchaseUnitSize).label;
  }

  protected isCounted(id: string): boolean {
    return this.valid(this.counted()[id]);
  }

  protected enter(id: string, value: number | null | string): void {
    const quantity = value === '' || value === null ? null : Number(value);
    this.counted.update((counted) => ({ ...counted, [id]: quantity }));
    this.problem.set(null);
    this.dirty = true;
    this.saveState.set('Not saved yet');
    if (this.saveTimer) {
      clearTimeout(this.saveTimer);
    }
    this.saveTimer = setTimeout(() => void this.saveNow(), autosaveAfterMs);
  }

  protected async post(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await this.saveNow();
      if (this.problem()) {
        return;
      }
      const count = await firstValueFrom(this.api.postStockCount(this.id()));
      this.count.set(count);
      this.confirming.set(null);
      this.notify.info('Count posted: stock is set to what was counted.');
    } catch (error) {
      this.problem.set(errorMessage(error, 'The count could not be posted.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async discard(): Promise<void> {
    this.busy.set(true);
    try {
      this.cancelAutosave();
      await firstValueFrom(this.api.discardStockCount(this.id()));
      this.notify.info('Count discarded.');
      void this.router.navigate(['/costing/counts']);
    } catch (error) {
      this.problem.set(errorMessage(error, 'The count could not be discarded.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected print(): void {
    window.print();
  }

  // Saves the sheet as it stands. One save at a time; entries made during it are saved after.
  private async saveNow(): Promise<void> {
    this.cancelAutosave();
    if (this.saving) {
      await this.saving;
      if (!this.dirty) {
        return;
      }
    }
    if (!this.dirty && this.saved()) {
      return;
    }
    this.saving = this.send();
    try {
      await this.saving;
    } finally {
      this.saving = null;
    }
  }

  private async send(): Promise<void> {
    const materials = new Map(this.materials().map((m) => [m.id, m]));
    const lines = Object.entries(this.counted())
      .filter(([id, v]) => this.valid(v) && materials.has(id))
      .map(([id, v]) => ({ rawMaterialId: id, countedQuantity: Math.round(v! * this.sizeOf(materials.get(id)!) * 1000) / 1000 }));
    this.dirty = false;
    this.saveState.set('Saving…');
    try {
      await firstValueFrom(this.api.saveStockCount(this.id(), lines));
      this.saved.set(true);
      this.saveState.set(`Saved ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`);
    } catch (error) {
      this.dirty = true;
      this.saveState.set('Not saved');
      this.problem.set(errorMessage(error, 'The count could not be saved. Your entries are still here; they are saved with the next one.'));
    }
  }

  private cancelAutosave(): void {
    if (this.saveTimer) {
      clearTimeout(this.saveTimer);
      this.saveTimer = null;
    }
  }

  private sizeOf(material: MaterialCost): number {
    return countUnit(material.unit, material.purchaseUnitSize).size;
  }

  private valid(value: number | null | undefined): value is number {
    return typeof value === 'number' && Number.isFinite(value) && value >= 0;
  }

  private async load(id: string): Promise<void> {
    this.error.set(null);
    try {
      const [materials, count] = await Promise.all([firstValueFrom(this.api.materials()), firstValueFrom(this.api.stockCount(id))]);
      this.materials.set(materials);
      this.count.set(count);
      this.saved.set(count !== null);
      const units = new Map(materials.map((m) => [m.id, this.sizeOf(m)]));
      this.counted.set(Object.fromEntries((count?.lines ?? []).map((l) => [l.rawMaterialId, l.countedQuantity / (units.get(l.rawMaterialId) ?? 1)])));
      if (count) {
        this.saveState.set('Saved');
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The count could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
