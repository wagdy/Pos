import { Component, inject } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { TillStaff } from '../../core/staff/staff.api';

// A new PIN, typed twice: nobody sees it, so a typo would lock the person out of their own till.
// 4 to 8 digits, the server's rule.
@Component({
  selector: 'app-pin-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.hasPin ? 'New PIN' : 'Set a PIN' }} for {{ data.fullName }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" (ngSubmit)="save()" id="pin-form" class="pin-form">
        <mat-form-field>
          <mat-label>PIN</mat-label>
          <input matInput type="password" formControlName="pin" inputmode="numeric" autocomplete="new-password" cdkFocusInitial />
          <mat-hint>4 to 8 digits</mat-hint>
          @if (form.controls.pin.hasError('pattern') && form.controls.pin.touched) {
            <mat-error>Use 4 to 8 digits.</mat-error>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label>The same PIN again</mat-label>
          <input matInput type="password" formControlName="repeat" inputmode="numeric" autocomplete="new-password" />
          @if (form.hasError('mismatch') && form.controls.repeat.touched) {
            <mat-error>The two PINs are not the same.</mat-error>
          }
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="pin-form" [disabled]="form.invalid">Save PIN</button>
    </mat-dialog-actions>
  `,
  styles: `
    .pin-form {
      display: grid;
      gap: 8px;
      min-width: 260px;
    }
  `,
})
export class PinDialog {
  protected readonly data = inject<TillStaff>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<PinDialog, string>);

  protected readonly form = new FormGroup(
    {
      pin: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^\d{4,8}$/)] }),
      repeat: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    },
    { validators: (group: AbstractControl): ValidationErrors | null => (group.value.pin === group.value.repeat ? null : { mismatch: true }) },
  );

  protected save(): void {
    if (this.form.valid) {
      this.ref.close(this.form.controls.pin.value);
    }
  }
}
