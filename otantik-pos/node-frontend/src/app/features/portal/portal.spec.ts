import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { authInterceptor } from '../../core/http/interceptors';
import { PortalAuth } from '../../core/portal/portal-auth';
import { PortalApi, PortalProfile } from '../../core/portal/portal.api';
import { PayrollSlip } from '../../core/hr/hr.api';
import { MyProfilePage } from './my-profile-page';
import { PortalLoginPage } from './portal-login-page';
import { PortalHrFrame } from './portal-hr-frame';
import { HrAccess } from '../../core/hr/hr-access';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
// Element by element: the template leaves no space between them.
const cell = (el: Element): string =>
  [...el.childNodes]
    .map((n) => (n.nodeType === Node.ELEMENT_NODE ? cell(n as Element) : n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').trim() : ''))
    .filter(Boolean)
    .join(' ');

describe('the portal’s sign-in, kept apart from the till’s', () => {
  const staffSignOut = vi.fn();

  function setUp() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, token: signal('staff-token'), isSignedIn: () => true, signOut: staffSignOut } },
      ],
    });
    return { http: TestBed.inject(HttpClient), backend: TestBed.inject(HttpTestingController), portal: TestBed.inject(PortalAuth) };
  }

  beforeEach(() => {
    localStorage.clear();
    staffSignOut.mockReset();
  });

  it('signs in with a mobile and PIN, keeps the session, and sends its token to the portal alone', async () => {
    const { http, backend, portal } = setUp();
    expect(portal.isSignedIn()).toBe(false);

    const signing = portal.signIn('01012345678', '2468');
    const login = backend.expectOne('/api/auth/employee-login');
    expect(login.request.body).toEqual({ mobileNumber: '01012345678', pinCode: '2468' });
    // Never the till's token on the portal's sign-in.
    expect(login.request.headers.has('Authorization')).toBe(false);
    login.flush({ token: 'employee-token', expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(), employeeId: 'hassan', name: 'Hassan', role: 'Employee' });
    await signing;
    expect(portal.isSignedIn()).toBe(true);
    expect(JSON.parse(localStorage.getItem('otantik-portal.session')!)).toMatchObject({ employeeId: 'hassan', name: 'Hassan' });

    http.get('/api/portal/me').subscribe();
    expect(backend.expectOne('/api/portal/me').request.headers.get('Authorization')).toBe('Bearer employee-token');
    http.get('/api/hr/employees').subscribe();
    expect(backend.expectOne('/api/hr/employees').request.headers.get('Authorization')).toBe('Bearer staff-token');
    backend.verify();
  });

  it('signs the employee out of the portal on a 401 there, and leaves the till signed in', async () => {
    localStorage.setItem('otantik-portal.session', JSON.stringify({ token: 'old', expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(), employeeId: 'hassan', name: 'Hassan' }));
    const { http, backend, portal } = setUp();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    expect(portal.isSignedIn()).toBe(true);

    http.get('/api/portal/me').subscribe({ error: () => undefined });
    backend.expectOne('/api/portal/me').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(portal.isSignedIn()).toBe(false);
    expect(localStorage.getItem('otantik-portal.session')).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/portal'], { queryParams: { reason: 'expired' } });
    expect(staffSignOut).not.toHaveBeenCalled();
  });

  it('counts a session that has run out as signed out', () => {
    localStorage.setItem('otantik-portal.session', JSON.stringify({ token: 'old', expiresAtUtc: '2020-01-01T00:00:00Z', employeeId: 'hassan', name: 'Hassan' }));
    const { portal } = setUp();
    expect(portal.isSignedIn()).toBe(false);
  });
});

describe('the portal’s sign-in screen', () => {
  const signIn = vi.fn();

  async function render(reason?: string) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: PortalAuth, useValue: { signIn } }],
    });
    const fixture = TestBed.createComponent(PortalLoginPage);
    if (reason) {
      fixture.componentRef.setInput('reason', reason);
    }
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.componentRef.changeDetectorRef.markForCheck();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    await refresh();
    return { el, refresh, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => signIn.mockReset());

  it('signs in and opens their profile; a refusal says why and clears the PIN', async () => {
    const { el, page, refresh } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    expect(text(el.querySelector('h1')!)).toBe('My profile ملفي');
    // Nothing of the till here.
    expect(el.querySelector('nav, mat-toolbar')).toBeNull();

    // Nothing typed yet: said so, nothing sent.
    await page['signIn']();
    await refresh();
    expect(text(el.querySelector('.error')!)).toBe('Enter your mobile number and your PIN.');
    expect(signIn).not.toHaveBeenCalled();
    expect((el.querySelector('button[type=submit]') as HTMLButtonElement).disabled).toBe(false);

    signIn.mockRejectedValueOnce(new HttpErrorResponse({ status: 401, error: { detail: 'Wrong mobile number or PIN.' } }));
    Object.assign(page, { mobile: ' 01012345678 ', pin: '1111' });
    await page['signIn']();
    await refresh();
    expect(signIn).toHaveBeenLastCalledWith('01012345678', '1111');
    expect(text(el.querySelector('.error')!)).toBe('Wrong mobile number or PIN.');
    expect(page['pin']).toBe('');
    expect(navigate).not.toHaveBeenCalled();

    signIn.mockResolvedValueOnce(undefined);
    page['pin'] = '2468';
    await page['signIn']();
    expect(navigate).toHaveBeenCalledWith(['/my-profile']);
  });

  it('says they were signed out', async () => {
    const { el } = await render('expired');
    expect(text(el.querySelector('.notice')!)).toBe('You were signed out. Sign in again. سجّل الدخول مرة أخرى');
  });
});

describe('an employee’s own profile in the portal', () => {
  const me = vi.fn();
  const months = vi.fn();
  const payslip = vi.fn();
  const custody = vi.fn();
  const idPhoto = vi.fn();
  const signOut = vi.fn();
  const hrTabs = signal<string[]>([]);

  const profile: PortalProfile = {
    id: 'hassan', name: 'Hassan Mahmoud Ali', jobTitle: 'Chef', department: 'Kitchen', employmentType: 'Monthly', hiredOn: '2026-01-05',
    nationalId: '29001010123456', mobileNumber: '01012345678', idPhoto: null, shiftStartsAt: '10:00:00', shiftEndsAt: '19:00:00', daysOff: ['Friday'],
  };

  function slip(month: string, status: PayrollSlip['status']): PayrollSlip {
    return {
      employee: { id: 'hassan', name: 'Hassan Mahmoud Ali', jobTitle: 'Chef', hourlyRate: 20, dailyRate: 180 }, month, status,
      figures: {
        monthlySalary: 5_400, daysInMonth: 31, daysEmployed: 31, baseSalary: 5_400, incentives: 0, leaveAllowances: 0, overtimeMinutes: 0, overtimePay: 0,
        shortMinutes: 0, latenessPenalty: 0, absentDays: 0, absencePenalty: 0, advances: 0, broughtForward: 0, daysWorked: 20, daysOffWorked: 0,
        missingPunchDays: 0, openDays: 0, daysOffWorkedPay: 0, administrativePenaltyHours: 0, administrativePenalty: 0, holidaysWorked: 0, holidaysWorkedPay: 0,
        employmentType: 'Monthly', dailyWage: 0, gross: 5_400, deductions: 0, net: 5_400, netPayable: 5_400, carriedForward: 0, wagesCost: 5_400,
      },
      salaryPaid: 0, salaryOwed: 0, calculatedAtUtc: null, finalizedAtUtc: null, finalizedBy: null, adjustments: [], advances: [], salaries: [],
    } as unknown as PayrollSlip;
  }

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: PortalApi, useValue: { me, months, payslip, custody, idPhoto } },
        { provide: PortalAuth, useValue: { session: signal({ name: 'Hassan Mahmoud Ali' }), hrTabs, signOut } },
      ],
    });
    const fixture = TestBed.createComponent(MyProfilePage);
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    };
    await refresh();
    return { el, refresh, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    me.mockReset().mockReturnValue(of(profile));
    months.mockReset().mockReturnValue(of(['2026-10-01', '2026-09-01']));
    payslip.mockReset().mockImplementation((month: string) => of(slip(month, month === '2026-10-01' ? 'Draft' : 'Finalized')));
    custody.mockReset().mockReturnValue(of([
      { name: 'Dinner plates', category: 'Tableware', quantity: 37 },
      { name: 'Knife set', category: null, quantity: 1 },
    ]));
    signOut.mockReset();
    hrTabs.set([]);
  });

  it('shows their details, this month’s draft payslip and their custody, with nothing of the till', async () => {
    const { el } = await render();

    expect(cell(el.querySelector('.bar')!)).toBe('badge My profile ملفي Hassan Mahmoud Ali logout Sign out');
    expect(el.querySelector('app-shell, nav')).toBeNull();
    const details = Object.fromEntries([...el.querySelectorAll('.details dl > div')].map((d) => [cell(d.querySelector('dt')!), cell(d.querySelector('dd')!)]));
    expect(details).toMatchObject({
      'Full name الاسم بالكامل': 'Hassan Mahmoud Ali',
      'National ID رقم البطاقة': '29001010123456',
      'Job الوظيفة': 'Chef · Kitchen',
      'Shift الوردية': '10:00–19:00 · off Fri',
    });
    expect(payslip).toHaveBeenCalledWith('2026-10-01');
    expect(text(el.querySelector('.draft-note')!)).toContain('A draft');
    expect(el.querySelector('app-payslip-sheet .total')).not.toBeNull();
    // Read-only: no button on the slip.
    expect(el.querySelectorAll('app-payslip-sheet button').length).toBe(0);
    // Name, category and how many: nothing of what it cost.
    expect(cell(el.querySelector('.custody thead')!)).toBe('Asset الصنف Category الفئة Quantity العدد');
    expect([...el.querySelectorAll('.custody .held')].map((r) => [...r.querySelectorAll('td')].map(cell))).toEqual([
      ['Dinner plates', 'Tableware', '37'],
      ['Knife set', '—', '1'],
    ]);
    expect(text(el.querySelector('.custody')!)).not.toContain('L.E');
  });

  it('says when nothing is in their custody', async () => {
    custody.mockReturnValue(of([]));
    const { el } = await render();
    expect(el.querySelector('.custody table')).toBeNull();
    expect(text(el.querySelector('.custody p')!)).toBe('Nothing is in your custody.');
  });

  it('reads a month before when chosen, and says when there is none', async () => {
    const { el, page, refresh } = await render();

    page['pick']('2026-09-01');
    await refresh();
    expect(payslip).toHaveBeenLastCalledWith('2026-09-01');
    expect(el.querySelector('.draft-note')).toBeNull();

    payslip.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 404 })));
    page['pick']('2026-08-01');
    await refresh();
    expect(text(el.querySelector('.no-slip')!)).toBe('No payslip for this month.');
  });

  it('signs out', async () => {
    const { el } = await render();
    (el.querySelector('.bar button') as HTMLElement).click();
    expect(signOut).toHaveBeenCalled();
  });

  it('opens the HR tabs a manager gave them, when they have some', async () => {
    const { el, refresh } = await render();
    expect(el.querySelector('.bar a.hr-link')).toBeNull();
    hrTabs.set(['Now', 'Attendance']);
    await refresh();
    expect(el.querySelector('.bar a.hr-link')!.getAttribute('href')).toBe('/hr');
  });
});

describe('the portal around an employee’s HR tabs', () => {
  it('has the portal’s bar, back to their profile, says their changes wait for a manager, and signs out', async () => {
    const signOut = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: PortalAuth, useValue: { session: signal({ name: 'Hassan' }), signOut } },
        { provide: HrAccess, useValue: { any: () => true, has: (t: string) => t === 'Custody' } },
      ],
    });
    const fixture = TestBed.createComponent(PortalHrFrame);
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect([...el.querySelectorAll('.bar nav a')].map((a) => a.getAttribute('href'))).toEqual(['/my-profile', '/hr', '/custody']);
    expect(text(el.querySelector('.who')!)).toBe('Hassan');
    expect(text(el.querySelector('.note')!)).toContain('goes to a manager to approve; the rest of what you change here is made at once');
    expect(el.querySelector('app-shell, mat-toolbar')).toBeNull();
    (el.querySelector('.bar button') as HTMLElement).click();
    expect(signOut).toHaveBeenCalled();
  });
});
