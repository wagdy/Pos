import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import {
  ApprovalAction,
  ApprovalModule,
  ApprovalPreview,
  ApprovalRequest,
  CostingApi,
  UnitOfMeasure,
  actionLabels,
  moduleLabels,
} from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { ApprovalChanges } from './approval-changes';
import { Approvals, approvalEffect, voids } from './approvals';

type Deciding = { id: string; decision: 'approve' | 'reject' };
type Preview = { id: string; data: ApprovalPreview | null; error: string | null };

// One material's movement on a correction: what the batch took (or made), what the corrected one
// takes (or makes), and what that does to its stock.
interface NetRow {
  material: string;
  unit: UnitOfMeasure;
  was: number;
  now: number;
  change: number;
}

const baseLabels: Record<UnitOfMeasure, string> = { Gram: 'g', Millilitre: 'ml', Piece: 'pc' };

// Pending approvals (طلبات معلقة): every request from staff without approval rights, oldest
// first: deletions in recipes, purchases, waste, stock counts, raw materials, prep items and
// production; new and changed prep items; new and corrected batches. Each shows what it asks,
// line by line, for a manager to approve (it is done) or reject (nothing changes). A batch shows,
// before it is approved, what it would move now at today's averages: what the manager signs off
// on. The last decisions are kept below, as the history of who asked and who decided.
@Component({
  selector: 'app-approvals-page',
  imports: [
    ApprovalChanges,
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MoneyPipe,
  ],
  template: `
    <p class="note">
      Requests from staff without approval rights: deletions, and new or changed prep items and production batches. Until you decide, nothing
      changes: the record stays in every figure and cannot be changed, and nothing new is made or moved.
      <span class="inline-ar">طلبات بانتظار موافقة المدير.</span>
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
              <th>Module<span class="ar">القسم</span></th>
              <th>Request<span class="ar">الطلب</span></th>
              <th>Record<span class="ar">السجل</span></th>
              <th>Requested by<span class="ar">مقدم الطلب</span></th>
              <th>Date<span class="ar">التاريخ</span></th>
              <th class="num"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (r of pending(); track r.id) {
              <tr class="pending">
                <td class="nowrap">{{ labels[r.module].en }}<span class="ar">{{ labels[r.module].ar }}</span></td>
                <td class="nowrap">
                  <span class="action" [class]="r.action">{{ actions[r.action].en }}<span class="ar">{{ actions[r.action].ar }}</span></span>
                </td>
                <td>
                  {{ r.summary }}
                  @if (r.reason) {
                    <div class="muted small">Reason: {{ r.reason }}</div>
                  }
                  @if (r.changes.length) {
                    <app-approval-changes [changes]="r.changes" />
                  }
                </td>
                <td class="nowrap">{{ r.requestedBy }}</td>
                <td class="nowrap">{{ r.requestedAtUtc | date: 'd MMM y, HH:mm' }}</td>
                <td class="num actions">
                  <button mat-flat-button class="approve" (click)="ask(r, 'approve')" [disabled]="saving() || !!deciding()">
                    <mat-icon>check</mat-icon> Approve
                  </button>
                  <button mat-stroked-button (click)="ask(r, 'reject')" [disabled]="saving() || !!deciding()">
                    <mat-icon>close</mat-icon> Reject
                  </button>
                </td>
              </tr>
              @if (deciding()?.id === r.id) {
                <tr class="confirm-row" [class.deleting]="deciding()!.decision === 'approve' && r.action === 'Delete'">
                  <td colspan="6">
                    @if (deciding()!.decision === 'approve' && preview()?.id === r.id) {
                      @let p = preview()!;
                      @if (p.error) {
                        <p class="error">{{ p.error }}</p>
                      } @else if (!p.data) {
                        <mat-progress-bar mode="indeterminate" />
                      } @else if (p.data?.replaces; as was) {
                        <div class="moves">
                          <strong>What approving moves now <span class="inline-ar">حركة المخزون عند الموافقة</span></strong>
                          <p class="muted small">
                            The batch made {{ was.producedAtUtc | date: 'd MMM, HH:mm' }} is reversed (what it took goes back, what it made comes back out),
                            then made again with the new quantities at today's averages. The net change in stock:
                          </p>
                          <table class="grid compact">
                            <thead>
                              <tr>
                                <th>Material<span class="ar">الصنف</span></th>
                                <th class="num">Was<span class="ar">قبل</span></th>
                                <th class="num">Now<span class="ar">بعد</span></th>
                                <th class="num">Stock change<span class="ar">صافي الحركة</span></th>
                              </tr>
                            </thead>
                            <tbody>
                              @for (n of net(p.data!); track n.material) {
                                <tr>
                                  <td>{{ n.material }}</td>
                                  <td class="num">{{ n.was | number: '1.0-3' }} {{ base(n.unit) }}</td>
                                  <td class="num">{{ n.now | number: '1.0-3' }} {{ base(n.unit) }}</td>
                                  <td class="num strong" [class.out]="n.change < 0">{{ n.change > 0 ? '+' : '' }}{{ n.change | number: '1.0-3' }} {{ base(n.unit) }}</td>
                                </tr>
                              }
                            </tbody>
                            <tfoot>
                              <tr>
                                <td colspan="3">Batch cost: {{ was.totalCost | money }} before, at today's averages</td>
                                <td class="num strong">{{ p.data!.batch.totalCost === null ? '—' : (p.data!.batch.totalCost | money) }}</td>
                              </tr>
                            </tfoot>
                          </table>
                          @if (p.data!.batch.missingPrices.length) {
                            <p class="error">{{ p.data!.batch.missingPrices.join(', ') }} {{ p.data!.batch.missingPrices.length === 1 ? 'has' : 'have' }} no cost now: it cannot be made until it has one.</p>
                          }
                        </div>
                      } @else {
                        @let b = p.data!.batch;
                        <div class="moves">
                          <strong>What approving moves now, at today's averages <span class="inline-ar">حركة المخزون عند الموافقة</span></strong>
                          <table class="grid compact">
                            <thead>
                              <tr>
                                <th>Out of stock<span class="ar">يُصرف</span></th>
                                <th class="num">Quantity<span class="ar">الكمية</span></th>
                                <th class="num">Average cost<span class="ar">متوسط التكلفة</span></th>
                                <th class="num">Cost<span class="ar">التكلفة</span></th>
                              </tr>
                            </thead>
                            <tbody>
                              @for (l of b.lines; track l.rawMaterialId) {
                                <tr [class.short]="l.short !== null">
                                  <td>{{ l.material }}</td>
                                  <td class="num">{{ l.quantity | number: '1.0-3' }} {{ base(l.unit) }}</td>
                                  <td class="num muted">{{ l.costPerPurchaseUnit === null ? 'No cost yet' : (l.costPerPurchaseUnit | money) + ' / ' + l.purchaseUnit }}</td>
                                  <td class="num">{{ l.cost === null ? '—' : (l.cost | money) }}</td>
                                </tr>
                              }
                            </tbody>
                            <tfoot>
                              <tr>
                                <td colspan="3">
                                  Into stock: {{ b.producedQuantity / b.displayUnitSize | number: '1.0-3' }} {{ b.displayUnit }} of
                                  {{ b.prepItem }}, at {{ b.unitCostPerDisplayUnit === null ? '—' : (b.unitCostPerDisplayUnit | money) }} / {{ b.displayUnit }}
                                </td>
                                <td class="num strong">{{ b.totalCost === null ? '—' : (b.totalCost | money) }}</td>
                              </tr>
                            </tfoot>
                          </table>
                          @if (b.missingPrices.length) {
                            <p class="error">{{ b.missingPrices.join(', ') }} {{ b.missingPrices.length === 1 ? 'has' : 'have' }} no cost now: it cannot be made until it has one.</p>
                          } @else if (b.shortages.length) {
                            <p class="muted small">The records show too little {{ b.shortages.join(', ') }}: they go below zero until a delivery or a count puts them right.</p>
                          }
                        </div>
                      }
                    }
                    <div class="confirm">
                      @if (deciding()!.decision === 'approve') {
                        <span>{{ question(r) }} {{ effect(r.module, r.action) }}</span>
                      } @else {
                        <span>Reject: {{ r.action === 'Create' ? 'nothing is made.' : 'it stays as it is.' }}</span>
                        <mat-form-field subscriptSizing="dynamic" class="note-field">
                          <mat-label>Note for {{ r.requestedBy }} · ملاحظة</mat-label>
                          <input matInput #noteInput [(ngModel)]="note" maxlength="500" (keydown.enter)="decide(r)" />
                        </mat-form-field>
                      }
                      <span class="spacer"></span>
                      <button mat-button (click)="cancel()" [disabled]="saving()">Cancel</button>
                      <button mat-flat-button [class.danger-fill]="deciding()!.decision === 'approve' && r.action === 'Delete'" (click)="decide(r)"
                        [disabled]="saving() || (deciding()!.decision === 'approve' && blocked(r))">
                        {{ deciding()!.decision === 'approve' ? approveLabel(r) : 'Reject' }}
                      </button>
                    </div>
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="6" class="muted">Nothing waiting for approval.</td></tr>
            }
          </tbody>
        </table>
      </div>

      @if (decided().length) {
        <h3>Recent decisions <span class="inline-ar">القرارات الأخيرة</span></h3>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Module<span class="ar">القسم</span></th>
                <th>Record<span class="ar">السجل</span></th>
                <th>Requested by<span class="ar">مقدم الطلب</span></th>
                <th>Decision<span class="ar">القرار</span></th>
              </tr>
            </thead>
            <tbody>
              @for (r of decided(); track r.id) {
                <tr>
                  <td class="nowrap">{{ labels[r.module].en }}</td>
                  <td>
                    {{ r.summary }}
                    @if (r.reason) {
                      <div class="muted small">Reason: {{ r.reason }}</div>
                    }
                    @if (r.changes.length) {
                      <app-approval-changes [changes]="r.changes" />
                    }
                  </td>
                  <td class="nowrap">{{ r.requestedBy }}<div class="muted small">{{ r.requestedAtUtc | date: 'd MMM y, HH:mm' }}</div></td>
                  <td>
                    <span class="status" [class.approved]="r.status === 'Approved'" [class.rejected]="r.status === 'Rejected'">{{ outcome(r) }}</span>
                    by {{ r.decidedBy }}, {{ r.decidedAtUtc | date: 'd MMM y, HH:mm' }}
                    @if (r.decisionNote) {
                      <div class="muted small">{{ r.decisionNote }}</div>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    }
  `,
  styleUrl: './costing.scss',
  styles: `
    h3 {
      margin: 20px 0 8px;
      font: var(--mat-sys-title-medium);
    }
    tr.pending td {
      background: color-mix(in srgb, var(--till-warn) 8%, var(--mat-sys-surface));
    }
    .actions {
      white-space: nowrap;

      button + button {
        margin-left: 6px;
      }
    }
    .approve {
      --mat-button-filled-container-color: var(--till-ok);
    }
    .nowrap {
      white-space: nowrap;
    }
    .small {
      font-size: 0.85em;
    }
    tr.confirm-row td {
      background: var(--mat-sys-surface-container-high);
      color: var(--mat-sys-on-surface);
    }
    tr.confirm-row.deleting td {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .confirm {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .note-field {
      min-width: 260px;
    }
    .spacer {
      flex: 1;
    }
    .danger-fill:not(:disabled) {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
    .status {
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);

      &.approved {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
      }
      &.rejected {
        background: var(--mat-sys-surface-container-high);
      }
    }
    .action {
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);

      &.Create {
        background: color-mix(in srgb, var(--till-ok) 18%, transparent);
      }
      &.Update {
        background: var(--mat-sys-secondary-container);
        color: var(--mat-sys-on-secondary-container);
      }
      &.Delete {
        background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent);
      }
      .ar {
        display: inline;
        margin-left: 4px;
      }
    }
    .moves {
      margin-bottom: 10px;
      padding: 10px 12px;
      border-radius: 12px;
      background: var(--mat-sys-surface);
      color: var(--mat-sys-on-surface);

      table {
        margin-top: 6px;
      }
      tr.short td {
        background: var(--mat-sys-error-container);
      }
    }
    .strong {
      font-weight: 600;
    }
    .out {
      color: var(--mat-sys-error);
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
export class ApprovalsPage {
  private readonly api = inject(CostingApi);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);

  protected readonly labels = moduleLabels;
  protected readonly actions = actionLabels;
  protected readonly effect = approvalEffect;
  protected readonly pending = signal<ApprovalRequest[]>([]);
  protected readonly decided = signal<ApprovalRequest[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly deciding = signal<Deciding | null>(null);
  // What approving a batch would move, loaded as its approval is asked about.
  protected readonly preview = signal<Preview | null>(null);
  protected note = '';
  private readonly noteInput = viewChild<ElementRef<HTMLInputElement>>('noteInput');

  constructor() {
    void this.load();
    // Rejecting opens the note, ready to type in.
    effect(() => this.noteInput()?.nativeElement.focus());
  }

  protected ask(request: ApprovalRequest, decision: Deciding['decision']): void {
    this.note = '';
    this.deciding.set({ id: request.id, decision });
    this.preview.set(null);
    if (decision === 'approve' && request.module === 'ProductionOrder' && request.action !== 'Delete') {
      void this.loadPreview(request.id);
    }
  }

  protected cancel(): void {
    this.deciding.set(null);
    this.preview.set(null);
  }

  protected base(unit: UnitOfMeasure): string {
    return baseLabels[unit];
  }

  // "Void it?", "Approve the change?"
  protected question(request: ApprovalRequest): string {
    switch (request.action) {
      case 'Delete':
        return voids.has(request.module) ? 'Void it?' : 'Delete it?';
      case 'Create':
        return 'Approve it?';
      default:
        return request.module === 'ProductionOrder' ? 'Approve the correction?' : 'Approve the change?';
    }
  }

  protected approveLabel(request: ApprovalRequest): string {
    const batch = request.module === 'ProductionOrder';
    switch (request.action) {
      case 'Delete':
        return voids.has(request.module) ? 'Approve and void' : 'Approve and delete';
      case 'Create':
        return batch ? 'Approve and make' : 'Approve and add';
      default:
        return batch ? 'Approve the correction' : 'Approve the change';
    }
  }

  // A batch with an ingredient that has no cost now cannot be made: there is nothing to approve yet.
  protected blocked(request: ApprovalRequest): boolean {
    const preview = this.preview();
    return request.module === 'ProductionOrder' && request.action !== 'Delete' && (!preview?.data || preview.data.batch.missingPrices.length > 0);
  }

  // A correction's net movement, material by material: each ingredient back by what the batch
  // took and out by what the corrected one takes; the prep item out by what it made and in by
  // what the corrected one makes.
  protected net(preview: ApprovalPreview): NetRow[] {
    const was = preview.replaces!;
    const rows = new Map<string, NetRow>();
    for (const l of was.lines) {
      rows.set(l.rawMaterialId, { material: l.material, unit: l.unit, was: l.quantity, now: 0, change: 0 });
    }
    for (const l of preview.batch.lines) {
      const row = rows.get(l.rawMaterialId) ?? { material: l.material, unit: l.unit, was: 0, now: 0, change: 0 };
      rows.set(l.rawMaterialId, { ...row, now: l.quantity });
    }
    const ingredients = [...rows.values()].map((r) => ({ ...r, change: r.was - r.now }));
    const made = preview.batch.producedQuantity;
    return [
      ...ingredients,
      { material: `${preview.batch.prepItem} (made)`, unit: preview.batch.unit, was: was.producedQuantity, now: made, change: made - was.producedQuantity },
    ];
  }

  // What was decided, as the history says it.
  protected outcome(request: ApprovalRequest): string {
    return request.status === 'Rejected' ? 'Rejected' : ApprovalsPage.done(request.module, request.action);
  }

  private static done(module: ApprovalModule, action: ApprovalAction): string {
    const batch = module === 'ProductionOrder';
    switch (action) {
      case 'Delete':
        return voids.has(module) ? 'Voided' : 'Deleted';
      case 'Create':
        return batch ? 'Made' : 'Added';
      default:
        return batch ? 'Corrected' : 'Changed';
    }
  }

  protected async decide(request: ApprovalRequest): Promise<void> {
    const decision = this.deciding()?.decision;
    if (!decision || this.saving()) {
      return;
    }
    this.saving.set(true);
    try {
      if (decision === 'approve') {
        await firstValueFrom(this.api.approveRequest(request.id));
        this.notify.info(`Approved: it is ${ApprovalsPage.done(request.module, request.action).toLowerCase()}.`);
      } else {
        await firstValueFrom(this.api.rejectRequest(request.id, this.note.trim() || null));
        this.notify.info(
          request.action === 'Create'
            ? `Rejected: nothing is made.`
            : `Rejected: it stays as it is, and ${request.requestedBy} can change it again.`,
        );
      }
      this.cancel();
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The request could not be decided.');
    } finally {
      this.saving.set(false);
    }
  }

  private async loadPreview(requestId: string): Promise<void> {
    this.preview.set({ id: requestId, data: null, error: null });
    try {
      const data = await firstValueFrom(this.api.approvalPreview(requestId));
      if (this.preview()?.id === requestId) {
        this.preview.set({ id: requestId, data, error: null });
      }
    } catch (error) {
      if (this.preview()?.id === requestId) {
        this.preview.set({ id: requestId, data: null, error: errorMessage(error, 'What it would move could not be worked out.') });
      }
    }
  }

  private async load(): Promise<void> {
    try {
      const requests = await firstValueFrom(this.api.approvalRequests(30));
      this.pending.set(requests.pending);
      this.decided.set(requests.decided);
      // The tab's count and the other screens' marks follow.
      this.approvals.pending.set(requests.pending);
    } catch (error) {
      this.error.set(errorMessage(error, 'The approvals could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
