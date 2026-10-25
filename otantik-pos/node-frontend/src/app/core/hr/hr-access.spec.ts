import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { Permissions } from '../auth/permissions';
import { authInterceptor } from '../http/interceptors';
import { PortalAuth } from '../portal/portal-auth';
import { HrAccess, hrGiveGuard, hrGuard, hrTabGuard, portalHrMatch } from './hr-access';

// A token as the API makes them, its payload what matters here.
function jwt(payload: object): string {
  const json = new TextEncoder().encode(JSON.stringify(payload));
  const b64 = btoa(String.fromCharCode(...json)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `header.${b64}.signature`;
}

function portalSession(hrTabs: unknown): void {
  localStorage.setItem(
    'otantik-portal.session',
    JSON.stringify({ token: jwt({ sub: 'hassan', name: 'حسن', role: 'Employee', hr_tabs: hrTabs }), expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(), employeeId: 'hassan', name: 'حسن' }),
  );
}

const all = ['Now', 'Employees', 'Departments', 'Attendance', 'Corrections', 'Payroll', 'Advances', 'Holidays', 'Devices'];

describe('who holds which HR tab', () => {
  beforeEach(() => localStorage.clear());

  function staff(me: object | null, can: (p: string) => boolean) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: { employeeActing: () => false, me: signal(me), can } }],
    });
    return TestBed.inject(HrAccess);
  }

  const guard = (fn: ReturnType<typeof hrTabGuard>) =>
    TestBed.runInInjectionContext(() => fn({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) as true | UrlTree;
  const url = (result: true | UrlTree) => (result === true ? true : TestBed.inject(Router).serializeUrl(result));

  it('gives a manager every tab and the Permissions tab', () => {
    const access = staff({ hrTabs: all }, () => true);
    expect([...access.tabs()]).toEqual(all);
    expect(access.canGive()).toBe(true);
    expect(access.first()).toBe('now');
    expect(url(guard(hrGiveGuard))).toBe(true);
  });

  it('gives an accountant the tabs a manager ticked, and sends a typed address to the first one they hold', () => {
    const access = staff({ hrTabs: ['Payroll', 'Advances'] }, (p) => p !== Permissions.ApprovalDecide && p !== Permissions.OrderViewOwn);
    expect([...access.tabs()]).toEqual(['Payroll', 'Advances']);
    expect(access.canGive()).toBe(false);
    expect(url(guard(hrTabGuard('Payroll')))).toBe(true);
    expect(url(guard(hrTabGuard('Attendance')))).toBe('/hr/payroll');
    expect(url(guard(hrGiveGuard))).toBe('/hr/payroll');
    expect(url(guard(hrGuard))).toBe(true);
  });

  it('keeps whoever holds no tab out of HR, and reads an older API as every tab for whoever sees HR', () => {
    const cashier = staff({ hrTabs: [] }, (p) => p === Permissions.OrderViewOwn);
    expect(cashier.any()).toBe(false);
    expect(url(guard(hrGuard))).toBe('/orders');
    expect(url(guard(hrTabGuard('Now')))).toBe('/orders');

    TestBed.resetTestingModule();
    const older = staff({ permissions: [Permissions.HrView] }, (p) => p === Permissions.HrView);
    expect([...older.tabs()]).toEqual(all);
  });
});

describe('an employee working their HR tabs in the portal', () => {
  beforeEach(() => localStorage.clear());

  function setUp() {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    return { auth: TestBed.inject(AuthService), portal: TestBed.inject(PortalAuth), access: TestBed.inject(HrAccess) };
  }

  it('reads their tabs from the token, a list or one name, anything not a tab left out', () => {
    portalSession(['Attendance', 'Now', 'Kitchen']);
    expect(setUp().portal.hrTabs()).toEqual(['Now', 'Attendance']);
    TestBed.resetTestingModule();
    portalSession('Payroll');
    expect(setUp().portal.hrTabs()).toEqual(['Payroll']);
    TestBed.resetTestingModule();
    portalSession(undefined);
    expect(setUp().portal.hrTabs()).toEqual([]);
  });

  it('acts with no one of the till signed in: their tabs, an accountant’s rights in them, and nothing to give', () => {
    portalSession(['Now', 'Attendance']);
    const { auth, access } = setUp();
    expect(auth.employeeActing()).toBe(true);
    expect(TestBed.runInInjectionContext(() => portalHrMatch({}, []))).toBe(true);
    expect([...access.tabs()]).toEqual(['Now', 'Attendance']);
    expect(access.canGive()).toBe(false);
    expect(access.outside()).toBe('/my-profile');
    expect([Permissions.HrView, Permissions.HrManage, Permissions.ApprovalDecide, Permissions.InventoryManage, Permissions.OrderCreate].map((p) => auth.can(p))).toEqual([
      true, true, false, false, false,
    ]);
  });

  it('sends their token to HR and the portal, never to the till, and a 401 from HR signs them out of the portal', () => {
    portalSession(['Now']);
    const { portal } = setUp();
    const http = TestBed.inject(HttpClient);
    const backend = TestBed.inject(HttpTestingController);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const bearer = `Bearer ${portal.token()}`;

    http.get('/api/hr/attendance/now').subscribe();
    expect(backend.expectOne('/api/hr/attendance/now').request.headers.get('Authorization')).toBe(bearer);
    http.get('/api/auth/staff').subscribe();
    expect(backend.expectOne('/api/auth/staff').request.headers.has('Authorization')).toBe(false);

    http.get('/api/hr/employees').subscribe({ error: () => undefined });
    backend.expectOne('/api/hr/employees').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(portal.isSignedIn()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/portal'], { queryParams: { reason: 'expired' } });
    backend.verify();
  });

  it('is not the one acting while someone of the till is signed in here', () => {
    portalSession(['Now']);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { employeeActing: () => false, me: signal({ hrTabs: all }), can: () => true } },
      ],
    });
    expect(TestBed.runInInjectionContext(() => portalHrMatch({}, []))).toBe(false);
    expect(TestBed.inject(HrAccess).canGive()).toBe(true);
  });
});
