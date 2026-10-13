import { Component, Injectable, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalAction, ApprovalModule, ApprovalOutcome, ApprovalRequest, ApprovalResult, CostingApi } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';

// What deleting does, module by module: the books are never edited, a posted record is voided.
export const consequences: Record<ApprovalModule, string> = {
  Recipe: 'It is kept, marked deleted, and can be restored; past reports are not changed.',
  PurchaseInvoice:
    'It is kept, marked voided: its goods go back out of stock at its own prices and its cash payment goes back where it came from (or it comes off its supplier’s account), dated today; its spending leaves the reports of its own date.',
  Waste: 'What it took from stock goes back in, dated today. It is kept in the approvals history.',
  StockCount: 'Its adjustments are undone, dated today, and the variance report no longer measures from it.',
  RawMaterial: 'It leaves the lists: it can no longer be bought, counted or put in a recipe. The records that name it keep it.',
  PrepItem: 'It leaves the lists: it can no longer be made, counted or put in a recipe. The batches that name it keep it.',
  ProductionOrder: 'It is kept, marked voided: its ingredients go back into stock and the prep item comes back out, at the costs it was made at, dated today.',
  Treasury: 'It is kept, marked voided: its cash goes back where it came from, dated today, and a supplier payment goes back on the supplier’s account.',
};

// The posted records, which are voided rather than deleted.
export const voids: ReadonlySet<ApprovalModule> = new Set(['PurchaseInvoice', 'Waste', 'StockCount', 'ProductionOrder', 'Treasury']);

// What approving a new or changed prep item or batch does, for the manager deciding it.
export function approvalEffect(module: ApprovalModule, action: ApprovalAction): string {
  if (action === 'Delete') {
    return consequences[module];
  }
  if (module === 'Treasury') {
    return 'The cash moves now: into the safe, or out of it (or out of a day’s takings), as entered.';
  }
  if (module === 'ProductionOrder') {
    return action === 'Create'
      ? 'The batch is made now: its ingredients leave stock and the prep item comes in, at the average costs at this moment.'
      : 'The batch is reversed and made again with the new quantities, at the average costs at this moment.';
  }
  return action === 'Create'
    ? 'The prep item is added, and can then be made and used in recipes.'
    : 'The changes are saved, and the next batches are made by them.';
}

// "Deletion requested", "Change requested": a pending request's mark on its row.
export function pendingLabel(request: ApprovalRequest): { en: string; ar: string } {
  switch (request.action) {
    case 'Create':
      return { en: 'Waiting for approval', ar: 'بانتظار الموافقة' };
    case 'Update':
      return { en: 'Change requested', ar: 'طلب تعديل' };
    default:
      return { en: voids.has(request.module) ? 'Void requested' : 'Deletion requested', ar: voids.has(request.module) ? 'طلب إلغاء' : 'طلب حذف' };
  }
}

export interface DeleteRequestDialogData {
  module: ApprovalModule;
  // What the record is called, as the screen shows it: "Tomatoes", "Invoice F-1001".
  what: string;
  canApprove: boolean;
  // What else it means, for this record: "Sales of Latte stop taking stock".
  detail?: string;
}

// Asks before deleting. Someone who may approve deletions deletes; anyone else asks a manager,
// with a reason if they like. Closes with the reason, or nothing when cancelled.
@Component({
  selector: 'app-delete-request-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.canApprove) {
        {{ isVoid ? 'Void' : 'Delete' }} {{ data.what }}? <span class="ar">{{ isVoid ? 'إلغاء' : 'حذف' }}</span>
      } @else {
        Ask to {{ isVoid ? 'void' : 'delete' }} {{ data.what }}? <span class="ar">{{ isVoid ? 'طلب إلغاء' : 'طلب حذف' }}</span>
      }
    </h2>
    <mat-dialog-content>
      @if (data.detail) {
        <p><strong>{{ data.detail }}</strong></p>
      }
      @if (data.canApprove) {
        <p>{{ consequence }}</p>
      } @else {
        <p class="ask">
          A manager approves deletions. Until they decide it stays as it is, counted in the figures, and cannot be changed.
          <span class="ar">يُرسل الطلب للمدير للموافقة.</span>
        </p>
        <p class="muted">If approved: {{ consequence }}</p>
      }
      <mat-form-field class="reason" subscriptSizing="dynamic">
        <mat-label>Reason · السبب</mat-label>
        <textarea matInput [(ngModel)]="reason" maxlength="500" rows="2" [placeholder]="data.canApprove ? 'Optional' : 'Optional: tells the manager why'"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [class.danger-fill]="data.canApprove" [mat-dialog-close]="{ reason: reason.trim() || null }">
        {{ data.canApprove ? (isVoid ? 'Void' : 'Delete') : 'Ask the manager' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .reason {
      width: 100%;
      margin-top: 4px;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      white-space: nowrap;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .ask {
      padding: 8px 10px;
      border-radius: 8px;
      background: color-mix(in srgb, var(--till-warn) 14%, transparent);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .danger-fill {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
  `,
})
export class DeleteRequestDialog {
  protected readonly data = inject<DeleteRequestDialogData>(MAT_DIALOG_DATA);
  protected readonly isVoid = voids.has(this.data.module);
  protected readonly consequence = consequences[this.data.module];
  protected reason = '';
}

// Maker-checker for the costing screens: what waits for a manager (to mark those rows and lock
// them, and to list the new prep items and batches not made yet), the delete itself, which is a
// request for anyone who may not approve, and the word that a save went to a manager.
@Injectable({ providedIn: 'root' })
export class Approvals {
  private readonly api = inject(CostingApi);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  readonly pending = signal<ApprovalRequest[]>([]);
  readonly canApprove = computed(() => this.auth.can(Permissions.ApprovalDecide));
  private readonly byRecord = computed(() => new Map(this.pending().map((r) => [r.recordId, r])));

  // The request waiting for this record, whatever it asks, if one is: it is locked meanwhile.
  pendingFor(recordId: string): ApprovalRequest | undefined {
    return this.byRecord().get(recordId);
  }

  // The new ones of a module waiting to be made: prep items and batches that do not exist yet.
  waitingToBeMade(module: ApprovalModule): ApprovalRequest[] {
    return this.pending().filter((r) => r.module === module && r.action === 'Create');
  }

  // Says that a save went to a manager rather than being made, and marks it.
  async requested(result: ApprovalResult): Promise<void> {
    this.notify.info(
      result.request.action === 'Create'
        ? 'Submitted to the manager for approval. Nothing is made until it is approved.'
        : 'Change submitted to the manager for approval. It stays as it was until then.',
    );
    await this.refresh();
  }

  async refresh(): Promise<void> {
    if (!this.auth.can(Permissions.InventoryManage)) {
      return;
    }
    try {
      this.pending.set((await firstValueFrom(this.api.approvalRequests())).pending);
    } catch {
      // The screens still work; their rows are simply not marked until the next refresh.
    }
  }

  // Asks first, then deletes (for a manager) or sends the request (for anyone else). Null when
  // cancelled or refused.
  async delete(module: ApprovalModule, recordId: string, what: string, detail?: string): Promise<ApprovalOutcome | null> {
    const answer = await firstValueFrom(
      this.dialog
        .open<DeleteRequestDialog, DeleteRequestDialogData, { reason: string | null }>(DeleteRequestDialog, {
          data: { module, what, canApprove: this.canApprove(), ...(detail ? { detail } : {}) },
          width: '480px',
        })
        .afterClosed(),
    );
    if (!answer) {
      return null;
    }
    try {
      const result = await firstValueFrom(this.api.requestDeletion(crypto.randomUUID(), module, recordId, answer.reason));
      this.notify.info(
        result.outcome === 'Requested'
          ? 'Deletion request submitted to the manager for approval.'
          : `${what.charAt(0).toUpperCase()}${what.slice(1)} ${voids.has(module) ? 'voided' : 'deleted'}.`,
      );
      await this.refresh();
      return result.outcome;
    } catch (error) {
      this.notify.error(error, `${what} could not be deleted.`);
      return null;
    }
  }
}
