import { TestBed } from '@angular/core/testing';
import { Route, provideRouter } from '@angular/router';
import { routes } from '../../app.routes';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { FinancialsShell } from '../costing/financials-shell';
import { HrShell } from './hr-shell';

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
  it('gives HR & Payroll a section of its own, guarded, opening on the employees', () => {
    const hr = find(routes, 'hr')!;
    expect(hr.canActivate?.length).toBe(1);
    expect(hr.children!.map((r) => [r.path, r.redirectTo ?? (r.loadComponent ? 'page' : undefined)])).toEqual([
      ['', 'employees'],
      ['employees', 'page'],
      ['devices', 'page'],
    ]);
    // Advances in Financials are HR's too: guarded the same way.
    expect(find(find(routes, 'financials')!.children, 'advances')!.canActivate?.length).toBe(1);
  });

  it('has a tab each for the employees and the attendance devices', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(HrShell);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el.querySelector('h1')!)).toBe('HR & Payroll الموارد البشرية والرواتب');
    expect([...el.querySelectorAll('nav a')].map((a) => a.getAttribute('href'))).toEqual(['/employees', '/devices']);
  });

  it('shows the Advances tab in Financials only to whoever sees HR', async () => {
    async function tabs(hr: boolean): Promise<string[]> {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideRouter([]), { provide: AuthService, useValue: { can: (p: string) => hr || p !== Permissions.HrView } }],
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
