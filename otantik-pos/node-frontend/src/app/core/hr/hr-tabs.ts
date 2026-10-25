// The tabs of HR & Payroll, as the API names them (صلاحيات الموظفين): what a manager gives an
// employee, who works them in the portal, or an accountant, on the till. A manager has them all.
export type HrTab = 'Now' | 'Employees' | 'Departments' | 'Attendance' | 'Corrections' | 'Payroll' | 'Advances' | 'Holidays' | 'Devices';

export interface HrTabInfo {
  tab: HrTab;
  // Its address under /hr.
  path: string;
  en: string;
  ar: string;
}

// In the order they are shown.
export const hrTabs: readonly HrTabInfo[] = [
  { tab: 'Now', path: 'now', en: 'Now', ar: 'الآن' },
  { tab: 'Employees', path: 'employees', en: 'Employees', ar: 'الموظفين' },
  { tab: 'Departments', path: 'departments', en: 'Departments', ar: 'الأقسام' },
  { tab: 'Attendance', path: 'attendance', en: 'Attendance', ar: 'الحضور والانصراف' },
  { tab: 'Corrections', path: 'corrections', en: 'Corrections', ar: 'مراجعة وجزاءات' },
  { tab: 'Payroll', path: 'payroll', en: 'Payroll', ar: 'مسير الرواتب' },
  { tab: 'Advances', path: 'advances', en: 'Advances', ar: 'السلفيات' },
  { tab: 'Holidays', path: 'holidays', en: 'Holidays', ar: 'العطلات الرسمية' },
  { tab: 'Devices', path: 'devices', en: 'Attendance devices', ar: 'أجهزة البصمة' },
];

export const allHrTabs: readonly HrTab[] = hrTabs.map((t) => t.tab);

// The tabs a list names, in the order shown; anything that is not a tab left out.
export function hrTabsOf(names: readonly unknown[] | null | undefined): HrTab[] {
  const named = new Set(names ?? []);
  return allHrTabs.filter((t) => named.has(t));
}
