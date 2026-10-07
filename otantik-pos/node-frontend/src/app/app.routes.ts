import { Routes } from '@angular/router';
import { permissionGuard, signedInGuard, signedOutGuard } from './core/auth/guards';
import { Permissions } from './core/auth/permissions';

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
        path: 'costing',
        canActivate: [permissionGuard(Permissions.CostingView)],
        loadComponent: () => import('./features/costing/costing-page').then((m) => m.CostingPage),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'materials' },
          {
            path: 'materials',
            loadComponent: () => import('./features/costing/materials-page').then((m) => m.MaterialsPage),
          },
          {
            path: 'purchases',
            loadComponent: () => import('./features/costing/purchases-page').then((m) => m.PurchasesPage),
          },
          {
            path: 'recipes',
            loadComponent: () => import('./features/costing/recipes-page').then((m) => m.RecipesPage),
          },
          {
            // ?variantId= for one size of a dish.
            path: 'recipes/:kind/:id',
            loadComponent: () => import('./features/costing/recipe-card-page').then((m) => m.RecipeCardPage),
          },
          {
            path: 'theoretical',
            loadComponent: () => import('./features/costing/theoretical-page').then((m) => m.TheoreticalPage),
          },
          {
            path: 'settings',
            loadComponent: () => import('./features/costing/settings-page').then((m) => m.SettingsPage),
          },
        ],
      },
      {
        path: 'orders/:orderId',
        loadComponent: () => import('./features/orders/order-page').then((m) => m.OrderPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
