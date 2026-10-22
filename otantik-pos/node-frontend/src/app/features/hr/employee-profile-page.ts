import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalResult, isRequested } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { Department, Employee, HrApi, weekDays } from '../../core/hr/hr.api';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { PaperPreview } from '../costing/invoice-paper';
import { EmployeeDialog, EmployeeDialogData } from './employee-dialog';
import { EmploymentDialog, EmploymentDialogData } from './employment-dialog';
import { PayslipPage } from './payslip-page';

// What an ID card can be uploaded as, and how large.
const idPhotoTypes = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf'];
const maxIdPhotoBytes = 5 * 1024 * 1024;

export interface IdCardData {
  name: string;
  fileName: string;
  fetch: () => Observable<Blob>;
}

// The ID card alone, larger, over the page.
@Component({
  selector: 'app-id-card-dialog',
  imports: [MatButtonModule, MatDialogModule, PaperPreview],
  template: `
    <h2 mat-dialog-title>{{ data.name }} <span class="inline-ar">صورة البطاقة</span></h2>
    <mat-dialog-content>
      <app-paper-preview [fetch]="data.fetch" [fileName]="data.fileName" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
  `,
})
export class IdCardDialog {
  protected readonly data = inject<IdCardData>(MAT_DIALOG_DATA);
}

// Before the card's photo is taken off: it is not kept anywhere else.
@Component({
  selector: 'app-remove-id-card-dialog',
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Take off {{ data.name }}’s ID card? <span class="inline-ar">حذف صورة البطاقة</span></h2>
    <mat-dialog-content>
      <p>The photo is deleted from the till, and not kept anywhere else. To put a better one in its place, upload it instead: it replaces this one.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button class="danger-fill" [mat-dialog-close]="true">Take it off</button>
    </mat-dialog-actions>
  `,
  styles: `
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
    .danger-fill {
      background: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
  `,
})
export class RemoveIdCardDialog {
  protected readonly data = inject<{ name: string }>(MAT_DIALOG_DATA);
}

// An employee's profile (ملف الموظف), opened from their row on Employees: who they are (their full
// name, national ID, mobile, and their ID card's photo), their work and pay, and their payslip for
// the month, as the Payroll tab has it. Edited with the Employees tab's own form; deactivated by
// leaving, paid to their last day with nothing erased, and brought back the same way. A manager's
// change at once, anyone else's waiting for a manager, the profile locked until then. The ID card
// is uploaded, replaced or taken off by whoever manages HR, at once.
@Component({
  selector: 'app-employee-profile-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MoneyPipe, PaperPreview, PayslipPage, RouterLink],
  template: `
    <div class="toolbar profile-toolbar no-print">
      <a mat-button routerLink="/hr/employees"><mat-icon>arrow_back</mat-icon> Employees · الموظفين</a>
      <span class="spacer"></span>
      @if (employee(); as e) {
        @if (canManage()) {
          @if (asked()) {
            <span class="locked muted"><mat-icon>lock_clock</mat-icon> Waiting for a manager’s approval</span>
          } @else {
            <button mat-stroked-button (click)="edit(e)"><mat-icon>edit</mat-icon> Edit <span class="inline-ar">تعديل</span></button>
            @if (e.isActive) {
              <button mat-stroked-button class="danger" (click)="leave(e)" matTooltip="They leave: paid to their last day, and nothing is erased">
                <mat-icon>person_off</mat-icon> Deactivate <span class="inline-ar">إنهاء الخدمة</span>
              </button>
            } @else {
              <button mat-stroked-button (click)="comeBack(e)"><mat-icon>person_add_alt</mat-icon> Bring back <span class="inline-ar">إعادة التعيين</span></button>
            }
          }
        }
      }
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as problem) {
      <p class="error">{{ problem }}</p>
    } @else if (employee(); as e) {
      <section class="profile no-print" aria-label="Personal details">
        <header>
          <div class="avatar" aria-hidden="true">{{ initials() }}</div>
          <div class="who">
            <h2>{{ e.name }}</h2>
            <div class="muted">{{ e.jobTitle }} · {{ departmentName() }}</div>
          </div>
          @if (e.isActive) {
            <span class="status active">Employed<span class="ar">على رأس العمل</span></span>
          } @else {
            <span class="status">Left<span class="ar">ترك العمل</span></span>
          }
        </header>
        @if (asked(); as a) {
          <p class="waiting">
            <span class="pending-badge">{{ pendingLabel(a).en }}<span class="ar">{{ pendingLabel(a).ar }}</span></span>
            {{ a.summary }} <span class="muted small">· asked by {{ a.requestedBy }}</span>
          </p>
        }

        <div class="cards">
          <div class="card">
            <h3>Personal details <span class="inline-ar">البيانات الشخصية</span></h3>
            <dl>
              <div><dt>Full name<span class="ar">الاسم بالكامل</span></dt><dd>{{ e.name }}</dd></div>
              <div>
                <dt>National ID<span class="ar">رقم البطاقة</span></dt>
                <dd [class.missing]="!e.nationalId" class="digits">{{ e.nationalId ?? 'Not entered' }}</dd>
              </div>
              <div>
                <dt>Mobile<span class="ar">رقم الموبايل</span></dt>
                <dd [class.missing]="!e.mobileNumber">
                  @if (e.mobileNumber; as m) {
                    <a class="digits" [href]="'tel:' + m">{{ m }}</a>
                  } @else {
                    Not entered
                  }
                </dd>
              </div>
              <div>
                <dt>Started on<span class="ar">تاريخ التعيين</span></dt>
                <dd>
                  {{ e.hiredOn | date: 'd MMM y' }}
                  @if (e.leftOn) {
                    <span class="muted"> · last day {{ e.leftOn | date: 'd MMM y' }}</span>
                  }
                </dd>
              </div>
            </dl>
            <h3>Work and pay <span class="inline-ar">العمل والأجر</span></h3>
            <dl>
              <div><dt>Department · role<span class="ar">القسم والوظيفة</span></dt><dd>{{ departmentName() }} · {{ e.jobTitle }}</dd></div>
              <div>
                <dt>Pay<span class="ar">الأجر</span></dt>
                <dd>
                  @if (e.employmentType === 'Daily') {
                    {{ e.dailyWage | money }} a day <span class="muted">· daily wage</span>
                  } @else {
                    {{ e.monthlySalary | money }} a month <span class="muted">· {{ e.dailyRate | money }} a day</span>
                  }
                </dd>
              </div>
              <div>
                <dt>Shift<span class="ar">الوردية</span></dt>
                <dd>
                  @if (e.shiftStartsAt && e.shiftEndsAt) {
                    {{ e.shiftStartsAt.slice(0, 5) }}–{{ e.shiftEndsAt.slice(0, 5) }}
                  } @else {
                    Flexible hours
                  }
                  <span class="muted"> · off {{ daysOff() }}</span>
                </dd>
              </div>
              <div><dt>Device no.<span class="ar">رقم البصمة</span></dt><dd [class.missing]="!e.deviceUserId">{{ e.deviceUserId ?? 'Not on the device' }}</dd></div>
            </dl>
          </div>

          <div class="card id-card">
            <h3>ID card <span class="inline-ar">صورة البطاقة</span></h3>
            @if (e.idPhoto; as photo) {
              <app-paper-preview [fetch]="photoFetch()" [fileName]="photo.fileName" />
              <p class="muted small">Uploaded by {{ photo.uploadedBy }}, {{ photo.uploadedAtUtc | date: 'd MMM y, HH:mm' }}</p>
            } @else {
              <div class="empty">
                <mat-icon>badge</mat-icon>
                <span>No photo of their ID card yet.</span>
              </div>
            }
            @if (uploading()) {
              <mat-progress-bar mode="indeterminate" />
            }
            <div class="card-actions">
              @if (e.idPhoto; as photo) {
                <button mat-button (click)="enlarge(e, photo.fileName)"><mat-icon>zoom_in</mat-icon> View larger</button>
              }
              @if (canManage()) {
                <button mat-stroked-button (click)="picker.click()" [disabled]="uploading()">
                  <mat-icon>upload</mat-icon> {{ e.idPhoto ? 'Replace' : 'Upload' }} <span class="inline-ar">{{ e.idPhoto ? 'استبدال' : 'رفع' }}</span>
                </button>
                @if (e.idPhoto) {
                  <button mat-button class="danger" (click)="removePhoto(e)" [disabled]="uploading()"><mat-icon>delete</mat-icon> Take off</button>
                }
              }
            </div>
            <input #picker type="file" hidden accept="image/jpeg,image/png,image/webp,application/pdf" (change)="upload(e, picker)" aria-label="ID card photo to upload" />
            @if (canManage()) {
              <p class="muted small">A photo (JPEG, PNG or WebP) or a PDF, under 5 MB.</p>
            }
          </div>
        </div>
      </section>

      <section class="payslip-part" aria-label="Payslip">
        <div class="pay-head no-print">
          <h3>Payslip <span class="inline-ar">قسيمة الراتب</span></h3>
          <span class="spacer"></span>
          <button mat-icon-button (click)="moveMonth(-1)" aria-label="The month before"><mat-icon>chevron_left</mat-icon></button>
          <span class="month">{{ month() | date: 'MMMM y' }}</span>
          <button mat-icon-button (click)="moveMonth(1)" [disabled]="month() >= thisMonth()" aria-label="The month after"><mat-icon>chevron_right</mat-icon></button>
        </div>
        <app-payslip-page [employeeId]="e.id" [month]="month()" [embedded]="true" />
      </section>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .profile header {
      display: flex;
      align-items: center;
      gap: 14px;
      max-width: 1100px;
      margin: 4px 0 12px;

      h2 {
        margin: 0;
        font: var(--mat-sys-headline-small);
      }
      .who {
        flex: 1;
        min-width: 0;
      }
    }
    .avatar {
      display: grid;
      place-items: center;
      width: 52px;
      height: 52px;
      flex: none;
      border-radius: 50%;
      font: var(--mat-sys-title-medium);
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-surface-container-high);

      &.active {
        background: color-mix(in srgb, var(--till-ok, #2e7d32) 16%, transparent);
      }
    }
    .waiting {
      max-width: 1100px;
      margin: 0 0 12px;
    }
    .cards {
      display: grid;
      grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
      gap: 16px;
      max-width: 1100px;
    }
    .card {
      padding: 12px 16px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      h3 {
        margin: 4px 0 8px;
        font: var(--mat-sys-title-small);
        color: var(--mat-sys-on-surface-variant);
      }
    }
    dl {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 10px 16px;
      margin: 0 0 12px;

      dt {
        font: var(--mat-sys-label-small);
        color: var(--mat-sys-on-surface-variant);
      }
      dd {
        margin: 2px 0 0;
        font: var(--mat-sys-body-large);
        overflow-wrap: anywhere;
      }
      dd.missing {
        color: var(--mat-sys-on-surface-variant);
        font-style: italic;
      }
    }
    .digits {
      font-variant-numeric: tabular-nums;
      letter-spacing: 0.02em;
    }
    .id-card {
      --paper-height: 260px;
      --paper-min-width: 0;
    }
    .empty {
      display: grid;
      place-items: center;
      gap: 6px;
      min-height: 160px;
      border-radius: 8px;
      border: 1px dashed var(--mat-sys-outline-variant);
      color: var(--mat-sys-on-surface-variant);

      mat-icon {
        width: 40px;
        height: 40px;
        font-size: 40px;
      }
    }
    .card-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 8px;
    }
    .payslip-part {
      margin-top: 20px;

      app-payslip-page {
        display: block;
        max-width: 640px;
      }
    }
    .pay-head {
      display: flex;
      align-items: center;
      gap: 4px;
      max-width: 640px;

      h3 {
        margin: 0;
        font: var(--mat-sys-title-medium);
      }
      .month {
        min-width: 120px;
        text-align: center;
        font: var(--mat-sys-title-small);
      }
    }
    .locked {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .danger {
      color: var(--mat-sys-error);
    }
    .small {
      font-size: 0.85em;
    }
    @media (max-width: 760px) {
      .cards {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class EmployeeProfilePage {
  private readonly api = inject(HrApi);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  readonly employeeId = input.required<string>();

  protected readonly employee = signal<Employee | null>(null);
  protected readonly departments = signal<Department[]>([]);
  protected readonly loading = signal(true);
  protected readonly uploading = signal(false);
  protected readonly error = signal<string | null>(null);
  // The payslip's month (its first day); the business day's to start with, and none after it.
  protected readonly thisMonth = signal('');
  protected readonly month = signal('');
  protected readonly canManage = computed(() => this.auth.can(Permissions.HrManage));
  protected readonly asked = computed(() => {
    const e = this.employee();
    return e ? this.approvals.pendingFor(e.id) : undefined;
  });

  protected readonly departmentName = computed(() => {
    const e = this.employee();
    return this.departments().find((d) => d.id === e?.departmentId)?.name ?? '—';
  });

  // "HM" for Hassan Mahmoud; the first letter for one name.
  protected readonly initials = computed(() => {
    const words = (this.employee()?.name ?? '').split(/\s+/).filter(Boolean);
    return words.length > 1 ? words[0][0] + words[1][0] : (words[0]?.[0] ?? '?');
  });

  // "Fri, Sat", in the restaurant's week order; "no day" when none.
  protected readonly daysOff = computed(() => {
    const off = weekDays.filter((d) => this.employee()?.daysOff.includes(d.day)).map((d) => d.en);
    return off.length ? off.join(', ') : 'no day';
  });

  // The photo fetched again when it is replaced, and not when anything else about them changes.
  private readonly photoKey = computed(() => {
    const e = this.employee();
    return e?.idPhoto ? `${e.id} ${e.idPhoto.uploadedAtUtc}` : '';
  });
  protected readonly photoFetch = computed(() => {
    const [id] = this.photoKey().split(' ');
    return () => this.api.idPhoto(id);
  });

  constructor() {
    effect(() => {
      const id = this.employeeId();
      untracked(() => void this.load(id));
    });
    void this.approvals.refresh();
  }

  protected moveMonth(by: number): void {
    const [year, month] = this.month().split('-').map(Number);
    const moved = new Date(year, month - 1 + by, 1);
    const iso = `${moved.getFullYear()}-${String(moved.getMonth() + 1).padStart(2, '0')}-01`;
    if (iso <= this.thisMonth()) {
      this.month.set(iso);
    }
  }

  protected async edit(employee: Employee): Promise<void> {
    const data: EmployeeDialogData = { employee, departments: this.departments(), canApprove: this.approvals.canApprove(), today: await this.businessDay.today() };
    const result = await firstValueFrom(
      this.dialog.open<EmployeeDialog, EmployeeDialogData, Employee | ApprovalResult>(EmployeeDialog, { data, width: '620px', maxWidth: '95vw' }).afterClosed(),
    );
    await this.saved(result, `${result && !isRequested(result) ? result.name : 'The employee'} saved.`);
  }

  protected async leave(employee: Employee): Promise<void> {
    await this.employment({ employee, mode: 'leave', canApprove: this.approvals.canApprove(), today: await this.businessDay.today() });
  }

  protected async comeBack(employee: Employee): Promise<void> {
    await this.employment({ employee, mode: 'return', canApprove: this.approvals.canApprove(), today: await this.businessDay.today() });
  }

  protected enlarge(employee: Employee, fileName: string): void {
    this.dialog.open<IdCardDialog, IdCardData>(IdCardDialog, {
      data: { name: employee.name, fileName, fetch: () => this.api.idPhoto(employee.id) },
      maxWidth: '95vw',
    });
  }

  // Checked here first, so a file the till would refuse is said at once; the picker cleared, so
  // the same file can be chosen again.
  protected async upload(employee: Employee, picker: HTMLInputElement): Promise<void> {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) {
      return;
    }
    if (!idPhotoTypes.includes(file.type)) {
      this.notify.error(null, 'Upload a photo (JPEG, PNG or WebP) or a PDF of the ID card.');
      return;
    }
    if (file.size > maxIdPhotoBytes) {
      this.notify.error(null, 'The file must be under 5 MB: a photo from a phone is, or take it again at a smaller size.');
      return;
    }
    this.uploading.set(true);
    try {
      this.employee.set(await firstValueFrom(this.api.uploadIdPhoto(employee.id, file)));
      this.notify.info(`${employee.name}’s ID card saved.`);
    } catch (error) {
      this.notify.error(error, 'The ID card could not be uploaded.');
    } finally {
      this.uploading.set(false);
    }
  }

  protected async removePhoto(employee: Employee): Promise<void> {
    const sure = await firstValueFrom(
      this.dialog.open<RemoveIdCardDialog, { name: string }, boolean>(RemoveIdCardDialog, { data: { name: employee.name }, width: '500px', maxWidth: '95vw' }).afterClosed(),
    );
    if (!sure) {
      return;
    }
    this.uploading.set(true);
    try {
      this.employee.set(await firstValueFrom(this.api.removeIdPhoto(employee.id)));
      this.notify.info(`${employee.name}’s ID card taken off.`);
    } catch (error) {
      this.notify.error(error, 'The ID card could not be taken off.');
    } finally {
      this.uploading.set(false);
    }
  }

  private async employment(data: EmploymentDialogData): Promise<void> {
    const result = await firstValueFrom(
      this.dialog.open<EmploymentDialog, EmploymentDialogData, Employee | ApprovalResult>(EmploymentDialog, { data, width: '440px' }).afterClosed(),
    );
    await this.saved(result, data.mode === 'leave' ? `${data.employee.name} has left.` : `${data.employee.name} is back.`);
  }

  private async saved(result: Employee | ApprovalResult | undefined, done: string): Promise<void> {
    if (!result) {
      return;
    }
    if (isRequested(result)) {
      await this.approvals.requested(result);
    } else {
      this.notify.info(done);
      this.employee.set(result);
    }
  }

  private async load(id: string): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [employee, departments, today] = await Promise.all([
        firstValueFrom(this.api.employee(id)),
        firstValueFrom(this.api.departments()),
        this.businessDay.today(),
      ]);
      this.employee.set(employee);
      this.departments.set(departments);
      this.thisMonth.set(`${today.slice(0, 8)}01`);
      if (!this.month()) {
        this.month.set(this.thisMonth());
      }
    } catch (error) {
      this.employee.set(null);
      this.error.set(errorMessage(error, 'The employee could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
