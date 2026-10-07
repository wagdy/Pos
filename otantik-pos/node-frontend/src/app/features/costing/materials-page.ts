import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { CostingApi, MaterialCost, SaveMaterial, unitLabels } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { MaterialDialog } from './material-dialog';

// Raw materials and what they cost: AP$ per purchase unit (the weighted average of what was paid),
// the default yield, and EP$ after it. Stock on hand and its value too.
@Component({
  selector: 'app-materials-page',
  imports: [DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MoneyPipe],
  template: `
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Find · بحث</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" />
      </mat-form-field>
      <span class="spacer"></span>
      <span class="muted">Stock value · قيمة المخزون: <strong>{{ stockValue() | money }}</strong></span>
      <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon> New material</button>
    </div>
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
            </tr>
          </thead>
          <tbody>
            @for (m of shown(); track m.id) {
              <tr class="clickable" (click)="edit(m)">
                <td class="code">{{ m.code ?? '' }}</td>
                <td>{{ m.name }}</td>
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
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: './costing.scss',
})
export class MaterialsPage {
  private readonly api = inject(CostingApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');

  protected readonly shown = computed(() => {
    const needle = this.search().trim().toLowerCase();
    return this.materials().filter(
      (m) => !needle || [m.name, m.code, m.category].some((v) => v?.toLowerCase().includes(needle)),
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

  protected async edit(material: MaterialCost | null): Promise<void> {
    const saved = await firstValueFrom(
      this.dialog.open<MaterialDialog, MaterialCost | null, SaveMaterial>(MaterialDialog, { data: material, width: '640px' }).afterClosed(),
    );
    if (!saved) {
      return;
    }
    try {
      await firstValueFrom(this.api.saveMaterial(material?.id ?? null, saved));
      this.notify.info(`${saved.name} saved.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The material could not be saved.');
    }
  }

  private async load(): Promise<void> {
    this.error.set(null);
    try {
      this.materials.set(await firstValueFrom(this.api.materials()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The raw materials could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
