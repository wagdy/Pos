import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, tap, throwError } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { ConnectivityService } from '../connectivity/connectivity.service';
import { PortalAuth } from '../portal/portal-auth';

// The APIs of the tabs a manager gives: HR & Payroll's, its leaves and performance, and Custody's.
const employeeApis = ['/api/hr', '/api/leaves', '/api/performance', '/api/custody'];

// The till token on every API call. A 401 on anything but the sign-in itself means the API no
// longer accepts it (the shift ended, or the account changed in the delivery system), so the
// till goes back to the sign-in screen.
//
// The employee portal's calls are apart: they carry the employee's token and never the till's
// (on a till where both are signed in, neither reaches the other's API), and a 401 there signs
// the employee out of the portal, not the till. So do the calls of the tabs a manager gives
// (employeeApis) while an employee is the one acting (no one of the till signed in): the tabs a
// manager gave them, in the portal.
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const portalCall = request.url.startsWith('/api/portal');
  const hrAsEmployee = employeeApis.some((api) => request.url.startsWith(api)) && auth.employeeActing();
  if (portalCall || hrAsEmployee || request.url.startsWith('/api/auth/employee-login')) {
    const portal = inject(PortalAuth);
    const employeeToken = portalCall || hrAsEmployee ? portal.token() : null;
    const asEmployee = employeeToken ? request.clone({ setHeaders: { Authorization: `Bearer ${employeeToken}` } }) : request;
    return next(asEmployee).pipe(
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 401 && (portalCall || hrAsEmployee) && portal.session()) {
          portal.signOut('expired');
        }
        return throwError(() => error);
      }),
    );
  }

  const token = auth.token();
  const isApi = request.url.startsWith('/api/');
  const authorized = token && isApi ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;

  return next(authorized).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        auth.isSignedIn() &&
        !request.url.startsWith('/api/auth/sign-in')
      ) {
        auth.signOut('expired');
      }
      return throwError(() => error);
    }),
  );
};

// Notes whether the till server answered at all, which the banner and the buttons go by.
export const reachabilityInterceptor: HttpInterceptorFn = (request, next) => {
  const connectivity = inject(ConnectivityService);

  return next(request).pipe(
    tap(() => connectivity.markReachable(true)),
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        connectivity.markReachable(error.status !== 0);
      }
      return throwError(() => error);
    }),
  );
};
