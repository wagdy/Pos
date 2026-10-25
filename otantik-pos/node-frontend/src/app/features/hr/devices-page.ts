import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { aheadBy, addressOf, isIPv4, saveDeviceLink, savedDeviceLink, ZkPort } from '../../core/hr/device-link';
import { errorMessage } from '../../core/http/error-message';
import { AttendanceDevice, AttendancePolicy, DeviceCommand, HrApi } from '../../core/hr/hr.api';
import { HrAccess } from '../../core/hr/hr-access';
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

// From which day to which the device's attendance is read again.
@Component({
  selector: 'app-read-attendance-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Read {{ data.device }}'s attendance again</h2>
    <mat-dialog-content>
      <p class="muted">Every punch it holds from one day to another, sent again. What the till has already is kept once; what it missed comes in.</p>
      <div class="row">
        <mat-form-field>
          <mat-label>From · من</mat-label>
          <input matInput type="date" [(ngModel)]="from" [max]="to" aria-label="From" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>To · إلى</mat-label>
          <input matInput type="date" [(ngModel)]="to" [min]="from" aria-label="To" />
        </mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!from || !to || to < from" [mat-dialog-close]="{ from, to }">Read it</button>
    </mat-dialog-actions>
  `,
  styles: `
    .row {
      display: flex;
      gap: 12px;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class ReadAttendanceDialog {
  protected readonly data = inject<{ device: string; from: string; to: string }>(MAT_DIALOG_DATA);
  protected from = this.data.from;
  protected to = this.data.to;
}

// The attendance devices (أجهزة البصمة): a ZKTeco K40 Pro pushes its punches to this till over the
// restaurant's network. One that calls is listed as waiting, and refused, until a manager approves
// it by name. A manager can leave an approved one a command, which it takes the next time it asks:
// to list its users, for the Employees tab, or to send its attendance from a day again. Below, how
// punches are read: the grace for arriving late, and how close two scans are to count as one. A
// manager's to change.
//
// The device keeps its punches while the till is away, and sends them all when it is back: the
// laptop needs the same address on the network each time (a reservation on the router), as the
// device is given it once.
//
// Beside that, the till can reach the device itself, at its address on ZKTeco's own port (4370):
// to test that it answers, and to read its users and its whole log at once (Connection settings,
// a manager's). The punches read are kept as ADMS's are, once each; a device read for the first
// time is approved by it.
@Component({
  selector: 'app-devices-page',
  imports: [
    DatePipe,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatTooltipModule,
    RouterLink,
  ],
  template: `
    <section class="setup">
      <h2>Connecting a device <span class="inline-ar">ربط جهاز البصمة</span></h2>
      <p>On the K40 Pro: <strong>Main menu → COMM. → Cloud Server Setting</strong>. Server mode ADMS, domain name off, proxy off, HTTPS off.</p>
      <dl>
        <div>
          <dt>Server address</dt>
          <dd>{{ address() }}</dd>
        </div>
        <div>
          <dt>Server port</dt>
          <dd>{{ port() }}</dd>
        </div>
      </dl>
      @if (devicePort()) {
        <p class="muted small">Port {{ devicePort() }} is the device’s own: it answers nothing but the device, and the till’s screens stay on this machine.</p>
      } @else if (local) {
        <p class="muted small">This screen is open on the till itself: give the device this machine’s address on the restaurant’s network, not “localhost”.</p>
      }
      <p class="muted small">It shows up below within a minute, waiting. Approve it, and its punches start coming in, the ones it already holds too.</p>
      <p class="muted small">
        Reserve this machine’s address on the router, so it keeps it: the device holds its punches while the till is away and sends them all when it
        can reach this address again.
      </p>
    </section>

    @if (canApprove()) {
      <section class="link" aria-label="Connection settings">
        <h2>Connection settings <span class="inline-ar">إعدادات التوصيل</span></h2>
        <p class="muted small">
          The till reaches the device at its own address, to test it answers and to read its users and its whole log now. Its punches keep coming
          over ADMS too; none is kept twice.
        </p>
        <div class="fields">
          <mat-form-field>
            <mat-label>Device IP address · عنوان الجهاز</mat-label>
            <input matInput [(ngModel)]="deviceAddress" placeholder="192.168.1.106" inputmode="decimal" aria-label="Device IP address" />
            <mat-hint>On the device: COMM. → Ethernet → IP Address</mat-hint>
          </mat-form-field>
          <mat-form-field class="short">
            <mat-label>Port · المنفذ</mat-label>
            <input matInput type="number" min="1" max="65535" [(ngModel)]="tcpPort" aria-label="Device port" />
          </mat-form-field>
          <mat-form-field class="short">
            <mat-label>Comm key · مفتاح الاتصال</mat-label>
            <input matInput type="number" min="0" [(ngModel)]="commKey" aria-label="Comm key" />
            <mat-hint>0 unless one is set on it</mat-hint>
          </mat-form-field>
          <button mat-stroked-button (click)="test()" [disabled]="!linkable() || linking() !== null">
            <mat-icon>lan</mat-icon> Test connection <span class="inline-ar">اختبار التوصيل</span>
          </button>
          <button mat-flat-button (click)="sync()" [disabled]="!linkable() || linking() !== null">
            <mat-icon>sync</mat-icon> Sync device <span class="inline-ar">مزامنة</span>
          </button>
        </div>
        @if (linking(); as doing) {
          <mat-progress-bar mode="indeterminate" />
          <p class="muted small">{{ doing === 'test' ? 'Reaching the device…' : 'Reading its users and its whole log: up to a minute when it holds a lot…' }}</p>
        } @else if (linked(); as r) {
          <p class="result" [class.bad-text]="!r.ok">
            <mat-icon>{{ r.ok ? 'check_circle' : 'error' }}</mat-icon>
            <span>
              {{ r.text }}
              @if (r.unlinked && access.has('Employees')) {
                <a routerLink="/hr/employees">Link {{ r.unlinked === 1 ? 'it' : 'them' }} on the Employees tab</a>
              }
            </span>
          </p>
        }
      </section>
    }

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
                @if (d.callingIn) {
                  <div class="small adms ok-text"><mat-icon inline>wifi</mat-icon> Calling in over ADMS: punches come as they are made</div>
                } @else {
                  <div class="small adms bad-text">
                    <mat-icon inline>wifi_off</mat-icon> Not calling in over ADMS@if (d.lastCalledInAtUtc) { since {{ d.lastCalledInAtUtc | date: 'd MMM, HH:mm' }}}:
                    punches come only with Sync
                  </div>
                }
                @if (d.clockAheadMinutes !== null) {
                  <div class="small adms bad-text">
                    <mat-icon inline>schedule</mat-icon> Its clock is {{ ahead(d.clockAheadMinutes) }} ahead: restart it to set its clock by the till's
                  </div>
                }
              </td>
              <td class="nowrap">{{ d.lastSeenAtUtc | date: 'd MMM, HH:mm' }}</td>
              <td class="num nowrap" [attr.rowspan]="d.isApproved ? 2 : 1">
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
            @if (d.isApproved) {
              @let c = d.commands[0];
              <tr class="sync">
                <td colspan="3">
                  @if (canApprove()) {
                    <button mat-stroked-button (click)="readUsers(d)"><mat-icon>groups</mat-icon> Read its users <span class="inline-ar">قراءة المستخدمين</span></button>
                    <button mat-button (click)="readAttendance(d)"><mat-icon>history</mat-icon> Read its attendance again</button>
                  }
                  @if (c) {
                    <span class="command" [class.bad-text]="c.returnCode !== null && c.returnCode !== 0">
                      {{ what(c) }}:
                      @if (c.answeredAtUtc === null) {
                        @if (c.sentAtUtc === null) {
                          waiting for the device to ask, asked {{ c.queuedAtUtc | date: 'HH:mm' }}
                        } @else {
                          sent {{ c.sentAtUtc | date: 'HH:mm' }}, waiting for its answer
                        }
                      } @else if (c.returnCode === 0) {
                        done {{ c.answeredAtUtc | date: 'd MMM, HH:mm' }}
                        @if (c.kind === 'ReadUsers' && access.has('Employees')) {
                          · <a routerLink="/hr/employees">its users are on the Employees tab</a>
                        }
                      } @else {
                        refused, code {{ c.returnCode }}: its firmware may not take this command
                      }
                    </span>
                  }
                </td>
              </tr>
            }
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
    .link {
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
      .fields {
        margin-top: 8px;
      }
      mat-form-field.short {
        width: 150px;
      }
      mat-progress-bar {
        margin-top: 8px;
      }
    }
    .result {
      display: flex;
      align-items: flex-start;
      gap: 6px;

      mat-icon {
        flex: none;
        color: #2e7d32;
      }
      &.bad-text mat-icon {
        color: var(--mat-sys-error);
      }
      a {
        margin-left: 4px;
      }
    }
    .bad-text {
      color: var(--mat-sys-error);
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
    tr.sync td {
      background: var(--mat-sys-surface-container-lowest);

      button {
        margin-right: 8px;
      }
    }
    .adms {
      margin-top: 2px;
    }
    .ok-text {
      color: #2e7d32;
    }
    .command {
      font-size: 0.85em;
      color: var(--mat-sys-on-surface-variant);

      &.bad-text {
        color: var(--mat-sys-error);
      }
    }
  `,
})
export class DevicesPage {
  private readonly api = inject(HrApi);
  protected readonly access = inject(HrAccess);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly approvals = inject(Approvals);
  private readonly destroyRef = inject(DestroyRef);
  private poll: ReturnType<typeof setInterval> | null = null;

  // A manager's, or an employee's holding the tab in the portal.
  protected readonly canApprove = computed(() => this.access.canDecide('Devices'));
  protected readonly ahead = aheadBy;
  protected readonly devices = signal<AttendanceDevice[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  private readonly saved = signal<AttendancePolicy | null>(null);
  protected grace = 0;
  protected doubleScan = 3;

  // Where the device reaches this till: the device's own port and this machine's network address
  // when the till has one; otherwise the address and port this screen is served from.
  protected readonly local = ['localhost', '127.0.0.1', '::1', '[::1]'].includes(location.hostname);
  protected readonly devicePort = signal<number | null>(null);
  private readonly addresses = signal<string[]>([]);
  protected readonly address = computed(() =>
    this.devicePort() && this.local && this.addresses().length ? this.addresses().join(' or ') : location.hostname,
  );
  protected readonly port = computed(() => String(this.devicePort() ?? (location.port || (location.protocol === 'https:' ? '443' : '80'))));

  // Where the till reaches the device: as last used here, else the address a device last called
  // from, on ZKTeco's port, with no comm key unless one is set on it.
  private readonly link = savedDeviceLink();
  protected deviceAddress = this.link?.address ?? '';
  protected tcpPort = this.link?.port ?? ZkPort;
  protected commKey = this.link?.commKey ?? 0;
  protected readonly linking = signal<'test' | 'sync' | null>(null);
  protected readonly linked = signal<{ ok: boolean; text: string; unlinked?: number } | null>(null);

  constructor() {
    this.destroyRef.onDestroy(() => this.stopWatching());
    void this.load();
    void firstValueFrom(this.api.deviceConnection()).then(
      (c) => {
        this.devicePort.set(c.devicePort);
        this.addresses.set(c.addresses);
      },
      () => undefined,
    );
  }

  protected linkable(): boolean {
    const port = Number(this.tcpPort);
    return (
      isIPv4(this.deviceAddress) &&
      Number.isInteger(port) &&
      port >= 1 &&
      port <= 65535 &&
      Number(this.commKey) >= 0
    );
  }

  protected async test(): Promise<void> {
    const address = this.deviceAddress.trim();
    this.linking.set('test');
    this.linked.set(null);
    try {
      const probe = await firstValueFrom(this.api.testDevice(address, Number(this.tcpPort), Number(this.commKey) || 0));
      this.linked.set({ ok: probe.reached, text: probe.message });
      if (probe.reached) {
        this.remember(address);
        this.notify.info(probe.message);
      } else {
        this.notify.error(null, probe.message);
      }
    } catch (error) {
      this.linked.set({ ok: false, text: errorMessage(error, 'The device could not be tested.') });
      this.notify.error(error, 'The device could not be tested.');
    } finally {
      this.linking.set(null);
    }
  }

  protected async sync(): Promise<void> {
    const address = this.deviceAddress.trim();
    this.linking.set('sync');
    this.linked.set(null);
    try {
      const r = await firstValueFrom(this.api.syncDevice(address, Number(this.tcpPort), Number(this.commKey) || 0));
      this.remember(address);
      const text =
        `${r.deviceName}: ${count(r.users, 'user')} and ${count(r.punches, 'punch', 'punches')} read, ` +
        `${r.newPunches === 0 ? 'none of them new' : `${r.newPunches} new`}.` +
        (r.newDevice ? ' New to the till: approved, and listed below.' : '') +
        (r.usersNotOnTheTill
          ? ` ${r.usersNotOnTheTill} of its users ${r.usersNotOnTheTill === 1 ? 'is' : 'are'} not linked to anyone on the till yet.`
          : '');
      this.linked.set({ ok: true, text, unlinked: r.usersNotOnTheTill });
      this.notify.info(`Synced ${r.deviceName}: ${r.newPunches} new ${r.newPunches === 1 ? 'punch' : 'punches'}.`);
      await this.load(false);
    } catch (error) {
      this.linked.set({ ok: false, text: errorMessage(error, 'The device could not be read.') });
      this.notify.error(error, 'The device could not be read.');
    } finally {
      this.linking.set(null);
    }
  }

  private remember(address: string): void {
    saveDeviceLink({ address, port: Number(this.tcpPort), commKey: Number(this.commKey) || 0 });
  }

  protected what(c: DeviceCommand): string {
    return c.kind === 'ReadUsers' ? 'Its users' : 'Its attendance';
  }

  protected async readUsers(device: AttendanceDevice): Promise<void> {
    await this.command(() => this.api.readDeviceUsers(device.id), `${device.name}: asked for its users. It takes the command the next time it asks, within seconds.`);
  }

  protected async readAttendance(device: AttendanceDevice): Promise<void> {
    const today = new Date();
    const to = isoDate(today);
    const from = isoDate(new Date(today.getFullYear(), today.getMonth() - 1, 1));
    const answer = await firstValueFrom(
      this.dialog
        .open<ReadAttendanceDialog, { device: string; from: string; to: string }, { from: string; to: string }>(ReadAttendanceDialog, {
          data: { device: device.name ?? device.serialNumber, from, to },
          width: '440px',
        })
        .afterClosed(),
    );
    if (answer) {
      await this.command(() => this.api.readDeviceAttendance(device.id, answer.from, answer.to), `${device.name}: asked for its attendance again.`);
    }
  }

  private async command(call: () => ReturnType<HrApi['readDeviceUsers']>, done: string): Promise<void> {
    try {
      await firstValueFrom(call());
      this.notify.info(done);
      await this.load();
    } catch (error) {
      this.notify.error(error, 'The command could not be left for the device.');
    }
  }

  // A command less than ten minutes old and not answered yet: the list is read again every few
  // seconds until it is.
  private waiting(): boolean {
    const recent = Date.now() - 10 * 60_000;
    return this.devices().some((d) => d.commands[0] && d.commands[0].answeredAtUtc === null && Date.parse(d.commands[0].queuedAtUtc) > recent);
  }

  private watch(): void {
    if (this.poll || !this.waiting()) {
      return;
    }
    this.poll = setInterval(() => {
      void this.load(false).then(() => {
        if (!this.waiting()) {
          this.stopWatching();
        }
      });
    }, 5_000);
  }

  private stopWatching(): void {
    if (this.poll) {
      clearInterval(this.poll);
      this.poll = null;
    }
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

  private async load(showLoading = true): Promise<void> {
    this.loading.set(showLoading);
    this.error.set(null);
    try {
      const [devices, settings] = await Promise.all([firstValueFrom(this.api.devices()), firstValueFrom(this.api.settings())]);
      this.devices.set(devices);
      this.deviceAddress ||= devices.map(addressOf).find((a) => a !== null) ?? '';
      if (!this.saved()) {
        this.grace = settings.graceMinutes;
        this.doubleScan = settings.doubleScanMinutes;
      }
      this.saved.set(settings);
      this.watch();
    } catch (error) {
      this.error.set(errorMessage(error, 'The devices could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }
}

function count(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

function isoDate(day: Date): string {
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
}
