import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CostingApi, MaterialCost, SpoilageEntry, countUnit } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

interface Line {
  rawMaterialId: string | null;
  quantity: number | null;
  reason: string;
  other: string;
}

// Raw stock thrown away: expired, spoiled, dropped. Recorded, it takes the stock off and the
// variance report sets it against the shortfall, so a loss with a reason is not mistaken for
// one without. Dishes voided after the kitchen made them are recorded by the void itself.
@Component({
  selector: 'app-waste-page',
  imports: [DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule, MoneyPipe],
  template: `
    <p class="note">
      Record raw stock thrown away, with why. A dish voided after the kitchen made it is recorded by the void.
      <span class="inline-ar">سجّل الخامات التالفة وسببها.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Material<span class="ar">الخامة</span></th>
              <th class="num">Quantity<span class="ar">الكمية</span></th>
              <th>Why<span class="ar">السبب</span></th>
              <th class="num">Worth<span class="ar">القيمة</span></th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (line of lines(); track $index; let i = $index) {
              <tr>
                <td>
                  <mat-form-field subscriptSizing="dynamic" class="material">
                    <mat-select [ngModel]="line.rawMaterialId" (ngModelChange)="change(i, { rawMaterialId: $event })" placeholder="Choose…" [aria-label]="'Material, line ' + (i + 1)">
                      @for (m of materials(); track m.id) {
                        <mat-option [value]="m.id">{{ m.code ? m.code + ' · ' : '' }}{{ m.name }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                </td>
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="small">
                    <input matInput type="number" min="0" step="any" [ngModel]="line.quantity" (ngModelChange)="change(i, { quantity: $event })" [attr.aria-label]="'Quantity, line ' + (i + 1)" />
                    <span matTextSuffix>&nbsp;{{ unitOf(line) }}</span>
                  </mat-form-field>
                </td>
                <td>
                  <mat-form-field subscriptSizing="dynamic" class="reason">
                    <mat-select [ngModel]="line.reason" (ngModelChange)="change(i, { reason: $event })" [aria-label]="'Why, line ' + (i + 1)">
                      @for (r of reasons; track r.en) {
                        <mat-option [value]="r.en">{{ r.en }} · {{ r.ar }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                  @if (line.reason === 'Other') {
                    <mat-form-field subscriptSizing="dynamic" class="reason">
                      <input matInput maxlength="200" placeholder="What happened" [ngModel]="line.other" (ngModelChange)="change(i, { other: $event })" [attr.aria-label]="'What happened, line ' + (i + 1)" />
                    </mat-form-field>
                  }
                </td>
                <td class="num">{{ worth(line) === null ? '' : (worth(line) | money) }}</td>
                <td>
                  <button mat-icon-button (click)="remove(i)" [disabled]="lines().length === 1" aria-label="Remove line"><mat-icon>delete</mat-icon></button>
                </td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td><button mat-button (click)="add()"><mat-icon>add</mat-icon> Add line</button></td>
              <td colspan="2"></td>
              <td class="num">{{ total() | money }}</td>
              <td></td>
            </tr>
          </tfoot>
        </table>
      </div>
      <div class="toolbar actions">
        <span class="spacer"></span>
        <button mat-flat-button (click)="save()" [disabled]="!ready() || saving()"><mat-icon>delete_sweep</mat-icon> Record waste</button>
      </div>

      <h2 class="recent">Last 30 days <span class="inline-ar">آخر ٣٠ يومًا</span></h2>
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>When<span class="ar">التاريخ</span></th>
              <th>Material<span class="ar">الخامة</span></th>
              <th class="num">Quantity<span class="ar">الكمية</span></th>
              <th>Why<span class="ar">السبب</span></th>
              <th>By<span class="ar">بواسطة</span></th>
              <th class="num">Worth<span class="ar">القيمة</span></th>
            </tr>
          </thead>
          <tbody>
            @for (entry of recent(); track entry.spoilageId) {
              @for (l of entry.lines; track l.rawMaterialId; let first = $first) {
                <tr>
                  <td>{{ first ? (entry.recordedAtUtc | date: 'd MMM, HH:mm') : '' }}</td>
                  <td>{{ l.material }}</td>
                  <td class="num">{{ l.quantity / unitFor(l).size | number: '1.0-3' }} {{ unitFor(l).label }}</td>
                  <td>{{ l.reason }}</td>
                  <td>{{ first ? entry.recordedBy : '' }}</td>
                  <td class="num">{{ l.value === null ? 'No price' : (l.value | money) }}</td>
                </tr>
              }
            } @empty {
              <tr><td colspan="6" class="muted">Nothing recorded.</td></tr>
            }
          </tbody>
          @if (recent().length) {
            <tfoot>
              <tr>
                <td colspan="5">Total · الإجمالي</td>
                <td class="num">{{ recentTotal() | money }}</td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .material {
      min-width: 200px;
      width: 100%;
    }
    .small {
      width: 140px;
    }
    .reason {
      width: 220px;
    }
    .actions {
      margin-top: 12px;
    }
    .recent {
      margin: 24px 0 8px;
      font: var(--mat-sys-title-medium);
    }
  `,
})
export class WastePage {
  private readonly api = inject(CostingApi);
  private readonly notify = inject(NotifyService);

  protected readonly reasons = [
    { en: 'Expired', ar: 'منتهي الصلاحية' },
    { en: 'Spoiled', ar: 'تالف' },
    { en: 'Dropped or broken', ar: 'سقط أو انكسر' },
    { en: 'Damaged packaging', ar: 'تلف العبوة' },
    { en: 'Other', ar: 'أخرى' },
  ];

  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly recent = signal<SpoilageEntry[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly lines = signal<Line[]>([this.blank()]);

  // One entry, one id: a retry after a lost answer records it once. A new one after each save.
  private spoilageId = crypto.randomUUID();

  protected readonly total = computed(() => this.lines().reduce((sum, l) => sum + (this.worth(l) ?? 0), 0));
  protected readonly recentTotal = computed(() => this.recent().reduce((sum, e) => sum + (e.value ?? 0), 0));

  protected readonly ready = computed(() => {
    const lines = this.lines();
    const ids = lines.map((l) => l.rawMaterialId);
    return (
      lines.every((l) => l.rawMaterialId && (l.quantity ?? 0) > 0 && this.reasonOf(l)) && new Set(ids).size === ids.length
    );
  });

  constructor() {
    void this.load();
  }

  protected unitOf(line: Line): string {
    const material = this.material(line);
    return material ? countUnit(material.unit, material.purchaseUnitSize).label : '';
  }

  // At today's average cost, as the server will value it.
  protected worth(line: Line): number | null {
    const material = this.material(line);
    if (!material || material.costPerUnit === null || !line.quantity) {
      return null;
    }
    return line.quantity * countUnit(material.unit, material.purchaseUnitSize).size * material.costPerUnit;
  }

  protected change(index: number, patch: Partial<Line>): void {
    this.lines.update((lines) => lines.map((line, i) => (i === index ? { ...line, ...patch } : line)));
  }

  protected add(): void {
    this.lines.update((lines) => [...lines, this.blank()]);
  }

  protected remove(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
  }

  protected async save(): Promise<void> {
    if (!this.ready()) {
      return;
    }
    this.saving.set(true);
    try {
      const lines = this.lines().map((l) => ({
        rawMaterialId: l.rawMaterialId!,
        quantity: l.quantity! * countUnit(this.material(l)!.unit, this.material(l)!.purchaseUnitSize).size,
        reason: this.reasonOf(l),
      }));
      await firstValueFrom(this.api.recordSpoilage(this.spoilageId, lines));
      this.notify.info('Waste recorded.');
      this.lines.set([this.blank()]);
      this.spoilageId = crypto.randomUUID();
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The waste could not be recorded.');
    } finally {
      this.saving.set(false);
    }
  }

  protected unitFor(line: { unit: MaterialCost['unit']; purchaseUnitSize: number }): { label: string; size: number } {
    return countUnit(line.unit, line.purchaseUnitSize);
  }

  private reasonOf(line: Line): string {
    return line.reason === 'Other' ? line.other.trim() : line.reason;
  }

  private material(line: Line): MaterialCost | undefined {
    return this.materials().find((m) => m.id === line.rawMaterialId);
  }

  private blank(): Line {
    return { rawMaterialId: null, quantity: null, reason: 'Expired', other: '' };
  }

  private async load(): Promise<void> {
    this.error.set(null);
    try {
      const [materials, recent] = await Promise.all([firstValueFrom(this.api.materials()), firstValueFrom(this.api.spoilage(30))]);
      this.materials.set(materials);
      this.recent.set(recent);
    } catch (error) {
      this.error.set(errorMessage(error, 'The waste could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
