import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { EmployeeHrAccess, HrApi, StaffHrAccess } from '../../core/hr/hr.api';
import { HrTab, allHrTabs, hrTabs, hrTabsOf } from '../../core/hr/hr-tabs';
import { NotifyService } from '../../core/ui/notify.service';

// Whose tabs, as the dialog shows them. An employee's say whether they can sign in to the portal.
export interface TabsDialogData {
  name: string;
  tabs: readonly HrTab[];
  employee?: { hasMobile: boolean; hasPin: boolean };
}

// The HR & Payroll tabs given to one person (صلاحيات), ticked one by one or all at once. Closes
// with the tabs ticked.
@Component({
  selector: 'app-tabs-dialog',
  imports: [MatButtonModule, MatCheckboxModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>{{ data.name }}’s HR tabs <span class="ar">الصلاحيات</span></h2>
    <mat-dialog-content>
      <mat-checkbox class="all" [checked]="every()" [indeterminate]="some()" (change)="all($event.checked)">Every tab <span class="ar">الكل</span></mat-checkbox>
      <div class="tabs">
        @for (t of tabs; track t.tab) {
          <mat-checkbox [checked]="chosen().has(t.tab)" (change)="tick(t.tab, $event.checked)" [attr.data-tab]="t.tab">
            {{ t.en }} <span class="ar">{{ t.ar }}</span>
          </mat-checkbox>
        }
      </div>
      <p class="muted small">
        They see only these tabs. What they change there waits for a manager; correcting attendance, finalizing pay and the devices stay a manager’s.
      </p>
      @if (data.employee; as e) {
        @if (!e.hasMobile || !e.hasPin) {
          <p class="warn">
            They need a mobile number and a PIN to sign in to the portal: set {{ !e.hasMobile ? 'them' : 'a PIN' }} on their profile.
          </p>
        }
        <p class="muted small">They use them in the portal, on their phone. Saving signs them out of it: they sign in again to see their tabs.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [mat-dialog-close]="picked()">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    .all {
      display: block;
      margin-bottom: 4px;
      font-weight: 500;
    }
    .tabs {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
      gap: 0 12px;
      margin-bottom: 8px;
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.85em;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
    .warn {
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
  `,
})
export class TabsDialog {
  protected readonly data = inject<TabsDialogData>(MAT_DIALOG_DATA);
  protected readonly tabs = hrTabs;
  protected readonly chosen = signal<ReadonlySet<HrTab>>(new Set(this.data.tabs));
  protected readonly picked = computed(() => hrTabsOf([...this.chosen()]));
  protected readonly every = computed(() => this.chosen().size === allHrTabs.length);
  protected readonly some = computed(() => this.chosen().size > 0 && !this.every());

  protected tick(tab: HrTab, on: boolean): void {
    this.chosen.update((now) => {
      const next = new Set(now);
      if (on) {
        next.add(tab);
      } else {
        next.delete(tab);
      }
      return next;
    });
  }

  protected all(on: boolean): void {
    this.chosen.set(new Set(on ? allHrTabs : []));
  }
}

// Employee permissions (صلاحيات الموظفين), a manager's: the HR & Payroll tabs each works. An
// employee works theirs in the portal, on their phone; an accountant, on the till, with every tab
// until a manager ticks theirs. Each sees only their tabs, and what they change goes to a manager.
@Component({
  selector: 'app-permissions-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatTooltipModule, RouterLink],
  template: `
    <p class="note">
      Give each the tabs of HR &amp; Payroll they work. An employee uses theirs in the portal on their phone, signing in with their mobile number and
      PIN; an accountant, here on the till. They see only those tabs, and what they change there waits for a manager. A manager has every tab.
    </p>
    <div class="toolbar">
      <mat-form-field subscriptSizing="dynamic" class="find">
        <mat-label>Find · بحث</mat-label>
        <input matInput [ngModel]="query()" (ngModelChange)="query.set($event)" aria-label="Find" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error(); as e) {
      <p class="error">{{ e }}</p>
    } @else {
      <h3>Employees <span class="inline-ar">الموظفين</span></h3>
      <div class="table-wrap">
        <table class="grid employees">
          <thead>
            <tr>
              <th>Employee<span class="ar">الموظف</span></th>
              <th>HR tabs<span class="ar">الصلاحيات</span></th>
              <th>Portal<span class="ar">البوابة</span></th>
              <th class="num"><span class="visually-hidden">Change</span></th>
            </tr>
          </thead>
          <tbody>
            @for (e of employees(); track e.id) {
              <tr>
                <td>
                  <a [routerLink]="['/hr/employees', e.id]"><strong>{{ e.name }}</strong></a>
                  <div class="muted small">{{ e.jobTitle }} · {{ e.department }}</div>
                </td>
                <td>
                  @for (t of e.tabs; track t) {
                    <span class="tab">{{ label(t) }}</span>
                  } @empty {
                    <span class="muted">None</span>
                  }
                </td>
                <td class="small" [class.muted]="!e.hasMobile || !e.hasPin">
                  {{ !e.hasMobile ? 'No mobile number' : !e.hasPin ? 'No PIN yet' : 'Can sign in' }}
                </td>
                <td class="num">
                  <button mat-icon-button (click)="changeEmployee(e)" matTooltip="Change their tabs" [attr.aria-label]="'Change ' + e.name + '’s tabs'">
                    <mat-icon>edit</mat-icon>
                  </button>
                </td>
              </tr>
            } @empty {
              @if (!loading()) {
                <tr><td colspan="4" class="muted">{{ query() ? 'No one employed is called that.' : 'No one is employed yet.' }}</td></tr>
              }
            }
          </tbody>
        </table>
      </div>

      <h3>Accountants <span class="inline-ar">المحاسبين</span></h3>
      <div class="table-wrap">
        <table class="grid staff">
          <thead>
            <tr>
              <th>Accountant<span class="ar">المحاسب</span></th>
              <th>HR tabs<span class="ar">الصلاحيات</span></th>
              <th>Set by<span class="ar">بواسطة</span></th>
              <th class="num"><span class="visually-hidden">Change</span></th>
            </tr>
          </thead>
          <tbody>
            @for (s of staff(); track s.id) {
              <tr>
                <td><strong>{{ s.name }}</strong></td>
                <td>
                  @if (!s.restricted) {
                    <span class="muted">Every tab, until you tick theirs</span>
                  } @else {
                    @for (t of s.tabs; track t) {
                      <span class="tab">{{ label(t) }}</span>
                    } @empty {
                      <span class="muted">None</span>
                    }
                  }
                </td>
                <td class="muted small">{{ s.setBy ? s.setBy + ', ' + (s.setAtUtc | date: 'd MMM y') : '—' }}</td>
                <td class="num">
                  <button mat-icon-button (click)="changeStaff(s)" matTooltip="Change their tabs" [attr.aria-label]="'Change ' + s.name + '’s tabs'">
                    <mat-icon>edit</mat-icon>
                  </button>
                </td>
              </tr>
            } @empty {
              @if (!loading()) {
                <tr><td colspan="4" class="muted">{{ query() ? 'No accountant is called that.' : 'No accountant has a till account.' }}</td></tr>
              }
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    h3 {
      margin: 20px 0 6px;
      font: var(--mat-sys-title-small);
    }
    .find {
      min-width: 240px;
    }
    .tab {
      display: inline-block;
      margin: 2px 4px 2px 0;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .small {
      font-size: 0.85em;
    }
  `,
})
export class PermissionsPage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly query = signal('');
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  private readonly allEmployees = signal<EmployeeHrAccess[]>([]);
  private readonly allStaff = signal<StaffHrAccess[]>([]);

  protected readonly employees = computed(() => this.allEmployees().filter((e) => this.matches(e.name, e.jobTitle, e.department)));
  protected readonly staff = computed(() => this.allStaff().filter((s) => this.matches(s.name)));

  constructor() {
    void this.load();
  }

  protected label(tab: HrTab): string {
    return hrTabs.find((t) => t.tab === tab)?.en ?? tab;
  }

  protected async changeEmployee(e: EmployeeHrAccess): Promise<void> {
    const tabs = await this.pick({ name: e.name, tabs: e.tabs, employee: { hasMobile: e.hasMobile, hasPin: e.hasPin } });
    if (!tabs) {
      return;
    }
    try {
      const saved = await firstValueFrom(this.api.setEmployeeTabs(e.id, tabs));
      this.allEmployees.update((list) => list.map((x) => (x.id === saved.id ? saved : x)));
      this.notify.info(this.said(saved.name, saved.tabs));
    } catch (error) {
      this.notify.error(errorMessage(error, 'Their tabs could not be saved.'));
    }
  }

  protected async changeStaff(s: StaffHrAccess): Promise<void> {
    const tabs = await this.pick({ name: s.name, tabs: s.tabs });
    if (!tabs) {
      return;
    }
    try {
      const saved = await firstValueFrom(this.api.setStaffTabs(s.id, tabs));
      this.allStaff.update((list) => list.map((x) => (x.id === saved.id ? saved : x)));
      this.notify.info(this.said(saved.name, saved.tabs));
    } catch (error) {
      this.notify.error(errorMessage(error, 'Their tabs could not be saved.'));
    }
  }

  private async pick(data: TabsDialogData): Promise<HrTab[] | undefined> {
    return firstValueFrom(this.dialog.open<TabsDialog, TabsDialogData, HrTab[]>(TabsDialog, { data, width: '520px' }).afterClosed());
  }

  private said(name: string, tabs: readonly HrTab[]): string {
    return tabs.length ? `${name}’s HR tabs: ${tabs.map((t) => this.label(t)).join(', ')}.` : `${name} has no HR tabs now.`;
  }

  private matches(...text: string[]): boolean {
    const q = this.query().trim().toLowerCase();
    return !q || text.some((t) => t.toLowerCase().includes(q));
  }

  private async load(): Promise<void> {
    try {
      const list = await firstValueFrom(this.api.access());
      this.allEmployees.set(list.employees);
      this.allStaff.set(list.staff);
    } catch (error) {
      this.error.set(errorMessage(error, 'Who has which tab could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
