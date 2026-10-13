import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { PrepImportResult, unitLabels } from '../../core/costing/costing.api';

// What an import of batch recipes did: the prep items made (or asked of a manager), each with its
// batch and what to check on it; the raw materials added for their recipes; and each row skipped,
// by its number on the sheet, and why.
@Component({
  selector: 'app-prep-import-dialog',
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Imported from Excel <span class="inline-ar">استيراد المصنعات</span></h2>
    <mat-dialog-content>
      <p class="added">
        @if (made().length) {
          {{ made().length === 1 ? '1 prep item added.' : made().length + ' prep items added.' }}
          <span class="inline-ar">أُضيف {{ made().length }} صنف مصنع.</span>
        }
        @if (asked().length) {
          {{ asked().length === 1 ? '1 prep item' : asked().length + ' prep items' }} sent to a manager for approval: not made until then.
          <span class="inline-ar">أُرسل {{ asked().length }} للمدير للموافقة.</span>
        }
        @if (!data.imported.length) {
          No prep items added. <span class="inline-ar">لم يُضف أي صنف.</span>
        }
      </p>
      @if (data.categoryAdded) {
        <p class="muted">Filed under a new category: {{ data.categoryAdded }}.</p>
      }
      @if (data.materialsAdded.length) {
        <p class="muted">
          Raw materials added for their recipes, with no price yet: {{ materials() }}.
          <span class="inline-ar">خامات أُضيفت بدون سعر.</span>
        </p>
      }

      @if (data.imported.length) {
        <h3>Check each one <span class="inline-ar">للمراجعة</span></h3>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th class="num">Row<span class="ar">الصف</span></th>
                <th>Prep item<span class="ar">الصنف</span></th>
                <th class="num">One batch<span class="ar">الدفعة</span></th>
                <th>To check<span class="ar">للمراجعة</span></th>
              </tr>
            </thead>
            <tbody>
              @for (p of data.imported; track p.row) {
                <tr>
                  <td class="num">{{ p.row }}</td>
                  <td>
                    {{ p.name }}
                    @if (p.requested) {
                      <div class="muted small">waiting for approval</div>
                    }
                  </td>
                  <td class="num nowrap">{{ batch(p.batchQuantity) }}</td>
                  <td>
                    <ul>
                      @for (n of p.notes; track $index) {
                        <li>{{ n }}</li>
                      }
                    </ul>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (data.skipped.length) {
        <h3>{{ data.skipped.length === 1 ? '1 skipped' : data.skipped.length + ' skipped' }} <span class="inline-ar">لم تُستورد</span></h3>
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th class="num">Row<span class="ar">الصف</span></th>
                <th>Prep item<span class="ar">الصنف</span></th>
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
    ul {
      margin: 0;
      padding-left: 18px;
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class PrepImportDialog {
  protected readonly data = inject<PrepImportResult>(MAT_DIALOG_DATA);
  protected readonly made = computed(() => this.data.imported.filter((p) => !p.requested));
  protected readonly asked = computed(() => this.data.imported.filter((p) => p.requested));

  protected materials(): string {
    return this.data.materialsAdded.map((m) => `${m.name} (${unitLabels[m.unit]})`).join(', ');
  }

  // A batch in grams, as a kitchen writes it: 75 g, 15.56 kg.
  protected batch(grams: number): string {
    return grams >= 1000 ? `${+(grams / 1000).toFixed(3)} kg` : `${+grams.toFixed(3)} g`;
  }
}
