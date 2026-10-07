import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi, MaterialCost, SaveSharedCost, SharedCost } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MenuService } from '../../core/menu/menu.service';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';

interface Draft {
  id: string | null;
  name: string;
  source: 'material' | 'amount';
  rawMaterialId: string | null;
  monthlyAmount: number | null;
  // '' for all meals: a select shows null as nothing chosen.
  category: string;
}

// The food cost target, and the costs no single recipe carries: frying oil, gas, packaging. Each
// is a month's total, from what was bought of a material or a fixed amount, shared across the
// meals sold that month (all of them, or one category's).
@Component({
  selector: 'app-settings-page',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatRadioModule,
    MatSelectModule,
    MoneyPipe,
  ],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <section class="block">
        <h2>Targets <span class="inline-ar">الأهداف</span></h2>
        <p class="note">
          A dish whose ingredients cost more than the food cost target's share of its price is marked above target. A material whose
          actual usage strays further than the tolerance from standard, either way, is marked in the variance report.
        </p>
        <div class="toolbar">
          <mat-form-field subscriptSizing="dynamic" class="target">
            <mat-label>Food cost target · نسبة التكلفة</mat-label>
            <input matInput type="number" min="1" max="100" step="0.5" [(ngModel)]="target" [disabled]="!canEdit()" />
            <span matTextSuffix>%</span>
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="target">
            <mat-label>Variance tolerance · الحد المسموح</mat-label>
            <span matTextPrefix>±&nbsp;</span>
            <input matInput type="number" min="0.5" max="50" step="0.5" [(ngModel)]="tolerance" [disabled]="!canEdit()" />
            <span matTextSuffix>%</span>
          </mat-form-field>
          @if (canEdit()) {
            <button mat-flat-button (click)="saveTarget()" [disabled]="!targetValid() || (target === savedTarget && tolerance === savedTolerance) || busy()">
              Save
            </button>
          }
        </div>
      </section>

      <section class="block">
        <h2>Drinks <span class="inline-ar">المشروبات</span></h2>
        <p class="note">The menu categories that are drinks: their sales and costs make the beverage cost %, the rest the food cost %.</p>
        <div class="toolbar">
          <mat-form-field subscriptSizing="dynamic" class="drinks">
            <mat-label>Drinks categories · فئات المشروبات</mat-label>
            <mat-select multiple [(ngModel)]="beverages" [disabled]="!canEdit()">
              @for (c of categories(); track c) {
                <mat-option [value]="c">{{ c }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          @if (canEdit()) {
            <button mat-flat-button (click)="saveBeverages()" [disabled]="sameBeverages() || busy()">Save</button>
          }
        </div>
      </section>

      <section class="block">
        <h2>Shared costs <span class="inline-ar">التكاليف المشتركة</span></h2>
        <p class="note">
          Each month's total is shared equally across the meals sold that month, and added to their cost.
          A recipe card uses last month's figure per meal.
        </p>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Name<span class="ar">الاسم</span></th>
                <th>Month's cost from<span class="ar">مصدر التكلفة الشهرية</span></th>
                <th>Shared across<span class="ar">موزعة على</span></th>
                @if (canEdit()) {
                  <th></th>
                }
              </tr>
            </thead>
            <tbody>
              @for (cost of shared(); track cost.id) {
                <tr>
                  <td>{{ cost.name }}</td>
                  <td>
                    @if (cost.rawMaterialName) {
                      What was bought of {{ cost.rawMaterialName }}
                    } @else {
                      {{ cost.monthlyAmount | money }} a month
                    }
                  </td>
                  <td>{{ cost.category ? cost.category + ' meals' : 'All meals (not drinks)' }}</td>
                  @if (canEdit()) {
                    <td class="num">
                      @if (confirming() === cost.id) {
                        <span>Remove?</span>
                        <button mat-button (click)="confirming.set(null)">Keep</button>
                        <button mat-button class="danger" (click)="remove(cost)" [disabled]="busy()">Remove</button>
                      } @else {
                        <button mat-icon-button (click)="edit(cost)" [attr.aria-label]="'Change ' + cost.name"><mat-icon>edit</mat-icon></button>
                        <button mat-icon-button (click)="confirming.set(cost.id)" [attr.aria-label]="'Remove ' + cost.name"><mat-icon>delete</mat-icon></button>
                      }
                    </td>
                  }
                </tr>
              } @empty {
                <tr><td [attr.colspan]="canEdit() ? 4 : 3" class="muted">None. Frying oil is the usual one.</td></tr>
              }
            </tbody>
          </table>
        </div>

        @if (canEdit()) {
          @if (draft(); as d) {
            <div class="editor">
              <h3>{{ d.id ? 'Change ' + d.name : 'New shared cost' }}</h3>
              <mat-form-field subscriptSizing="dynamic">
                <mat-label>Name · الاسم</mat-label>
                <input matInput [(ngModel)]="d.name" placeholder="Frying oil" />
              </mat-form-field>
              <mat-radio-group [(ngModel)]="d.source" class="sources" aria-label="Where the month's cost comes from">
                <mat-radio-button value="material">From what was bought of a material</mat-radio-button>
                <mat-radio-button value="amount">A fixed amount each month</mat-radio-button>
              </mat-radio-group>
              @if (d.source === 'material') {
                <mat-form-field subscriptSizing="dynamic">
                  <mat-label>Material · الخامة</mat-label>
                  <mat-select [(ngModel)]="d.rawMaterialId">
                    @for (m of materials(); track m.id) {
                      <mat-option [value]="m.id">{{ m.name }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
              } @else {
                <mat-form-field subscriptSizing="dynamic">
                  <mat-label>Each month · شهريًا</mat-label>
                  <span matTextPrefix>L.E&nbsp;</span>
                  <input matInput type="number" min="0" step="any" [(ngModel)]="d.monthlyAmount" />
                </mat-form-field>
              }
              <mat-form-field subscriptSizing="dynamic">
                <mat-label>Shared across · موزعة على</mat-label>
                <mat-select [(ngModel)]="d.category">
                  <mat-option value="">All meals, not drinks · كل الوجبات</mat-option>
                  @for (c of categories(); track c) {
                    <mat-option [value]="c">{{ c }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <div class="toolbar">
                <span class="spacer"></span>
                <button mat-button (click)="draft.set(null)">Cancel</button>
                <button mat-flat-button (click)="saveShared(d)" [disabled]="busy()">Save</button>
              </div>
            </div>
          } @else {
            <button mat-button class="add" (click)="edit(null)"><mat-icon>add</mat-icon> Add a shared cost</button>
          }
        }
      </section>
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    .block {
      margin-bottom: 28px;

      h2 {
        margin: 0 0 4px;
        font: var(--mat-sys-title-medium);
      }
    }
    .drinks {
      min-width: 280px;
    }
    .target {
      width: 200px;
    }
    .add {
      margin-top: 8px;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .sources {
      display: flex;
      flex-wrap: wrap;
      gap: 0 16px;
    }
    .editor {
      display: grid;
      gap: 12px;
      max-width: 520px;
      margin-top: 12px;
      padding: 16px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);

      h3 {
        margin: 0;
        font: var(--mat-sys-title-small);
      }
    }
  `,
})
export class SettingsPage {
  private readonly api = inject(CostingApi);
  private readonly auth = inject(AuthService);
  private readonly menu = inject(MenuService);
  private readonly notify = inject(NotifyService);

  protected readonly canEdit = computed(() => this.auth.can(Permissions.InventoryManage));
  protected readonly categories = computed(() => this.menu.categories().map((c) => c.name));

  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly shared = signal<SharedCost[]>([]);
  protected readonly materials = signal<MaterialCost[]>([]);
  protected readonly draft = signal<Draft | null>(null);
  // The shared cost whose removal waits for a second tap.
  protected readonly confirming = signal<string | null>(null);

  protected target = 30;
  protected savedTarget = 30;
  protected tolerance = 5;
  protected savedTolerance = 5;
  protected beverages: string[] = [];
  private savedBeverages: string[] = [];

  constructor() {
    void this.load();
  }

  protected targetValid(): boolean {
    const percent = (value: unknown) => typeof value === 'number' && value > 0 && value < 100;
    return percent(this.target) && percent(this.tolerance);
  }

  protected async saveTarget(): Promise<void> {
    this.busy.set(true);
    try {
      const saved = await firstValueFrom(
        this.api.saveSettings({ foodCostTargetPercent: this.target, varianceTolerancePercent: this.tolerance }),
      );
      this.target = this.savedTarget = saved.foodCostTargetPercent;
      this.tolerance = this.savedTolerance = saved.varianceTolerancePercent ?? this.tolerance;
      this.notify.info('Targets saved.');
    } catch (error) {
      this.notify.error(error, 'The targets could not be saved.');
    } finally {
      this.busy.set(false);
    }
  }

  protected sameBeverages(): boolean {
    return [...this.beverages].sort().join('|') === [...this.savedBeverages].sort().join('|');
  }

  protected async saveBeverages(): Promise<void> {
    this.busy.set(true);
    try {
      const saved = await firstValueFrom(
        this.api.saveSettings({ foodCostTargetPercent: this.savedTarget, beverageCategories: this.beverages }),
      );
      this.beverages = [...(saved.beverageCategories ?? [])];
      this.savedBeverages = [...this.beverages];
      this.notify.info('Drinks categories saved.');
    } catch (error) {
      this.notify.error(error, 'The drinks categories could not be saved.');
    } finally {
      this.busy.set(false);
    }
  }

  protected edit(cost: SharedCost | null): void {
    this.draft.set({
      id: cost?.id ?? null,
      name: cost?.name ?? '',
      source: cost && cost.rawMaterialId === null ? 'amount' : 'material',
      rawMaterialId: cost?.rawMaterialId ?? null,
      monthlyAmount: cost?.monthlyAmount ?? null,
      category: cost?.category ?? '',
    });
  }

  protected async saveShared(draft: Draft): Promise<void> {
    const request: SaveSharedCost = {
      name: draft.name.trim(),
      rawMaterialId: draft.source === 'material' ? draft.rawMaterialId : null,
      monthlyAmount: draft.source === 'amount' ? draft.monthlyAmount : null,
      category: draft.category || null,
    };
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.saveSharedCost(draft.id, request));
      this.notify.info(`${request.name} saved.`);
      this.draft.set(null);
      this.shared.set(await firstValueFrom(this.api.sharedCosts()));
    } catch (error) {
      this.notify.error(error, 'The shared cost could not be saved.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(cost: SharedCost): Promise<void> {
    this.confirming.set(null);
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.deleteSharedCost(cost.id));
      this.notify.info(`${cost.name} removed.`);
      this.shared.set(await firstValueFrom(this.api.sharedCosts()));
    } catch (error) {
      this.notify.error(error, 'The shared cost could not be removed.');
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const [settings, shared, materials] = await Promise.all([
        firstValueFrom(this.api.settings()),
        firstValueFrom(this.api.sharedCosts()),
        firstValueFrom(this.api.materials()),
      ]);
      this.target = this.savedTarget = settings.foodCostTargetPercent;
      this.tolerance = this.savedTolerance = settings.varianceTolerancePercent ?? 5;
      this.beverages = [...(settings.beverageCategories ?? [])];
      this.savedBeverages = [...this.beverages];
      this.shared.set(shared);
      this.materials.set(materials);
    } catch (error) {
      this.error.set(errorMessage(error, 'The costing settings could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
