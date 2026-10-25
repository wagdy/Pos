import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Me } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Permission } from '../../core/auth/permissions';
import { ConnectivityService } from '../../core/connectivity/connectivity.service';
import { MenuService } from '../../core/menu/menu.service';
import { OrdersStore } from '../../core/orders/orders.store';
import { TillHubService } from '../../core/realtime/till-hub.service';
import { NotifyService } from '../../core/ui/notify.service';
import { captainMe, cashierMe } from '../../../testing/fixtures';
import { Shell } from './shell';

// The shared RolePermissions table's accountant: the back office and HR, no orders.
const accountantMe: Me = {
  id: 'staff-accountant',
  fullName: 'Mona',
  role: 'Accountant',
  permissions: ['costing.view', 'inventory.manage', 'inventory.view', 'hr.view', 'hr.manage'],
};

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the till top bar', () => {
  async function render(me: Me): Promise<HTMLElement> {
    const can = (permission: Permission) => me.permissions.includes(permission);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { me: signal(me), can, canAny: (permissions: readonly Permission[]) => permissions.some(can) } },
        {
          provide: ConnectivityService,
          useValue: { nodeOnline: signal(true), cloudOnline: signal(true), cloudNotConfigured: signal(false), cloud: signal(null), printers: signal([]) },
        },
        { provide: TillHubService, useValue: { start: vi.fn().mockResolvedValue(undefined), stop: vi.fn().mockResolvedValue(undefined) } },
        { provide: OrdersStore, useValue: { clear: vi.fn() } },
        { provide: MenuService, useValue: { load: vi.fn().mockResolvedValue(undefined) } },
        { provide: NotifyService, useValue: { attention: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(Shell);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const nav = (el: HTMLElement) => [...el.querySelectorAll('nav a')].map((a) => a.getAttribute('aria-label'));

  it('tells a captain they take orders only', async () => {
    const el = await render(captainMe);

    expect(text(el.querySelector('.badge')!)).toBe('Order taking only');
    expect(nav(el)).toEqual(['Open orders']);
  });

  it('says nothing of the kind to a cashier, who sees no HR', async () => {
    const el = await render(cashierMe);

    expect(el.querySelector('.badge')).toBeNull();
    expect(nav(el)).not.toContain('HR & Payroll');
  });

  // The accountant takes no orders at all, so "order taking only" would be wrong twice over.
  it('says nothing of the kind to the accountant, who sees the back office alone', async () => {
    const el = await render(accountantMe);

    expect(el.querySelector('.badge')).toBeNull();
    expect(nav(el)).toEqual(['Costing', 'HR & Payroll', 'Custody & Assets']);
    expect(text(el.querySelector('.user-name')!)).toBe('Mona');
  });
});
