import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { ApprovalResult, isRequested } from '../../core/costing/costing.api';
import { CustodyApi, CustodyItem } from '../../core/custody/custody.api';
import { errorMessage } from '../../core/http/error-message';
import { Department, Employee, HrApi, weekDays } from '../../core/hr/hr.api';
import { HrAccess } from '../../core/hr/hr-access';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals, pendingLabel } from '../costing/approvals';
import { BusinessDay } from '../costing/financials';
import { PaperPreview } from '../costing/invoice-paper';
import { EmployeeDialog, EmployeeDialogData, idDigits } from './employee-dialog';
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

// A manager's: their portal PIN, typed twice. 4 to 6 digits, Arabic ones as well; never shown
// again, nor kept as typed.
@Component({
  selector: 'app-pin-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.hasPin ? 'Change' : 'Set' }} {{ data.name }}’s portal PIN <span class="inline-ar">الرقم السري للبوابة</span></h2>
    <mat-dialog-content>
      <p class="muted">They sign in to the portal with their mobile number, {{ data.mobile }}, and this PIN. Tell them it yourself: it is not shown again.</p>
      <mat-form-field class="wide">
        <mat-label>PIN · الرقم السري</mat-label>
        <input matInput type="password" inputmode="numeric" autocomplete="new-password" maxlength="6" [(ngModel)]="pin" aria-label="PIN" />
        <mat-hint>4 to 6 digits</mat-hint>
      </mat-form-field>
      <mat-form-field class="wide">
        <mat-label>Again · تأكيد</mat-label>
        <input matInput type="password" inputmode="numeric" autocomplete="new-password" maxlength="6" [(ngModel)]="again" aria-label="PIN again" />
        @if (again && digits(again) !== digits(pin)) {
          <mat-hint class="bad">Not the same PIN</mat-hint>
        }
      </mat-form-field>
      @if (data.hasPin) {
        <p class="muted small">A new PIN signs them out of the portal wherever they are signed in.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!ready()" [mat-dialog-close]="digits(pin)">Save the PIN</button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide {
      width: 100%;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .bad {
      color: var(--mat-sys-error);
    }
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
  `,
})
export class PinDialog {
  protected readonly data = inject<{ name: string; mobile: string; hasPin: boolean }>(MAT_DIALOG_DATA);
  protected pin = '';
  protected again = '';
  protected readonly digits = (text: string) => idDigits(text);

  protected ready(): boolean {
    const pin = this.digits(this.pin);
    return /^\d{4,6}$/.test(pin) && pin === this.digits(this.again);
  }
}

// An employee's profile (ملف الموظف), opened from their row on Employees: who they are (their full
// name, national ID, mobile, and their ID card's photo) and their work and pay, beside their payslip
// for the month as the Payroll tab has it (one under the other on a narrow screen). Edited with the Employees tab's own form; deactivated by
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
      <header class="profile-head no-print">
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
        <p class="waiting no-print">
          <span class="pending-badge">{{ pendingLabel(a).en }}<span class="ar">{{ pendingLabel(a).ar }}</span></span>
          {{ a.summary }} <span class="muted small">· asked by {{ a.requestedBy }}</span>
        </p>
      }

      <div class="layout">
        <section class="profile no-print" aria-label="Personal details">
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

            <div class="card custody">
              <h3>Assigned custody <span class="inline-ar">العهدة</span></h3>
              @for (c of custody(); track c.asset.id) {
                <div class="held">
                  <span>
                    <strong>{{ c.asset.name }}</strong> × {{ c.asset.quantity }}
                    <div class="muted small">{{ c.asset.categoryName ?? 'No category' }}@if (c.since) { · since {{ c.since | date: 'd MMM y' }} }</div>
                  </span>
                  <span class="amount">{{ c.asset.totalCost === null ? '—' : (c.asset.totalCost | money) }}</span>
                </div>
              } @empty {
                <p class="muted">Nothing in their custody.</p>
              }
              @if (!auth.employeeActing()) {
                <a mat-button routerLink="/custody/assets"><mat-icon>inventory_2</mat-icon> Custody &amp; Assets</a>
              }
            </div>

            <div class="card pin">
              <h3>Portal <span class="inline-ar">بوابة الموظف</span></h3>
              @if (e.hasPin) {
                <p>A PIN is set{{ e.pinSetAtUtc ? ', on ' + (e.pinSetAtUtc | date: 'd MMM y') : '' }}: they sign in with their mobile number and it.</p>
              } @else if (!e.mobileNumber) {
                <p class="muted">Enter their mobile number first: they sign in to the portal with it.</p>
              } @else {
                <p class="muted">No PIN yet: they cannot sign in to the portal.</p>
              }
              @if (portalAddress(); as address) {
                <p class="muted small">On the restaurant's Wi-Fi: <span class="digits">{{ address }}</span></p>
              }
              @if (canSetPin() && e.isActive && e.mobileNumber) {
                <div class="card-actions">
                  <button mat-stroked-button (click)="setPin(e)"><mat-icon>password</mat-icon> {{ e.hasPin ? 'Change PIN' : 'Set a PIN' }} <span class="inline-ar">الرقم السري</span></button>
                  @if (e.hasPin) {
                    <button mat-button class="danger" (click)="removePin(e)"><mat-icon>no_encryption</mat-icon> Take it off</button>
                  }
                </div>
              }
            </div>
          </div>
        </section>

        @if (access.has('Payroll')) {
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
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .profile-head {
      display: flex;
      align-items: center;
      gap: 14px;
      max-width: 1320px;
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
      max-width: 1320px;
      margin: 0 0 12px;
    }
    /* Two columns on a desktop: who they are on the left, the payslip on the right. */
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 640px);
      gap: 20px;
      align-items: start;
      max-width: 1320px;
    }
    .cards {
      display: grid;
      gap: 16px;
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
    .held {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      padding: 6px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);

      .amount {
        margin-left: auto;
        white-space: nowrap;
        font-variant-numeric: tabular-nums;
      }
    }
    .card-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 8px;
    }
    .payslip-part app-payslip-page {
      display: block;
    }
    .pay-head {
      display: flex;
      align-items: center;
      gap: 4px;

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
    /* One under the other where two would be cramped. */
    @media (max-width: 1100px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class EmployeeProfilePage {
  private readonly api = inject(HrApi);
  protected readonly access = inject(HrAccess);
  protected readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly businessDay = inject(BusinessDay);
  protected readonly approvals = inject(Approvals);
  protected readonly pendingLabel = pendingLabel;

  private readonly custodyApi = inject(CustodyApi);

  readonly employeeId = input.required<string>();
  protected readonly custody = signal<CustodyItem[]>([]);
  protected readonly portalAddress = signal<string | null>(null);
  // A PIN is a manager's to set.
  protected readonly canSetPin = this.approvals.canApprove;

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

  protected async setPin(employee: Employee): Promise<void> {
    const pin = await firstValueFrom(
      this.dialog
        .open<PinDialog, { name: string; mobile: string; hasPin: boolean }, string>(PinDialog, {
          data: { name: employee.name, mobile: employee.mobileNumber ?? '', hasPin: !!employee.hasPin },
          width: '440px',
        })
        .afterClosed(),
    );
    if (!pin) {
      return;
    }
    try {
      this.employee.set(await firstValueFrom(this.api.setPin(employee.id, pin)));
      this.notify.info(`${employee.name}’s portal PIN saved.`);
    } catch (error) {
      this.notify.error(error, 'The PIN could not be saved.');
    }
  }

  protected async removePin(employee: Employee): Promise<void> {
    try {
      this.employee.set(await firstValueFrom(this.api.removePin(employee.id)));
      this.notify.info(`${employee.name} can no longer sign in to the portal.`);
    } catch (error) {
      this.notify.error(error, 'The PIN could not be taken off.');
    }
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
    // Only a help: the profile stands without them.
    try {
      this.custody.set(await firstValueFrom(this.custodyApi.employeeCustody(id)));
    } catch {
      this.custody.set([]);
    }
    try {
      const where = await firstValueFrom(this.api.deviceConnection());
      this.portalAddress.set(where.portalPort && where.addresses.length ? `http://${where.addresses[0]}:${where.portalPort}/portal` : null);
    } catch {
      this.portalAddress.set(null);
    }
  }
}
