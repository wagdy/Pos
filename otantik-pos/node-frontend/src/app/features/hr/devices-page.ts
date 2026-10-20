import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import { errorMessage } from '../../core/http/error-message';
import { AttendanceDevice, AttendancePolicy, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';

// A device's name, as the manager gives it on approving it.
@Component({
  selector: 'app-device-name-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.device.isApproved ? 'Rename' : 'Approve' }} {{ data.device.serialNumber }}</h2>
    <mat-dialog-content>
      @if (!data.device.isApproved) {
        <p class="muted">Its punches are taken from now on, and what it kept while it waited is sent.</p>
      }
      <mat-form-field class="wide">
        <mat-label>Name · الاسم</mat-label>
        <input matInput [(ngModel)]="name" maxlength="100" placeholder="Kitchen door" aria-label="Device name" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!name.trim()" [mat-dialog-close]="name.trim()">{{ data.device.isApproved ? 'Save' : 'Approve' }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide {
      width: 100%;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class DeviceNameDialog {
  protected readonly data = inject<{ device: AttendanceDevice }>(MAT_DIALOG_DATA);
  protected name = this.data.device.name ?? '';
}

// The attendance devices (أجهزة البصمة): a ZKTeco K40 Pro pushes its punches to this till over the
// restaurant's network. One that calls is listed as waiting, and refused, until a manager approves
// it by name. Below, how punches are read: the grace for arriving late, and how close two scans are
// to count as one. A manager's to change.
@Component({
  selector: 'app-devices-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <section class="setup">
      <h2>Connecting a device <span class="inline-ar">ربط جهاز البصمة</span></h2>
      <p>On the K40 Pro: <strong>Main menu → COMM. → Cloud Server Setting</strong>. Server mode ADMS, domain name off, proxy off, HTTPS off.</p>
      <dl>
        <div>
          <dt>Server address</dt>
          <dd>{{ address }}</dd>
        </div>
        <div>
          <dt>Server port</dt>
          <dd>{{ port }}</dd>
        </div>
      </dl>
      @if (local) {
        <p class="muted small">This screen is open on the till itself: give the device this machine’s address on the restaurant’s network, not “localhost”.</p>
      }
      <p class="muted small">It shows up below within a minute, waiting. Approve it, and its punches start coming in.</p>
    </section>

    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
    <div class="table-wrap">
      <table class="grid">
        <thead>
          <tr>
            <th>Device<span class="ar">الجهاز</span></th>
            <th>Status<span class="ar">الحالة</span></th>
            <th>Last heard from<span class="ar">آخر اتصال</span></th>
            <th class="num"><span class="visually-hidden">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          @for (d of devices(); track d.id) {
            <tr>
              <td>
                <strong>{{ d.name ?? 'Unnamed device' }}</strong>
                <div class="muted small">Serial {{ d.serialNumber }}@if (d.lastAddress) { · {{ d.lastAddress }}}</div>
              </td>
              <td>
                @if (d.isApproved) {
                  <span class="status ok">Approved<span class="ar">معتمد</span></span>
                  <div class="muted small">by {{ d.approvedBy }}</div>
                } @else {
                  <span class="pending-badge">Waiting for approval<span class="ar">بانتظار الموافقة</span></span>
                  <div class="muted small">Nothing it sends is taken until then</div>
                }
              </td>
              <td class="nowrap">{{ d.lastSeenAtUtc | date: 'd MMM, HH:mm' }}</td>
              <td class="num nowrap">
                @if (canApprove()) {
                  @if (d.isApproved) {
                    <button mat-icon-button (click)="name(d)" matTooltip="Rename" [attr.aria-label]="'Rename ' + d.serialNumber"><mat-icon>edit</mat-icon></button>
                    <button mat-icon-button class="danger" (click)="revoke(d)" matTooltip="Refuse it again" [attr.aria-label]="'Refuse ' + d.serialNumber">
                      <mat-icon>block</mat-icon>
                    </button>
                  } @else {
                    <button mat-flat-button (click)="name(d)">Approve</button>
                  }
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="4" class="muted">No device has called yet.</td></tr>
          }
        </tbody>
      </table>
    </div>

    <section class="settings" aria-label="How punches are read">
      <h2>How punches are read <span class="inline-ar">إعدادات الحضور</span></h2>
      <div class="fields">
        <mat-form-field>
          <mat-label>Grace for arriving late · سماحية التأخير</mat-label>
          <input matInput type="number" min="0" max="120" [(ngModel)]="grace" [disabled]="!canApprove()" aria-label="Grace for arriving late" />
          <span matTextSuffix>&nbsp;min</span>
          <mat-hint>Up to this late is on time; later counts from the shift’s start</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Scans this close are one · البصمة المكررة</mat-label>
          <input matInput type="number" min="0" max="30" [(ngModel)]="doubleScan" [disabled]="!canApprove()" aria-label="Scans this close are one" />
          <span matTextSuffix>&nbsp;min</span>
          <mat-hint>A finger put on twice</mat-hint>
        </mat-form-field>
        @if (canApprove()) {
          <button mat-flat-button (click)="saveSettings()" [disabled]="!settingsChanged()">Save</button>
        }
      </div>
    </section>
  `,
  styleUrl: '../costing/costing.scss',
  styles: `
    .setup,
    .settings {
      margin-bottom: 16px;
      padding: 12px 16px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);

      h2 {
        margin: 0 0 8px;
        font: var(--mat-sys-title-medium);
      }
      p {
        margin: 4px 0;
      }
    }
    .settings {
      margin: 16px 0 0;
    }
    dl {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 32px;
      margin: 8px 0;

      dt {
        font: var(--mat-sys-label-medium);
        color: var(--mat-sys-on-surface-variant);
      }
      dd {
        margin: 0;
        font: var(--mat-sys-title-medium);
        font-family: ui-monospace, monospace;
      }
    }
    .fields {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 16px;

      mat-form-field {
        width: 280px;
      }
    }
    .status {
      display: inline-block;
      padding: 1px 8px;
      border-radius: 999px;
      font: var(--mat-sys-label-small);

      &.ok {
        background: color-mix(in srgb, #2e7d32 16%, transparent);
      }
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
export class DevicesPage {
  private readonly api = inject(HrApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);

  protected readonly canApprove = this.approvals.canApprove;
  protected readonly devices = signal<AttendanceDevice[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  private readonly saved = signal<AttendancePolicy | null>(null);
  protected grace = 0;
  protected doubleScan = 3;

  // Where the device reaches this till: the address and port this screen is served from.
  protected readonly address = location.hostname;
  protected readonly port = location.port || (location.protocol === 'https:' ? '443' : '80');
  protected readonly local = ['localhost', '127.0.0.1', '::1', '[::1]'].includes(location.hostname);

  constructor() {
    void this.load();
  }

  protected settingsChanged(): boolean {
    const saved = this.saved();
    return !!saved && (saved.graceMinutes !== this.grace || saved.doubleScanMinutes !== this.doubleScan);
  }

  protected async name(device: AttendanceDevice): Promise<void> {
    const name = await firstValueFrom(
      this.dialog.open<DeviceNameDialog, { device: AttendanceDevice }, string>(DeviceNameDialog, { data: { device }, width: '420px' }).afterClosed(),
    );
    if (!name) {
      return;
    }
    try {
      await firstValueFrom(this.api.approveDevice(device.id, name));
      this.notify.info(device.isApproved ? 'Renamed.' : `${name} approved: its punches come in from now on.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The device could not be approved.');
    }
  }

  protected async revoke(device: AttendanceDevice): Promise<void> {
    try {
      await firstValueFrom(this.api.revokeDevice(device.id));
      this.notify.info(`${device.name ?? device.serialNumber} is refused again: nothing more it sends is taken.`);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The device could not be refused.');
    }
  }

  protected async saveSettings(): Promise<void> {
    try {
      const saved = await firstValueFrom(this.api.saveSettings({ graceMinutes: this.grace, doubleScanMinutes: this.doubleScan }));
      this.saved.set(saved);
      this.notify.info('Saved: the days are read this way from now on, in every month not yet finalized.');
    } catch (error) {
      this.notify.error(error, 'The settings could not be saved.');
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [devices, settings] = await Promise.all([firstValueFrom(this.api.devices()), firstValueFrom(this.api.settings())]);
      this.devices.set(devices);
      if (!this.saved()) {
        this.grace = settings.graceMinutes;
        this.doubleScan = settings.doubleScanMinutes;
      }
      this.saved.set(settings);
    } catch (error) {
      this.error.set(errorMessage(error, 'The devices could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}
