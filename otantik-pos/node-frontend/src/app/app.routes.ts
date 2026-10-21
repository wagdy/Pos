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
          // General expenses (OPEX): bought, never stock, so not under Purchases.
          {
            path: 'expense-items',
            loadComponent: () => import('./features/costing/expense-items-page').then((m) => m.ExpenseItemsPage),
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
                path: 'batch',
                loadComponent: () => import('./features/costing/purchase-batch-page').then((m) => m.PurchaseBatchPage),
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
              // Where the spending was before the reports had a tab of their own.
              { path: 'spending', redirectTo: '/costing/reports/spending' },
              // Where the expense items were before they had a tab of their own.
              { path: 'expense-items', redirectTo: '/costing/expense-items' },
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
                path: 'reconciliation',
                loadComponent: () => import('./features/costing/reconciliation-page').then((m) => m.ReconciliationPage),
              },
              {
                path: 'daily-cash',
                loadComponent: () => import('./features/costing/daily-cash-page').then((m) => m.DailyCashPage),
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
              {
                // Employees' advances: who is paid what, so only for whoever sees HR.
                path: 'advances',
                canActivate: [permissionGuard(Permissions.HrView)],
                loadComponent: () => import('./features/costing/advances-page').then((m) => m.AdvancesPage),
              },
              {
                path: 'tax-service',
                loadComponent: () => import('./features/costing/tax-service-page').then((m) => m.TaxServicePage),
              },
            ],
          },
          {
            // Reports (التقارير): where the money went, and the food cost reports.
            path: 'reports',
            loadComponent: () => import('./features/costing/costing-reports-shell').then((m) => m.CostingReportsShell),
            children: [
              { path: '', pathMatch: 'full', redirectTo: 'spending' },
              {
                path: 'spending',
                loadComponent: () => import('./features/costing/spending-page').then((m) => m.SpendingPage),
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
            ],
          },
          // Where the reports were before they had a tab of their own.
          { path: 'theoretical', redirectTo: 'reports/theoretical' },
          { path: 'variance', redirectTo: 'reports/variance' },
          { path: 'kpis', redirectTo: 'reports/kpis' },
          { path: 'break-even', redirectTo: 'reports/break-even' },
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
        // HR & Payroll (الموارد البشرية والرواتب): employees, their attendance and their pay.
        path: 'hr',
        canActivate: [permissionGuard(Permissions.HrView)],
        loadComponent: () => import('./features/hr/hr-shell').then((m) => m.HrShell),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'now' },
          {
            // Who is in now, live from the attendance device.
            path: 'now',
            loadComponent: () => import('./features/hr/now-page').then((m) => m.NowPage),
          },
          {
            path: 'employees',
            loadComponent: () => import('./features/hr/employees-page').then((m) => m.EmployeesPage),
          },
          {
            path: 'departments',
            loadComponent: () => import('./features/hr/departments-page').then((m) => m.DepartmentsPage),
          },
          {
            // ?date=, or ?employee=&month= for one employee's month.
            path: 'attendance',
            loadComponent: () => import('./features/hr/attendance-page').then((m) => m.AttendancePage),
          },
          {
            // ?month= its first day.
            path: 'payroll',
            loadComponent: () => import('./features/hr/payroll-page').then((m) => m.PayrollPage),
          },
          {
            path: 'payroll/:employeeId',
            loadComponent: () => import('./features/hr/payslip-page').then((m) => m.PayslipPage),
          },
          {
            // Suspended days for a manager's punch-out, and the month's administrative penalties.
            path: 'corrections',
            loadComponent: () => import('./features/hr/corrections-page').then((m) => m.CorrectionsPage),
          },
          {
            // A year's public holidays: ?year= not needed, it opens on the business day's.
            path: 'holidays',
            loadComponent: () => import('./features/hr/holidays-page').then((m) => m.HolidaysPage),
          },
          {
            path: 'devices',
            loadComponent: () => import('./features/hr/devices-page').then((m) => m.DevicesPage),
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
