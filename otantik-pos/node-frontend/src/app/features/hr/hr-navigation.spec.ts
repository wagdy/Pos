import { TestBed } from '@angular/core/testing';
import { Route, provideRouter } from '@angular/router';
import { routes } from '../../app.routes';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { signal } from '@angular/core';
import { FinancialsShell } from '../costing/financials-shell';
import { HrShell } from './hr-shell';
import { CorrectionsCount } from './corrections-count';
import { hrGuard, portalHrMatch } from '../../core/hr/hr-access';

// The route of that path, anywhere under these.
function find(list: Route[] | undefined, path: string): Route | undefined {
  for (const route of list ?? []) {
    if (route.path === path) {
      return route;
    }
    const inside = find(route.children, path);
    if (inside) {
      return inside;
    }
  }
  return undefined;
}

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the HR navigation', () => {
  it('gives HR & Payroll a section of its own, each tab guarded by its own, and the same tabs in the portal', () => {
    const shell = routes.find((r) => r.path === '' && r.children)!;
    const till = shell.children!.find((r) => r.path === 'hr')!;
    expect(till.canActivate).toEqual([hrGuard]);
    expect(till.children!.map((r) => [r.path, typeof r.redirectTo === 'function' ? 'first held' : r.loadComponent ? 'page' : undefined, r.canActivate?.length ?? 0])).toEqual([
      ['', 'first held', 0],
      ['now', 'page', 1],
      ['employees', 'page', 1],
      ['employees/:employeeId', 'page', 1],
      ['departments', 'page', 1],
      ['attendance', 'page', 1],
      ['payroll', 'page', 1],
      ['payroll/:employeeId', 'page', 1],
      ['advances', 'page', 1],
      ['corrections', 'page', 1],
      ['holidays', 'page', 1],
      ['devices', 'page', 1],
      ['permissions', 'page', 1],
    ]);
    // The portal's /hr, before the till's: only while an employee is the one signed in, around the same tabs.
    const portal = routes.find((r) => r.path === 'hr')!;
    expect(routes.indexOf(portal)).toBeLessThan(routes.indexOf(shell));
    expect(portal.canMatch).toEqual([portalHrMatch]);
    expect(portal.children![0].children).toBe(till.children);
    // Advances in Financials are HR's too: guarded the same way.
    expect(find(find(routes, 'financials')!.children, 'advances')!.canActivate?.length).toBe(1);
  });

  async function shellOf(hrTabs: string[], manager: boolean) {
    const count = signal(0);
    const refresh = vi.fn();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CorrectionsCount, useValue: { count, refresh } },
        { provide: AuthService, useValue: { employeeActing: () => false, me: signal({ hrTabs }), can: (p: string) => manager || p !== Permissions.ApprovalDecide } },
      ],
    });
    const fixture = TestBed.createComponent(HrShell);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, fixture, count, refresh };
  }

  it('has a tab each for now, the employees, departments, attendance, corrections, payroll, advances, holidays and devices, counting what waits, and Permissions for a manager', async () => {
    const all = ['Now', 'Employees', 'Departments', 'Attendance', 'Corrections', 'Payroll', 'Advances', 'Holidays', 'Devices'];
    const { el, fixture, count, refresh } = await shellOf(all, true);

    expect(text(el.querySelector('h1')!)).toBe('HR & Payroll الموارد البشرية والرواتب');
    expect([...el.querySelectorAll('nav a')].map((a) => a.getAttribute('href'))).toEqual([
      '/now', '/employees', '/departments', '/attendance', '/corrections', '/payroll', '/advances', '/holidays', '/devices', '/permissions',
    ]);
    expect(text(el.querySelectorAll('nav a')[9])).toBe('Permissionsصلاحيات الموظفين');
    expect(refresh).toHaveBeenCalled();
    expect(el.querySelector('.count')).toBeNull();

    count.set(2);
    fixture.detectChanges();
    expect(el.querySelectorAll('nav a')[4].querySelector('.count')!.getAttribute('aria-label')).toBe('2 waiting');
    expect(text(el.querySelector('.count')!)).toBe('2');
  });

  it('shows an accountant only the tabs a manager ticked for them, and no Permissions', async () => {
    const { el } = await shellOf(['Payroll', 'Advances'], false);
    expect([...el.querySelectorAll('nav a')].map((a) => a.getAttribute('href'))).toEqual(['/payroll', '/advances']);
  });

  it('shows the Advances tab in Financials only to whoever sees HR', async () => {
    async function tabs(hr: boolean): Promise<string[]> {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideRouter([]), { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, can: (p: string) => hr || p !== Permissions.HrView } }],
      });
      const fixture = TestBed.createComponent(FinancialsShell);
      await fixture.whenStable();
      fixture.detectChanges();
      return [...(fixture.nativeElement as HTMLElement).querySelectorAll('nav a')].map((a) => a.getAttribute('href')!);
    }

    expect(await tabs(true)).toContain('/advances');
    expect(await tabs(false)).not.toContain('/advances');
  });
});
