import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Me } from '../api/models';
import { captainMe, cashierMe } from '../../../testing/fixtures';
import { AuthService } from './auth.service';
import { CanDirective } from './can.directive';
import { MoneyPermissions, Permissions } from './permissions';

@Component({
  imports: [CanDirective],
  template: `
    <button *appCan="P.OrderSendToKitchen" id="send">Send to kitchen</button>
    <button *appCan="P.OrderCheckout" id="checkout">Checkout</button>
    <button *appCan="P.VoidAfterPayment" id="refund">Refund</button>
    <button *appCan="P.ReceiptPrintFinal" id="receipt">Print receipt</button>
    <span *appCan="money" id="any-money">money</span>
  `,
})
class Till {
  protected readonly P = Permissions;
  protected readonly money = MoneyPermissions;
}

describe('role-based buttons', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // The sign-in response is answered with a manager's rights on purpose: the till must ignore it
  // and believe only GET /api/auth/me.
  async function signInAs(me: Me): Promise<void> {
    const signedIn = auth.signIn(me.id, '2468');
    http.expectOne('/api/auth/sign-in').flush({
      token: 'token-' + me.id,
      expiresAtUtc: '2026-10-04T00:00:00Z',
      staff: { ...cashierMe, role: 'Manager', permissions: [...cashierMe.permissions, 'staff.manage'] },
    });
    await new Promise((resolve) => setTimeout(resolve));
    const meRequest = http.expectOne('/api/auth/me');
    expect(meRequest.request.headers.has('Authorization')).toBe(false); // the interceptor is not in this test bed
    meRequest.flush(me);
    await signedIn;
  }

  async function render(): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(Till);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('takes the role from /api/auth/me, never from the sign-in response', async () => {
    await signInAs(captainMe);

    expect(auth.me()?.role).toBe('CaptainOrder');
    expect(auth.can(Permissions.StaffManage)).toBe(false);
    expect(auth.can(Permissions.OrderCheckout)).toBe(false);
  });

  // Requirement: a captain who reaches the POS screen has no payment, receipt or void controls.
  it('shows a captain order taking and nothing that touches money', async () => {
    await signInAs(captainMe);
    const till = await render();

    expect(till.querySelector('#send')).not.toBeNull();
    expect(till.querySelector('#checkout')).toBeNull();
    expect(till.querySelector('#refund')).toBeNull();
    expect(till.querySelector('#receipt')).toBeNull();
    expect(till.querySelector('#any-money')).toBeNull();
  });

  it('gives a cashier the whole till', async () => {
    await signInAs(cashierMe);
    const till = await render();

    for (const id of ['send', 'checkout', 'refund', 'receipt', 'any-money']) {
      expect(till.querySelector('#' + id), id).not.toBeNull();
    }
  });
});
