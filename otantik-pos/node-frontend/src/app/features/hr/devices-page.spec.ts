import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AttendanceDevice, DeviceCommand, HrApi } from '../../core/hr/hr.api';
import { saveDeviceLink, savedDeviceLink } from '../../core/hr/device-link';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { DeviceNameDialog, DevicesPage, ReadAttendanceDialog } from './devices-page';
import { HrAccess } from '../../core/hr/hr-access';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

const waiting: AttendanceDevice = {
  id: 'd1', serialNumber: 'CQZ7232060112', name: null, isApproved: false, approvedBy: null, approvedAtUtc: null,
  firstSeenAtUtc: '2026-10-20T08:00:00Z', lastSeenAtUtc: '2026-10-20T08:00:00Z', lastAddress: '192.168.1.40', commands: [],
  lastCalledInAtUtc: '2026-10-20T08:00:00Z', callingIn: true, clockAheadMinutes: null,
};

function command(extra: Partial<DeviceCommand> = {}): DeviceCommand {
  return {
    id: 'c1', number: 2, kind: 'ReadUsers', text: 'DATA QUERY USERINFO', queuedBy: 'Omar', queuedAtUtc: '2026-10-20T08:00:00Z', sentAtUtc: null,
    answeredAtUtc: null, returnCode: null, ...extra,
  };
}
const approved: AttendanceDevice = {
  ...waiting, id: 'd2', serialNumber: 'CQZ0000000002', name: 'Kitchen door', isApproved: true, approvedBy: 'Omar', lastCalledInAtUtc: null, callingIn: false,
};

describe('the attendance devices', () => {
  const devices = vi.fn();
  const settings = vi.fn();
  const approveDevice = vi.fn();
  const revokeDevice = vi.fn();
  const saveSettings = vi.fn();
  const readDeviceUsers = vi.fn();
  const readDeviceAttendance = vi.fn();
  const deviceConnection = vi.fn();
  const testDevice = vi.fn();
  const syncDevice = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: HrApi, useValue: { devices, settings, approveDevice, revokeDevice, saveSettings, readDeviceUsers, readDeviceAttendance, deviceConnection, testDevice, syncDevice } },
        { provide: Approvals, useValue: { canApprove } },
        { provide: HrAccess, useValue: { has: () => true, canDecide: () => canApprove() } },
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
    deviceConnection.mockReset().mockReturnValue(of({ devicePort: null, addresses: [] }));
    testDevice.mockReset();
    syncDevice.mockReset();
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
    canApprove.set(true);
    localStorage.clear();
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
    // Whether each calls the till over ADMS, so its punches come as they are made.
    expect(text(rows[0].querySelector('.adms')!)).toBe('wifi Calling in over ADMS: punches come as they are made');
    expect(text(rows[1].querySelector('.adms')!)).toBe('wifi_off Not calling in over ADMS: punches come only with Sync');
    expect(rows[1].querySelector('.adms')!.classList).toContain('bad-text');
  });

  it('says when a device’s clock is ahead of the till’s', async () => {
    devices.mockReturnValue(of([{ ...approved, callingIn: true, clockAheadMinutes: 300 }]));
    const { el } = await render();

    expect([...el.querySelectorAll('tbody tr')[0].querySelectorAll('.adms')].map(text)).toEqual([
      'wifi Calling in over ADMS: punches come as they are made',
      "schedule Its clock is about 5 hours ahead: restart it to set its clock by the till's",
    ]);
  });

  it('gives the device this machine’s network address and the device’s own port, when the till has one', async () => {
    deviceConnection.mockReturnValue(of({ devicePort: 5082, addresses: ['192.168.1.132'] }));
    const { el, fixture } = await render();
    await fixture.whenStable();
    fixture.detectChanges();

    const local = ['localhost', '127.0.0.1'].includes(location.hostname);
    expect([...el.querySelectorAll('.setup dd')].map(text)).toEqual([local ? '192.168.1.132' : location.hostname, '5082']);
    expect(text(el.querySelector('.setup')!)).toContain('Port 5082 is the device’s own');
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

  it('tests the connection to the device at the address it last called from, and says how it went', async () => {
    const { el, page, fixture } = await render();
    fixture.detectChanges();
    const card = el.querySelector('section.link')!;
    expect(text(card.querySelector('h2')!)).toBe('Connection settings إعدادات التوصيل');
    expect([page['deviceAddress'], page['tcpPort'], page['commKey']]).toEqual(['192.168.1.40', 4370, 0]);
    expect([...card.querySelectorAll('button')].map(text)).toEqual(['lan Test connection اختبار التوصيل', 'sync Sync device مزامنة']);

    const message = 'Connected to K40 Pro, serial CQZ7232060112: 2 users and 3 punches on it.';
    testDevice.mockReturnValue(of({ reached: true, message, serialNumber: 'CQZ7232060112', model: 'K40 Pro', firmware: null, users: 2, records: 3 }));
    await page['test']();
    fixture.detectChanges();
    expect(testDevice).toHaveBeenCalledWith('192.168.1.40', 4370, 0);
    expect(notify.info).toHaveBeenCalledWith(message);
    expect(text(card.querySelector('.result')!)).toBe(`check_circle ${message}`);
    expect(savedDeviceLink()).toEqual({ address: '192.168.1.40', port: 4370, commKey: 0 });

    // No answer: said, in red, and the address not remembered.
    const none = 'No answer from 192.168.1.50 on port 4370. Is the device on, and is this machine on the restaurant’s network?';
    page['deviceAddress'] = '192.168.1.50';
    testDevice.mockReturnValue(of({ reached: false, message: none, serialNumber: null, model: null, firmware: null, users: null, records: null }));
    await page['test']();
    fixture.detectChanges();
    expect(notify.error).toHaveBeenCalledWith(null, none);
    expect(card.querySelector('.result')!.classList).toContain('bad-text');
    expect(savedDeviceLink()).toEqual({ address: '192.168.1.40', port: 4370, commKey: 0 });

    // Not an address: nothing to press.
    page['deviceAddress'] = 'kitchen door';
    expect(page['linkable']()).toBe(false);
    page['deviceAddress'] = '192.168.1.300';
    expect(page['linkable']()).toBe(false);
  });

  it('syncs the device: its users and punches read, the new ones said, and the list read again', async () => {
    saveDeviceLink({ address: '192.168.1.106', port: 4370, commKey: 12345 });
    const { el, page, fixture } = await render();
    expect([page['deviceAddress'], page['tcpPort'], page['commKey']]).toEqual(['192.168.1.106', 4370, 12345]);
    devices.mockClear();

    syncDevice.mockReturnValue(of({
      serialNumber: 'CQZ7232060112', deviceName: 'K40 Pro (192.168.1.106)', newDevice: true, users: 2, usersNotOnTheTill: 1, punches: 3, newPunches: 3,
    }));
    await page['sync']();
    fixture.detectChanges();
    expect(syncDevice).toHaveBeenCalledWith('192.168.1.106', 4370, 12345);
    expect(text(el.querySelector('section.link .result')!)).toBe(
      'check_circle K40 Pro (192.168.1.106): 2 users and 3 punches read, 3 new. New to the till: approved, and listed below. ' +
        '1 of its users is not linked to anyone on the till yet. Link it on the Employees tab',
    );
    expect(el.querySelector('section.link .result a')!.getAttribute('href')).toBe('/hr/employees');
    expect(notify.info).toHaveBeenCalledWith('Synced K40 Pro (192.168.1.106): 3 new punches.');
    expect(devices).toHaveBeenCalledTimes(1);

    syncDevice.mockReturnValue(of({
      serialNumber: 'CQZ7232060112', deviceName: 'Kitchen door', newDevice: false, users: 1, usersNotOnTheTill: 0, punches: 1, newPunches: 0,
    }));
    await page['sync']();
    fixture.detectChanges();
    expect(text(el.querySelector('section.link .result')!)).toBe('check_circle Kitchen door: 1 user and 1 punch read, none of them new.');
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
    expect(el.querySelector('section.link')).toBeNull();
    expect([...el.querySelectorAll<HTMLInputElement>('.settings input')].every((i) => i.disabled)).toBe(true);
    expect([...el.querySelectorAll('.settings button')].length).toBe(0);
  });
});
