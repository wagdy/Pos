import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';
import { Permission, Permissions } from './permissions';

// Where someone lands: the open orders for whoever takes them, the costing for an accountant,
// who works the back office and takes no orders.
export function homePath(auth: AuthService): string {
  return !auth.can(Permissions.OrderViewOwn) && auth.can(Permissions.CostingView) ? '/costing' : '/orders';
}

// Signed in, as /api/auth/me confirmed at start-up or sign-in.
export const signedInGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isSignedIn() ? true : inject(Router).createUrlTree(['/sign-in']);
};

export const signedOutGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isSignedIn() ? inject(Router).createUrlTree([homePath(auth)]) : true;
};

// The open orders, for whoever takes orders; anyone else is sent to their own section.
export const ordersGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const home = homePath(auth);
  return home === '/orders' ? true : inject(Router).createUrlTree([home]);
};

// A section only some roles have: its link is hidden from the rest, and this keeps a typed
// address from opening it. The API refuses the data either way.
export const permissionGuard =
  (permission: Permission): CanActivateFn =>
  () =>
    inject(AuthService).can(permission) ? true : inject(Router).createUrlTree([homePath(inject(AuthService))]);
