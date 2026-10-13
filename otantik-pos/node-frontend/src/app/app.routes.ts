import { Routes } from '@angular/router';
import { inject } from '@angular/core';
import { AuthService } from './core/auth/auth.service';
import { homePath, ordersGuard, permissionGuard, signedInGuard, signedOutGuard } from './core/auth/guards';
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
      { path: '', pathMatch: 'full', redirectTo: () => homePath(inject(AuthService)) },
      {
        path: 'orders',
        canActivate: [ordersGuard],
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
            loadComponent: () => import('./features/costing/purchases-shell').then((m) => m.PurchasesShell),
            children: [
              { path: '', pathMatch: 'full', redirectTo: 'new' },
              {
                path: 'new',
                loadComponent: () => import('./features/costing/purchases-page').then((m) => m.PurchasesPage),
              },
              {
                path: 'invoices',
                loadComponent: () => import('./features/costing/invoices-page').then((m) => m.InvoicesPage),
              },
              {
                path: 'invoices/:id',
                loadComponent: () => import('./features/costing/invoice-page').then((m) => m.InvoicePage),
              },
              {
                path: 'suppliers',
                loadComponent: () => import('./features/costing/suppliers-page').then((m) => m.SuppliersPage),
              },
              {
                path: 'spending',
                loadComponent: () => import('./features/costing/spending-page').then((m) => m.SpendingPage),
              },
              {
                path: 'expense-items',
                loadComponent: () => import('./features/costing/expense-items-page').then((m) => m.ExpenseItemsPage),
              },
              {
                path: 'categories',
                loadComponent: () => import('./features/costing/purchase-categories-page').then((m) => m.PurchaseCategoriesPage),
              },
            ],
          },
          // Where the invoices were before they were under Purchases.
          { path: 'invoices', pathMatch: 'full', redirectTo: 'purchases/invoices' },
          { path: 'invoices/:id', redirectTo: 'purchases/invoices/:id' },
          {
            path: 'waste',
            loadComponent: () => import('./features/costing/waste-page').then((m) => m.WastePage),
          },
          {
            path: 'counts',
            loadComponent: () => import('./features/costing/counts-page').then((m) => m.CountsPage),
          },
          {
            path: 'counts/:id',
            loadComponent: () => import('./features/costing/count-sheet-page').then((m) => m.CountSheetPage),
          },
          {
            path: 'recipes',
            loadComponent: () => import('./features/costing/recipes-shell').then((m) => m.RecipesShell),
            children: [
              {
                path: '',
                pathMatch: 'full',
                loadComponent: () => import('./features/costing/recipes-page').then((m) => m.RecipesPage),
              },
              {
                // ?targetKind=&catalogItemId=&variantId= to start linked to a menu item.
                path: 'new',
                data: { mode: 'new' },
                loadComponent: () => import('./features/costing/recipe-card-page').then((m) => m.RecipeCardPage),
              },
              {
                path: 'recipe/:recipeId',
                data: { mode: 'view' },
                loadComponent: () => import('./features/costing/recipe-card-page').then((m) => m.RecipeCardPage),
              },
              {
                path: 'recipe/:recipeId/edit',
                data: { mode: 'edit' },
                loadComponent: () => import('./features/costing/recipe-card-page').then((m) => m.RecipeCardPage),
              },
              {
                path: 'prep',
                loadComponent: () => import('./features/costing/prep-items-page').then((m) => m.PrepItemsPage),
              },
              {
                // 'new' for one not saved yet.
                path: 'prep/:id',
                loadComponent: () => import('./features/costing/prep-item-page').then((m) => m.PrepItemPage),
              },
              {
                path: 'categories',
                loadComponent: () => import('./features/costing/recipe-categories-page').then((m) => m.RecipeCategoriesPage),
              },
              {
                // ?prepItemId= to start with one chosen.
                path: 'production',
                loadComponent: () => import('./features/costing/production-page').then((m) => m.ProductionPage),
              },
              {
                // A menu item's card by its catalog ids (?variantId= for a size), as linked before
                // recipes had ids of their own: sent on to the recipe it sells by.
                path: ':kind/:id',
                data: { mode: 'menu' },
                loadComponent: () => import('./features/costing/recipe-card-page').then((m) => m.RecipeCardPage),
              },
            ],
          },
          {
            // Financials (الماليات): the safe, its funding, expense vouchers, and what suppliers are owed and paid.
            path: 'financials',
            loadComponent: () => import('./features/costing/financials-shell').then((m) => m.FinancialsShell),
            children: [
              { path: '', pathMatch: 'full', redirectTo: 'safe' },
              {
                path: 'safe',
                loadComponent: () => import('./features/costing/safe-page').then((m) => m.SafePage),
              },
              {
                path: 'funding',
                loadComponent: () => import('./features/costing/funding-page').then((m) => m.FundingPage),
              },
              {
                path: 'expenses',
                loadComponent: () => import('./features/costing/expense-vouchers-page').then((m) => m.ExpenseVouchersPage),
              },
              {
                // ?supplier= for one supplier's statement of account.
                path: 'suppliers',
                loadComponent: () => import('./features/costing/supplier-debts-page').then((m) => m.SupplierDebtsPage),
              },
              {
                path: 'payments',
                loadComponent: () => import('./features/costing/supplier-payments-page').then((m) => m.SupplierPaymentsPage),
              },
            ],
          },
          {
            path: 'theoretical',
            loadComponent: () => import('./features/costing/theoretical-page').then((m) => m.TheoreticalPage),
          },
          {
            path: 'variance',
            loadComponent: () => import('./features/costing/variance-page').then((m) => m.VariancePage),
          },
          {
            path: 'kpis',
            loadComponent: () => import('./features/costing/kpis-page').then((m) => m.KpisPage),
          },
          {
            path: 'break-even',
            loadComponent: () => import('./features/costing/break-even-page').then((m) => m.BreakEvenPage),
          },
          {
            path: 'settings',
            loadComponent: () => import('./features/costing/settings-page').then((m) => m.SettingsPage),
          },
          {
            // A manager's: what others have asked for, waiting for a decision.
            path: 'approvals',
            canActivate: [permissionGuard(Permissions.ApprovalDecide)],
            loadComponent: () => import('./features/costing/approvals-page').then((m) => m.ApprovalsPage),
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
