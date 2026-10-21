import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { EmployeeImportResult } from '../../core/hr/hr.api';

// What an import from Excel did: who was added, or asked of a manager, and the departments added
// with them; and each row it skipped, by its number on the sheet, and why, for the sheet to be put
// right and imported again (those already added are skipped then, never added twice).
@Component({
  selector: 'app-employee-import-dialog',
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Imported from Excel <span class="inline-ar">الاستيراد من إكسيل</span></h2>
    <mat-dialog-content>
      <p class="added">
        @if (!data.imported.length) {
          No employees added. <span class="inline-ar">لم يُضف أي موظف.</span>
        } @else if (requested) {
          {{ count(data.imported.length) }} asked of a manager: each is added once approved.
          <span class="inline-ar">بانتظار موافقة المدير</span>
        } @else {
          {{ count(data.imported.length) }} added. <span class="inline-ar">تمت الإضافة</span>
        }
      </p>
      @if (data.departmentsAdded.length) {
        <p class="muted">New departments: {{ data.departmentsAdded.join(', ') }}.</p>
      }
      @if (data.skipped.length) {
        <h3>{{ data.skipped.length === 1 ? '1 row skipped' : data.skipped.length + ' rows skipped' }} <span class="inline-ar">صفوف لم تُستورد</span></h3>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th class="num">Row<span class="ar">الصف</span></th>
                <th>Employee<span class="ar">الموظف</span></th>
                <th>Why<span class="ar">السبب</span></th>
              </tr>
            </thead>
            <tbody>
              @for (s of data.skipped; track s.row) {
                <tr>
                  <td class="num">{{ s.row }}</td>
                  <td>{{ s.name ?? '—' }}</td>
                  <td>{{ s.reason }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Done</button>
    </mat-dialog-actions>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .added {
      font: var(--mat-sys-title-small);
    }
    h3 {
      margin: 16px 0 8px;
      font: var(--mat-sys-title-small);
    }
  `,
})
export class EmployeeImportDialog {
  protected readonly data = inject<EmployeeImportResult>(MAT_DIALOG_DATA);
  protected readonly requested = this.data.imported.some((e) => e.requested);

  protected count(n: number): string {
    return n === 1 ? '1 employee' : `${n} employees`;
  }
}
