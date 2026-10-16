import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { CostingApi, ApprovalRequest, MaterialCost, RawMaterialImportResult, unitLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from './approvals';
import { saveBlob } from './invoice-paper';
import { MaterialDialog, MaterialDialogResult } from './material-dialog';
import { MaterialImportDialog } from './material-import-dialog';

// Raw materials and what they cost: AP$ per purchase unit (the weighted average of what was paid),
// the default yield, and EP$ after it. Stock on hand and its value too. Many at once from Excel:
// the template to fill in, and the filled-in sheet imported, each row a stock item. Shown one stock
// category at a time when the list is long.
@Component({
  selector: 'app-materials-page',
  imports: [
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MatTooltipModule,
    MoneyPipe,
  ],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Find · بحث</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Filter by category · تصفية بالقسم</mat-label>
        <mat-select [ngModel]="category()" (ngModelChange)="category.set($event)" aria-label="Filter by category">
          <mat-option [value]="null">All categories · الكل</mat-option>
          @for (c of categories(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }} ({{ c.count }})</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <span class="spacer"></span>
      <span class="muted">Stock value · قيمة المخزون: <strong>{{ stockValue() | money }}</strong></span>
      <button mat-stroked-button (click)="downloadTemplate()" [disabled]="busy()">
        <mat-icon>download</mat-icon> Excel template<span class="inline-ar">نموذج إكسل</span>
      </button>
      <button mat-stroked-button (click)="picker.click()" [disabled]="busy()">
        <mat-icon>upload_file</mat-icon> Import from Excel<span class="inline-ar">استيراد</span>
      </button>
      <input #picker type="file" hidden accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        (change)="importFile(picker)" aria-label="Excel file to import" />
      <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon> New material</button>
    </div>
    @if (busy()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (materials().length === 0) {
      <p class="note">No raw materials yet. Add each ingredient you buy, with its price, then give the dishes their recipes.</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Code<span class="ar">الكود</span></th>
              <th>Material<span class="ar">الخامة</span></th>
              <th>Category<span class="ar">الفئة</span></th>
              <th>Bought by<span class="ar">وحدة الشراء</span></th>
              <th class="num">AP$ / unit<span class="ar">تكلفة الشراء</span></th>
              <th class="num">Yield %<span class="ar">نسبة الصافي</span></th>
              <th class="num">EP$ / unit<span class="ar">تكلفة الصافي</span></th>
              <th class="num">On hand<span class="ar">الرصيد</span></th>
              <th class="num">Stock value<span class="ar">قيمة المخزون</span></th>
              <th class="num">Last price paid<span class="ar">آخر سعر</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (m of shown(); track m.id) {
              <tr class="clickable" [class.pending-deletion]="!!pendingOf(m)" (click)="edit(m)">
                <td class="code">{{ m.code ?? '' }}</td>
                <td>
                  {{ m.name }}
                  @if (m.kind === 'Prep') {
                    <span class="badge" title="Made in the kitchen: its cost comes from its batches">Prep · نصف مصنع</span>
                  }
                  @if (pendingOf(m); as p) {
                    <div><span class="pending-badge">{{ pendingLabel(p).en }}<span class="ar">{{ pendingLabel(p).ar }}</span></span>
                      <span class="muted small"> by {{ p.requestedBy }}, {{ p.requestedAtUtc | date: 'd MMM' }}</span></div>
                  }
                </td>
                <td>{{ m.category ?? '' }}</td>
                <td>{{ m.purchaseUnit }} <span class="muted">({{ m.purchaseUnitSize | number: '1.0-3' }} {{ unit(m) }})</span></td>
                <td class="num">
                  @if (m.costPerPurchaseUnit !== null) {
                    {{ m.costPerPurchaseUnit | money }}
                  } @else {
                    <span class="error">No price</span>
                  }
                </td>
                <td class="num">{{ m.defaultYieldPercent | number: '1.0-2' }}%</td>
                <td class="num">{{ m.costPerPurchaseUnit === null ? '' : (ep(m) | money) }}</td>
                <td class="num" [class.error]="m.quantityOnHand < 0">
                  {{ m.quantityOnHand / m.purchaseUnitSize | number: '1.0-3' }} {{ m.purchaseUnit }}
                </td>
                <td class="num">{{ m.stockValue === null ? '' : (m.stockValue | money) }}</td>
                <td class="num">{{ m.lastPurchaseCostPerPurchaseUnit === null ? '' : (m.lastPurchaseCostPerPurchaseUnit | money) }}</td>
                <td class="num actions" (click)="$event.stopPropagation()">
                  @if (pendingOf(m)) {
                    <mat-icon class="locked" matTooltip="Waiting for a manager's approval: locked until decided" aria-label="Locked: waiting for a manager">lock_clock</mat-icon>
                  } @else {
                    <button mat-icon-button class="danger" (click)="remove(m)" matTooltip="Delete" [attr.aria-label]="'Delete ' + m.name">
                      <mat-icon>delete</mat-icon>
                    </button>
                  }
                </td>
              </tr>
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
    .actions {
      white-space: nowrap;
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
export class MaterialsPage {
  protected readonly pendingLabel = pendingLabel;
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);

  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected readonly busy = signal(false);

  // The stock categories the materials are in, by name, with how many each holds; the one shown
  // (null: all).
  protected readonly category = signal<string | null>(null);
  protected readonly categories = computed(() => {
    const byId = new Map<string, { id: string; name: string; count: number }>();
    for (const m of this.materials()) {
      const entry = byId.get(m.categoryId) ?? { id: m.categoryId, name: m.category ?? 'General', count: 0 };
      entry.count++;
      byId.set(m.categoryId, entry);
    }
    return [...byId.values()].sort((a, b) => a.name.localeCompare(b.name));
  });

  protected readonly shown = computed(() => {
    const needle = this.search().trim().toLowerCase();
    const category = this.category();
    return this.materials().filter(
      (m) =>
        (category === null || m.categoryId === category) &&
        (!needle || [m.name, m.code, m.category].some((v) => v?.toLowerCase().includes(needle))),
    );
  });

  protected readonly stockValue = computed(() => this.materials().reduce((sum, m) => sum + (m.stockValue ?? 0), 0));

  constructor() {
    void this.load();
  }

  protected unit(m: MaterialCost): string {
    return unitLabels[m.unit];
  }

  // EP$: what a usable purchase unit costs once trimming is lost.
  protected ep(m: MaterialCost): number {
    return ((m.costPerPurchaseUnit ?? 0) * 100) / m.defaultYieldPercent;
  }

  protected pendingOf(material: MaterialCost): ApprovalRequest | undefined {
    return this.approvals.pendingFor(material.id);
  }

  // Archived for a manager; asked of one for anyone else. Only one in no recipe and with nothing
  // on the shelf can go: the till says why otherwise.
  protected async remove(material: MaterialCost): Promise<void> {
    if ((await this.approvals.delete('RawMaterial', material.id, material.name)) === 'Applied') {
      await this.load();
    }
  }

  // Saved, then its unit changed when it was: at once, or asked of a manager. Its other changes
  // stay saved when the unit cannot be.
  protected async edit(material: MaterialCost | null): Promise<void> {
    // Locked while a request about it waits for a manager.
    const pending = material && this.pendingOf(material);
    if (pending) {
      this.notify.info(`${material.name}: ${pendingLabel(pending).en.toLowerCase()}, waiting for a manager. It can be changed once that is decided.`);
      return;
    }
    const saved = await firstValueFrom(
      this.dialog
        .open<MaterialDialog, MaterialCost | null, MaterialDialogResult>(MaterialDialog, { data: material, width: '640px', maxWidth: '95vw' })
        .afterClosed(),
    );
    if (!saved) {
      return;
    }
    const { material: details, unitChange } = saved;
    try {
      await firstValueFrom(this.api.saveMaterial(material?.id ?? null, details));
    } catch (error) {
      this.notify.error(error, 'The material could not be saved.');
      return;
    }
    if (material && unitChange) {
      try {
        const result = await firstValueFrom(this.api.changeMaterialUnit(material.id, unitChange));
        if (result.outcome === 'Requested' && result.request) {
          await this.approvals.requested({ outcome: result.outcome, request: result.request });
        } else {
          this.notify.info(`${details.name} saved, counted in ${{ Gram: 'grams', Millilitre: 'millilitres', Piece: 'pieces' }[unitChange.unit]} now.`);
        }
      } catch (error) {
        this.notify.error(null, `${details.name} was saved, but its unit was not changed: ${errorMessage(error, 'please try again.')}`);
      }
    } else {
      this.notify.info(`${details.name} saved.`);
    }
    await this.load();
  }

  protected async downloadTemplate(): Promise<void> {
    this.busy.set(true);
    try {
      saveBlob(await firstValueFrom(this.api.rawMaterialsTemplate()), 'raw-materials-template.xlsx');
    } catch (error) {
      this.notify.error(error, 'The template could not be downloaded.');
    } finally {
      this.busy.set(false);
    }
  }

  // The sheet chosen: imported, what it did shown, and the list read again. The picker is cleared,
  // so the same file can be chosen again once it is put right.
  protected async importFile(picker: HTMLInputElement): Promise<void> {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) {
      return;
    }
    this.busy.set(true);
    try {
      const result = await firstValueFrom(this.api.importRawMaterials(file));
      this.dialog.open<MaterialImportDialog, RawMaterialImportResult>(MaterialImportDialog, { data: result, width: '640px', maxWidth: '95vw' });
      if (result.created.length) {
        await this.load();
      }
    } catch (error) {
      this.notify.error(error, 'The file could not be imported.');
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    this.error.set(null);
    void this.approvals.refresh();
    try {
      this.materials.set(await firstValueFrom(this.api.materials()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The raw materials could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
