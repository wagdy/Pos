import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { permissionGuard } from './guards';
import { Permissions } from './permissions';

describe('the costing section guard', () => {
  function enter(can: boolean): boolean | UrlTree {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { can: () => can } }] });
    return TestBed.runInInjectionContext(
      () => permissionGuard(Permissions.CostingView)({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot) as boolean | UrlTree,
    );
  }

  it('lets a manager in', () => {
    expect(enter(true)).toBe(true);
  });

  it('sends a cashier who typed the address back to the open orders', () => {
    const result = enter(false);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/orders');
  });
});
