import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { homePath, ordersGuard, permissionGuard } from './guards';
import { Permissions } from './permissions';

describe('the costing section guard', () => {
  function enter(can: boolean): boolean | UrlTree {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, can: () => can } }] });
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

// An accountant works the back office: no orders, costing yes, changes and deletes only asked for.
describe('an accountant at the till', () => {
  const accountant = new Set<string>([Permissions.InventoryView, Permissions.InventoryManage, Permissions.CostingView]);

  function run(guard: () => unknown): string | boolean {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, can: (p: string) => accountant.has(p) } }] });
    const result = TestBed.runInInjectionContext(guard) as boolean | UrlTree;
    return typeof result === 'boolean' ? result : TestBed.inject(Router).serializeUrl(result);
  }

  it('lands on the costing, and is sent there from the open orders', () => {
    expect(homePath({ can: (p: string) => accountant.has(p) } as AuthService)).toBe('/costing');
    expect(run(() => ordersGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe('/costing');
  });

  it('is kept out of the approvals, which are a manager\'s', () => {
    expect(run(() => permissionGuard(Permissions.ApprovalDecide)({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe('/costing');
  });

  it('leaves whoever takes orders on the open orders', () => {
    expect(homePath({ can: () => true } as unknown as AuthService)).toBe('/orders');
  });
});
