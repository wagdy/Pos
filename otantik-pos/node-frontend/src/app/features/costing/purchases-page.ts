import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { CostingApi, MaterialCost } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

interface Line {
  rawMaterialId: string | null;
  quantity: number | null;
  cost: number | null;
}

// Goods received: what came in, in the units it was bought by, and what the delivery's lines cost
// in all. Each priced line moves the material's average cost; a line without a price adds stock
// at the cost already on file.
@Component({
  selector: 'app-purchases-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule, MoneyPipe],
  template: `
    <p class="note">
      Enter a delivery as it is on the supplier's invoice: how many of each purchase unit came, and what that line cost.
      <span class="inline-ar">أدخل الفاتورة كما هي: الكمية بوحدة الشراء وإجمالي تكلفة السطر.</span>
    </p>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (materials().length === 0) {
      <p class="note">Add the raw materials first, on the Raw materials tab.</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Material<span class="ar">الخامة</span></th>
              <th class="num">Quantity<span class="ar">الكمية</span></th>
              <th class="num">Line total<span class="ar">إجمالي السطر</span></th>
              <th class="num">AP$ / unit<span class="ar">سعر الوحدة</span></th>
              <th class="num">Average now<span class="ar">المتوسط الحالي</span></th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (line of lines(); track $index; let i = $index) {
              <tr>
                <td>
                  <mat-form-field subscriptSizing="dynamic" class="material">
                    <mat-select [(ngModel)]="line.rawMaterialId" (ngModelChange)="touch()" placeholder="Choose…" [aria-label]="'Material, line ' + (i + 1)">
                      @for (m of materials(); track m.id) {
                        <mat-option [value]="m.id">{{ m.code ? m.code + ' · ' : '' }}{{ m.name }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                </td>
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="small">
                    <input matInput type="number" min="0" step="any" [(ngModel)]="line.quantity" (ngModelChange)="touch()" [attr.aria-label]="'Quantity, line ' + (i + 1)" />
                    <span matTextSuffix>&nbsp;{{ material(line)?.purchaseUnit ?? '' }}</span>
                  </mat-form-field>
                </td>
                <td class="num">
                  <mat-form-field subscriptSizing="dynamic" class="small">
                    <span matTextPrefix>L.E&nbsp;</span>
                    <input matInput type="number" min="0" step="any" [(ngModel)]="line.cost" (ngModelChange)="touch()" [attr.aria-label]="'Line total, line ' + (i + 1)" />
                  </mat-form-field>
                </td>
                <td class="num">{{ unitPrice(line) === null ? '' : (unitPrice(line) | money) }}</td>
                <td class="num muted">{{ material(line)?.costPerPurchaseUnit == null ? '' : (material(line)!.costPerPurchaseUnit | money) }}</td>
                <td>
                  <button mat-icon-button (click)="remove(i)" [disabled]="lines().length === 1" aria-label="Remove line">
                    <mat-icon>delete</mat-icon>
                  </button>
                </td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td>
                <button mat-button (click)="add()"><mat-icon>add</mat-icon> Add line</button>
              </td>
              <td></td>
              <td class="num">{{ total() | money }}</td>
              <td colspan="3"></td>
            </tr>
          </tfoot>
        </table>
      </div>
      <div class="toolbar actions">
        <span class="spacer"></span>
        <button mat-button (click)="reset()" [disabled]="saving()">Clear</button>
        <button mat-flat-button (click)="save()" [disabled]="!ready() || saving()">
          <mat-icon>inventory</mat-icon> Receive into stock
        </button>
      </div>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .material {
      min-width: 220px;
      width: 100%;
    }
    .small {
      width: 150px;
    }
    .actions {
      margin-top: 12px;
    }
  `,
})
export class PurchasesPage {
  private readonly api = inject(CostingApi);
  private readonly notify = inject(NotifyService);

  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly lines = signal<Line[]>([this.blank()]);

  // One delivery, one id: a retry after a lost answer books it once. A new one after each save.
  private purchaseId = crypto.randomUUID();

  // The lines are changed in place by the form; this bumps the signal so totals follow.
  private readonly version = signal(0);

  protected readonly total = computed(() => {
    this.version();
    return this.lines().reduce((sum, l) => sum + (l.cost ?? 0), 0);
  });

  // Every line names a material and a quantity; no material twice; costs, where given, not negative.
  protected readonly ready = computed(() => {
    this.version();
    const lines = this.lines();
    const ids = lines.map((l) => l.rawMaterialId);
    return (
      lines.every((l) => l.rawMaterialId && (l.quantity ?? 0) > 0 && (l.cost === null || l.cost >= 0)) &&
      new Set(ids).size === ids.length
    );
  });

  constructor() {
    void this.load();
  }

  protected material(line: Line): MaterialCost | undefined {
    return this.materials().find((m) => m.id === line.rawMaterialId);
  }

  protected unitPrice(line: Line): number | null {
    return line.cost !== null && (line.quantity ?? 0) > 0 ? line.cost / line.quantity! : null;
  }

  protected touch(): void {
    this.version.update((v) => v + 1);
  }

  protected add(): void {
    this.lines.update((lines) => [...lines, this.blank()]);
  }

  protected remove(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
  }

  protected reset(): void {
    this.lines.set([this.blank()]);
    this.purchaseId = crypto.randomUUID();
  }

  protected async save(): Promise<void> {
    if (!this.ready()) {
      return;
    }
    this.saving.set(true);
    try {
      // Stock is kept in the material's own unit (grams, millilitres, pieces).
      const lines = this.lines().map((l) => ({
        rawMaterialId: l.rawMaterialId!,
        quantity: l.quantity! * this.material(l)!.purchaseUnitSize,
        cost: l.cost,
      }));
      await firstValueFrom(this.api.receivePurchase(this.purchaseId, lines));
      this.notify.info(`Received: ${lines.length} ${lines.length === 1 ? 'line' : 'lines'} into stock.`);
      this.reset();
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The delivery could not be received.');
    } finally {
      this.saving.set(false);
    }
  }

  private blank(): Line {
    return { rawMaterialId: null, quantity: null, cost: null };
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
