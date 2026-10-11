import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { CostingApi, Supplier } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';

// One of: a supplier to change, a name typed on an invoice to add, or neither for a new one.
export interface SupplierDialogData {
  supplier?: Supplier;
  name?: string;
  // Opened from an invoice: the supplier goes on it once saved.
  fromInvoice?: boolean;
}

// A supplier's name and how to reach them. Opened on the Suppliers tab to add or change one,
// and over an invoice to add the supplier just typed without leaving it. Closes with the
// supplier as the till saved it; a refusal (a name already taken) keeps it open with all typed.
@Component({
  selector: 'app-supplier-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.supplier) {
        Edit supplier <span class="ar">تعديل مورد</span>
      } @else {
        Add a supplier <span class="ar">إضافة مورد</span>
      }
    </h2>
    <mat-dialog-content>
      @if (data.fromInvoice) {
        <p class="hint">Added now, it goes on this invoice. The rest can be filled in later on the Suppliers tab.</p>
      }
      <form [formGroup]="form" (ngSubmit)="save()" id="supplier-form" class="form">
        <mat-form-field class="wide">
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" maxlength="200" cdkFocusInitial />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Contact person · المسؤول</mat-label>
          <input matInput formControlName="contactPerson" maxlength="200" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Phone · الهاتف</mat-label>
          <input matInput type="tel" formControlName="phone" maxlength="32" />
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>Email · البريد الإلكتروني</mat-label>
          <input matInput type="email" formControlName="email" maxlength="200" />
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>Notes · ملاحظات</mat-label>
          <textarea matInput formControlName="notes" maxlength="500" rows="2" placeholder="Delivers Mondays and Thursdays"></textarea>
        </mat-form-field>
      </form>
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close [disabled]="saving()">Cancel</button>
      <button mat-flat-button type="submit" form="supplier-form" [disabled]="form.invalid || saving()">
        {{ data.fromInvoice ? 'Add and use it' : 'Save' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(180px, 1fr));
      gap: 8px 12px;
    }
    .wide {
      grid-column: 1 / -1;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .hint {
      margin: 0 0 12px;
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
    @media (max-width: 560px) {
      .form {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class SupplierDialog {
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<SupplierDialog, Supplier>);
  protected readonly data = inject<SupplierDialogData>(MAT_DIALOG_DATA);

  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly form = new FormGroup({
    name: new FormControl((this.data.supplier?.name ?? this.data.name ?? '').trim(), {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/)],
    }),
    contactPerson: new FormControl(this.data.supplier?.contactPerson ?? '', { nonNullable: true }),
    phone: new FormControl(this.data.supplier?.phone ?? '', { nonNullable: true }),
    email: new FormControl(this.data.supplier?.email ?? '', { nonNullable: true, validators: [Validators.email] }),
    notes: new FormControl(this.data.supplier?.notes ?? '', { nonNullable: true }),
  });

  // The id is made here for a new one: a retry after a lost answer adds it once.
  private readonly id = this.data.supplier?.id ?? crypto.randomUUID();

  protected async save(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      return;
    }
    const v = this.form.getRawValue();
    const blank = (value: string) => value.trim() || null;
    this.saving.set(true);
    this.problem.set(null);
    try {
      const saved = await firstValueFrom(
        this.api.saveSupplier(this.id, {
          name: v.name.trim(),
          contactPerson: blank(v.contactPerson),
          phone: blank(v.phone),
          email: blank(v.email),
          notes: blank(v.notes),
        }),
      );
      this.ref.close(saved);
    } catch (error) {
      this.problem.set(errorMessage(error, 'The supplier could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
