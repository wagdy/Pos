import { AttendanceDevice } from './hr.api';

// Where the till reaches an attendance device on its own port (ZKTeco's, 4370): its address,
// port and comm key, as last used in this browser. Remembered when a test or a sync reached it,
// so the Devices tab opens on them and the Employees tab's Sync reads the same device.
export interface DeviceLink {
  address: string;
  port: number;
  commKey: number;
}

export const ZkPort = 4370;

const Key = 'otantik.hr.device-link';

export function isIPv4(address: string): boolean {
  const parts = address.trim().split('.');
  return parts.length === 4 && parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) <= 255);
}

// "about 5 hours", "about an hour", "25 minutes": how far a device's clock runs ahead.
export function aheadBy(minutes: number): string {
  return minutes >= 90 ? `about ${Math.round(minutes / 60)} hours` : minutes >= 50 ? 'about an hour' : `${minutes} minutes`;
}

// The address a device last called from, when it is one the till can reach it at.
export function addressOf(device: AttendanceDevice): string | null {
  const address = device.lastAddress?.replace(/^::ffff:/, '') ?? '';
  return isIPv4(address) ? address : null;
}

// Kept in the browser when it can be: a private window may not keep it.
export function savedDeviceLink(): DeviceLink | null {
  try {
    const saved = JSON.parse(localStorage.getItem(Key) ?? 'null') as Partial<DeviceLink> | null;
    if (!saved || typeof saved.address !== 'string' || !isIPv4(saved.address)) {
      return null;
    }
    return { address: saved.address, port: Number(saved.port) || ZkPort, commKey: Number(saved.commKey) || 0 };
  } catch {
    return null;
  }
}

export function saveDeviceLink(link: DeviceLink): void {
  try {
    localStorage.setItem(Key, JSON.stringify(link));
  } catch {
    // Not remembered: given again next time.
  }
}
