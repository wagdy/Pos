import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { RawMaterialImportResult } from '../../core/costing/costing.api';

// What an import from Excel did: how many materials it added and the categories added with them,
// and each row it skipped, by its number on the sheet, and why, for the sheet to be put right.
@Component({
  selector: 'app-material-import-dialog',
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Imported from Excel <span class="inline-ar">الاستيراد من إكسل</span></h2>
    <mat-dialog-content>
      <p class="added">
        @switch (data.created.length) {
          @case (0) {
            No materials added. <span class="inline-ar">لم تُضف أي خامة.</span>
          }
          @case (1) {
            1 material added, as a stock item. <span class="inline-ar">أُضيفت خامة واحدة.</span>
          }
          @default {
            {{ data.created.length }} materials added, as stock items. <span class="inline-ar">أُضيفت {{ data.created.length }} خامة.</span>
          }
        }
      </p>
      @if (data.categoriesAdded.length) {
        <p class="muted">New categories: {{ data.categoriesAdded.join(', ') }}.</p>
      }
      @if (data.skipped.length) {
        <h3>{{ data.skipped.length === 1 ? '1 row skipped' : data.skipped.length + ' rows skipped' }} <span class="inline-ar">صفوف لم تُستورد</span></h3>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th class="num">Row<span class="ar">الصف</span></th>
                <th>Material<span class="ar">الخامة</span></th>
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
  styleUrl: './costing.scss',
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
export class MaterialImportDialog {
  protected readonly data = inject<RawMaterialImportResult>(MAT_DIALOG_DATA);
}
