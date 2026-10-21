import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { Department, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';

const dialogStyles = `
  .wide {
    width: 100%;
  }
  .muted {
    color: var(--mat-sys-on-surface-variant);
  }
  .ar {
    direction: rtl;
    unicode-bidi: isolate;
    margin-left: 6px;
    opacity: 0.75;
    font-size: 0.8em;
  }
`;

// A department's name, new or changed. Closes with it.
@Component({
  selector: 'app-department-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.name ? 'Rename ' + data.name : 'Add a department' }} <span class="ar">قسم</span></h2>
    <mat-dialog-content>
      <mat-form-field class="wide">
        <mat-label>Name · الاسم</mat-label>
        <input matInput [(ngModel)]="name" maxlength="100" placeholder="Kitchen, Service, Management" aria-label="Name" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!name.trim()" [mat-dialog-close]="name.trim()">Save</button>
    </mat-dialog-actions>
  `,
  styles: dialogStyles,
})
export class DepartmentDialog {
  protected readonly data = inject<{ name?: string }>(MAT_DIALOG_DATA);
  protected name = this.data.name ?? '';
}

// A department deleted: where its employees go first, when it has any. Closes with the department
// they move to, or null for an empty one.
@Component({
  selector: 'app-delete-department-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Delete {{ data.department.name }}? <span class="ar">حذف القسم</span></h2>
    <mat-dialog-content>
      @if (data.department.everyone) {
        <p>
          Its {{ data.department.everyone }} {{ data.department.everyone === 1 ? 'employee moves' : 'employees move' }} to another department first@if (data.department.everyone > data.department.employed) {, those who left too}.
        </p>
        <mat-form-field class="wide">
          <mat-label>Move them to · نقلهم إلى</mat-label>
          <mat-select [(ngModel)]="moveTo" aria-label="Move them to">
            @for (d of data.others; track d.id) {
              <mat-option [value]="d.id">{{ d.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      } @else {
        <p class="muted">Nobody is in it.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!!data.department.everyone && !moveTo" [mat-dialog-close]="{ moveTo: moveTo || null }">Delete</button>
    </mat-dialog-actions>
  `,
  styles: dialogStyles,
})
export class DeleteDepartmentDialog {
  protected readonly data = inject<{ department: Department; others: Department[] }>(MAT_DIALOG_DATA);
  protected moveTo = '';
}

// HR's departments (الأقسام): the kitchen, service, management, each employee in one, and how many
// are in each. A manager's to add, rename and delete; deleting one moves its employees to another
// first. General is always there, where new employees start: renamed, never deleted.
@Component({
  selector: 'app-departments-page',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="toolbar">
      <span class="muted">Every employee is in one. The Employees tab shows them by department.</span>
      <span class="spacer"></span>
      @if (canChange()) {
        <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon> Add a department <span class="inline-ar">إضافة قسم</span></button>
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <div class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>Department<span class="ar">القسم</span></th>
              <th class="num">Employed<span class="ar">على رأس العمل</span></th>
              <th class="num">With those who left<span class="ar">مع من ترك العمل</span></th>
              @if (canChange()) {
                <th class="num"><span class="visually-hidden">Change</span></th>
              }
            </tr>
          </thead>
          <tbody>
            @for (d of departments(); track d.id) {
              <tr>
                <td>
                  <strong>{{ d.name }}</strong>
                  @if (d.isGeneral) {
                    <div class="muted small">Where new employees start · always there</div>
                  }
                </td>
                <td class="num">{{ d.employed }}</td>
                <td class="num">{{ d.everyone }}</td>
                @if (canChange()) {
                  <td class="num nowrap">
                    <button mat-icon-button (click)="rename(d)" matTooltip="Rename" [attr.aria-label]="'Rename ' + d.name"><mat-icon>edit</mat-icon></button>
                    @if (!d.isGeneral) {
                      <button mat-icon-button class="danger" (click)="remove(d)" matTooltip="Delete" [attr.aria-label]="'Delete ' + d.name">
                        <mat-icon>delete</mat-icon>
                      </button>
                    }
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .nowrap {
      white-space: nowrap;
    }
    .danger {
      color: var(--mat-sys-error);
    }
  `,
})
export class DepartmentsPage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);

  protected readonly canChange = this.approvals.canApprove;
  protected readonly departments = signal<Department[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected async add(): Promise<void> {
    const name = await this.askName({});
    if (name) {
      await this.act(() => this.api.saveDepartment(crypto.randomUUID(), name), `${name} added.`);
    }
  }

  protected async rename(d: Department): Promise<void> {
    const name = await this.askName({ name: d.name });
    if (name && name !== d.name) {
      await this.act(() => this.api.saveDepartment(d.id, name), `Renamed ${name}.`);
    }
  }

  protected async remove(d: Department): Promise<void> {
    const answer = await firstValueFrom(
      this.dialog
        .open<DeleteDepartmentDialog, { department: Department; others: Department[] }, { moveTo: string | null }>(DeleteDepartmentDialog, {
          data: { department: d, others: this.departments().filter((o) => o.id !== d.id) },
          width: '440px',
        })
        .afterClosed(),
    );
    if (answer) {
      const to = this.departments().find((o) => o.id === answer.moveTo);
      await this.act(() => this.api.deleteDepartment(d.id, answer.moveTo), to ? `${d.name} deleted: its employees are in ${to.name} now.` : `${d.name} deleted.`);
    }
  }

  private askName(data: { name?: string }): Promise<string | undefined> {
    return firstValueFrom(this.dialog.open<DepartmentDialog, { name?: string }, string>(DepartmentDialog, { data, width: '420px' }).afterClosed());
  }

  // Does it, says so, and reads the list again; a refusal is said as the till says it.
  private async act(call: () => Observable<unknown>, done: string): Promise<void> {
    try {
      await firstValueFrom(call());
      this.notify.info(done);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'It could not be saved.');
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.departments.set(await firstValueFrom(this.api.departments()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The departments could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
