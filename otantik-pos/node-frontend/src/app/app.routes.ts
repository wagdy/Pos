import { Routes } from '@angular/router';
import { signedInGuard, signedOutGuard } from './core/auth/guards';

export const routes: Routes = [
  {
    path: 'sign-in',
    canActivate: [signedOutGuard],
    loadComponent: () => import('./features/sign-in/sign-in-page').then((m) => m.SignInPage),
  },
  {
    path: '',
    canActivate: [signedInGuard],
    loadComponent: () => import('./features/shell/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'orders' },
      {
        path: 'orders',
        loadComponent: () => import('./features/orders/open-orders-page').then((m) => m.OpenOrdersPage),
      },
      {
        path: 'reports',
        loadComponent: () => import('./features/reports/reports-page').then((m) => m.ReportsPage),
      },
      {
        path: 'staff',
        loadComponent: () => import('./features/staff/staff-page').then((m) => m.StaffPage),
      },
      {
        path: 'orders/:orderId',
        loadComponent: () => import('./features/orders/order-page').then((m) => m.OrderPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
