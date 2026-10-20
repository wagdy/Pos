import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AttendanceDevice, HrApi } from '../../core/hr/hr.api';
import { NotifyService } from '../../core/ui/notify.service';
import { Approvals } from '../costing/approvals';
import { DeviceNameDialog, DevicesPage } from './devices-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

const waiting: AttendanceDevice = {
  id: 'd1', serialNumber: 'CQZ7232060112', name: null, isApproved: false, approvedBy: null, approvedAtUtc: null,
  firstSeenAtUtc: '2026-10-20T08:00:00Z', lastSeenAtUtc: '2026-10-20T08:00:00Z', lastAddress: '192.168.1.40',
};
const approved: AttendanceDevice = { ...waiting, id: 'd2', serialNumber: 'CQZ0000000002', name: 'Kitchen door', isApproved: true, approvedBy: 'Omar' };

describe('the attendance devices', () => {
  const devices = vi.fn();
  const settings = vi.fn();
  const approveDevice = vi.fn();
  const revokeDevice = vi.fn();
  const saveSettings = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const canApprove = signal(true);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: HrApi, useValue: { devices, settings, approveDevice, revokeDevice, saveSettings } },
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
