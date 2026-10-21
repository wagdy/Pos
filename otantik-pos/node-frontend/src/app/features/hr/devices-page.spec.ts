import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AttendanceDevice, DeviceCommand, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { DeviceNameDialog, DevicesPage, ReadAttendanceDialog } from './devices-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

const waiting: AttendanceDevice = {
  id: 'd1', serialNumber: 'CQZ7232060112', name: null, isApproved: false, approvedBy: null, approvedAtUtc: null,
  firstSeenAtUtc: '2026-10-20T08:00:00Z', lastSeenAtUtc: '2026-10-20T08:00:00Z', lastAddress: '192.168.1.40', commands: [],
};

function command(extra: Partial<DeviceCommand> = {}): DeviceCommand {
  return {
    id: 'c1', number: 2, kind: 'ReadUsers', text: 'DATA QUERY USERINFO', queuedBy: 'Omar', queuedAtUtc: '2026-10-20T08:00:00Z', sentAtUtc: null,
    answeredAtUtc: null, returnCode: null, ...extra,
  };
}
const approved: AttendanceDevice = { ...waiting, id: 'd2', serialNumber: 'CQZ0000000002', name: 'Kitchen door', isApproved: true, approvedBy: 'Omar' };

describe('the attendance devices', () => {
  const devices = vi.fn();
  const settings = vi.fn();
  const approveDevice = vi.fn();
  const revokeDevice = vi.fn();
  const saveSettings = vi.fn();
  const readDeviceUsers = vi.fn();
  const readDeviceAttendance = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HrApi, useValue: { devices, settings, approveDevice, revokeDevice, saveSettings, readDeviceUsers, readDeviceAttendance } },
        { provide: Approvals, useValue: { canApprove } },
        { provide: NotifyService, useValue: notify },
        { provide: MatDialog, useValue: { open } },
      ],
    });
    const fixture = TestBed.createComponent(DevicesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    devices.mockReset().mockReturnValue(of([waiting, approved]));
    settings.mockReset().mockReturnValue(of({ graceMinutes: 0, doubleScanMinutes: 3 }));
    approveDevice.mockReset().mockReturnValue(of({ ...waiting, isApproved: true, name: 'Back door' }));
    revokeDevice.mockReset().mockReturnValue(of({ ...approved, isApproved: false }));
    saveSettings.mockReset().mockImplementation((p: object) => of(p));
    readDeviceUsers.mockReset().mockReturnValue(of(command()));
    readDeviceAttendance.mockReset().mockReturnValue(of(command({ kind: 'ReadAttendance' })));
    open.mockReset();
    notify.info.mockReset();
    canApprove.set(true);
  });

  it('says where to point the device, and lists those waiting apart from those approved', async () => {
    const { el } = await render();

    expect(text(el.querySelector('.setup')!)).toContain('Main menu → COMM. → Cloud Server Setting');
    expect([...el.querySelectorAll('.setup dd')].map(text)).toEqual([location.hostname, location.port || '80']);
    const rows = [...el.querySelectorAll('tbody tr')];
    expect(text(rows[0].querySelector('strong')!)).toBe('Unnamed device');
    expect(text(rows[0].querySelector('td .muted')!)).toBe('Serial CQZ7232060112 · 192.168.1.40');
    expect(text(rows[0].querySelectorAll('td')[1])).toContain('Waiting for approval');
    expect(text(rows[0].querySelector('button')!)).toBe('Approve');
    expect(text(rows[1].querySelector('.status')!)).toBe('Approvedمعتمد');
    expect(text(rows[1].querySelectorAll('td')[1].querySelector('.muted')!)).toBe('by Omar');
  });

  it('approves a device by the name a manager gives it, and refuses one again', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of('Back door') });

    await page['name'](waiting);
    expect(open.mock.lastCall![0]).toBe(DeviceNameDialog);
    expect(approveDevice).toHaveBeenCalledWith('d1', 'Back door');
    expect(notify.info).toHaveBeenCalledWith('Back door approved: its punches come in from now on.');

    await page['revoke'](approved);
    expect(revokeDevice).toHaveBeenCalledWith('d2');
  });

  it('asks an approved device for its users or its attendance again, and says how far the command got', async () => {
    devices.mockReturnValue(of([waiting, { ...approved, commands: [command()] }]));
    const { el, page, fixture } = await render();
    const sync = () => text(el.querySelector('tr.sync .command')!);

    expect([...el.querySelectorAll('tr.sync button')].map(text)).toEqual(['groups Read its users قراءة المستخدمين', 'history Read its attendance again']);
    expect(sync()).toContain('Its users: waiting for the device to ask, asked');

    await page['readUsers'](approved);
    expect(readDeviceUsers).toHaveBeenCalledWith('d2');
    expect(notify.info).toHaveBeenCalledWith('Kitchen door: asked for its users. It takes the command the next time it asks, within seconds.');

    open.mockReturnValue({ afterClosed: () => of({ from: '2026-09-01', to: '2026-10-21' }) });
    await page['readAttendance'](approved);
    expect(open.mock.lastCall![0]).toBe(ReadAttendanceDialog);
    expect(readDeviceAttendance).toHaveBeenCalledWith('d2', '2026-09-01', '2026-10-21');

    devices.mockReturnValue(of([{ ...approved, commands: [command({ sentAtUtc: '2026-10-20T08:00:05Z', answeredAtUtc: '2026-10-20T08:00:09Z', returnCode: 0 })] }]));
    await page['load']();
    fixture.detectChanges();
    expect(sync()).toContain('Its users: done');
    expect(el.querySelector('tr.sync a')!.getAttribute('href')).toBe('/hr/employees');

    devices.mockReturnValue(of([{ ...approved, commands: [command({ sentAtUtc: '2026-10-20T08:00:05Z', answeredAtUtc: '2026-10-20T08:00:09Z', returnCode: -1002 })] }]));
    await page['load']();
    fixture.detectChanges();
    expect(sync()).toBe('Its users: refused, code -1002: its firmware may not take this command');
  });

  it('saves how punches are read, once changed', async () => {
    const { page } = await render();
    expect(page['settingsChanged']()).toBe(false);

    page['grace'] = 10;
    expect(page['settingsChanged']()).toBe(true);
    await page['saveSettings']();
    expect(saveSettings).toHaveBeenCalledWith({ graceMinutes: 10, doubleScanMinutes: 3 });
    expect(page['settingsChanged']()).toBe(false);
  });

  it('shows an accountant the devices and the settings, but nothing to change', async () => {
    canApprove.set(false);
    const { el } = await render();

    expect(el.querySelectorAll('tbody button').length).toBe(0);
    expect([...el.querySelectorAll<HTMLInputElement>('.settings input')].every((i) => i.disabled)).toBe(true);
    expect([...el.querySelectorAll('.settings button')].length).toBe(0);
  });
});
