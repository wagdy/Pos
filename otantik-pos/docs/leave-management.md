# Leave management (نظام أرصدة وموافقات الإجازات)

The kinds of leave, each employee's balance of them a year, and the requests a manager approves or
rejects. Part of HR & Payroll, in the `hr` schema (migration `LeaveAndPerformance`, with
Performance & Disciplinary). Employees ask in
the portal; managers decide on HR's Leaves & Holidays tab.

## Decided

- **Approval marks the days.** An approved paid leave marks each of its days as leave on the
  timesheet: a paid day, not an absence, as the Attendance tab's leave mark is today. An approved
  unpaid leave marks them as unpaid leave, a new kind of day: a day's pay off, never counted as an
  absence.
- **Working days only.** A request takes the days of its dates the employee would work: not a
  weekly day off of theirs, not a public holiday, and only while employed (`LeaveDays`). Sunday to
  Thursday, off on Fridays and Saturdays, is five days; with a holiday on the Tuesday, four.
- **Managers alone decide**, even for an employee holding the Leaves & Holidays tab: approving,
  rejecting and cancelling, a balance's days, and the types.
- **The till starts with three types**: Annual (اعتيادي), 21 days, paid, shared for new hires;
  Sick (مرضي), 7 days, paid; Unpaid (بدون راتب), unpaid.
- **A new hire's share of annual leave**, by the days of the year left from their first day, to
  the half day: joining on 2 July is 183 days of 365, 10.5 of 21. Only a type shared for new hires
  (`ProratedForNewHires`, Annual's); their sick days in full.
- **Balances in half days**; requests in whole working days, so a half is left over.
- **No carry-over**: each year opens afresh on 1 January at the type's days.
- **An approved leave can be cancelled** by a manager from a day on (the business day unless
  said): its days from then are given back to the balance and unmarked; those before stay taken.
  Cancelled from its first day or before, none of it stands (`Cancelled`).

## The database layer (built)

- **`LeaveType`** (`hr.LeaveTypes`): its name (unique), `IsPaid`, and `DefaultDaysPerYear` (0 to
  365). An unpaid type has no days a year (the database checks it too): it has no balance to run
  out, and asking for it is never refused for want of days.
- **`EmployeeLeaveBalance`** (`hr.EmployeeLeaveBalances`): an employee's days of a paid type in a
  year.
  - `TotalAllocatedDays`, in half days: the type's days a year when the year opens (`Open`, a new
    hire's share for a type shared so), a manager's to change after (`Allocate`), never below
    what is taken.
  - `UsedDays`: taken by approved requests (`Use`), never past what is left; given back by
    `Release`.
  - `RemainingDays` is worked out, not kept.
  - One row to an employee, a type and a year (2000 to 2100), and the database refuses more taken
    than given.
- **`LeaveRequest`** (`hr.LeaveRequests`):
  - its employee and type, `StartDate` and `EndDate` (within one year, 90 days at most), and
    `TotalDaysRequested` (its working days, at least one, no more than its dates hold);
  - its `Reason`, who asked (`RequestedBy`, the employee or a manager for them) and when;
  - `Status`: `Pending`, then `Approved` or `Rejected`, once; an approved one `Cancelled` if
    cancelled before it began, or cut short (`CancelledFrom`, `CancelledDays` given back,
    `DaysTaken` left). Its decision is the manager's notes
    (`ManagerNotes`), their till account (`DecidedById`, as `ApprovedById` in the brief; it
    records a rejection's maker too), their name, and when. The database checks that a decided
    request says who decided it, and a pending one does not.
- **Repositories**: the types by name; a balance by employee, type and year, and a year's
  balances; the pending requests (oldest first), an employee's (latest first), and an employee's
  waiting or approved ones meeting given dates, so that a day is asked for once.

- **Unpaid leave on the pay**: a day of approved unpaid leave with no punches is `UnpaidLeave` on
  the timesheet, a day's pay off (`UnpaidLeaveDays`, `UnpaidLeaveDeduction`, apart from absences,
  in the payroll's totals and its Excel); punched in after all, a working day like any other. A
  daily worker is paid only the days they work, so theirs takes nothing. An approved leave's
  days carry their request (`DayMark.LeaveRequestId`): a manager does not mark them by hand, but
  cancels the leave from that day.

## The API (built)

| Endpoint | What it does |
| --- | --- |
| `GET /api/leaves/types` | The types, and whether anything uses each |
| `PUT /api/leaves/types/{id}`, `DELETE …/{id}` | Added or changed (paid or not fixed once used); deleted while nothing uses it: a manager's |
| `GET /api/leaves/balances?year=&employeeId=` | A year's balances, everyone employed in it or one, a row each paid type: given, taken, left, asked for (pending), and whether kept yet |
| `PUT /api/leaves/balances/{employeeId}/{typeId}/{year}` | A balance's days, in half days: a manager's |
| `GET /api/leaves?status=&employeeId=&year=` | The requests: waiting ones, an employee's, or a year's |
| `GET /api/leaves/days?employeeId=&startDate=&endDate=` | The working days the dates would take |
| `POST /api/leaves` | Asked for an employee by someone of HR |
| `POST /api/leaves/{id}/approve`, `…/reject` | A manager's, with notes: approving takes the days and marks them |
| `POST /api/leaves/{id}/cancel` | An approved one, `from` a day (today unless given): a manager's |
| `GET /api/portal/leaves`, `GET …/leaves/days`, `POST …/leaves` | The employee's own: this year's balances and their requests, the days a leave would take, and asking |

Asking is refused over a day already asked for, and for a paid type past what is left less what
is asked already. Approving is refused once the dates hold other working days than were asked (a
holiday added since), and nothing is approved, cancelled or marked in a month whose pay is
finalized. Seen on the Leaves & Holidays tab (HR's Holidays tab); the portal's port answers
`/api/leaves` for an employee holding it.

## The screens (built)

- **The portal's My leaves** (إجازاتي), on `/my-profile`: what is left of each paid type this year
  (and what is asked for, waiting); `+ Request leave`, its type, dates and reason, the working
  days it takes shown first, with what is left of the balance, and refused before sending when
  it is more than is left less what is asked already; and their requests, the latest first,
  with Pending, Approved, Rejected, Cancelled or Cut short and what the manager said. The
  portal's `/api/portal/leaves` also names the types they may ask for (`types`).
- **HR's Leaves & Holidays** (الإجازات والعطلات), the tab that was Holidays (`/hr/leaves`; the old
  `/hr/holidays` opens its holidays), counting the requests waiting on the tab:
  - **Pending approvals**: those waiting, the oldest first, with what is left of each paid one's
    balance, approved at a click or rejected with why; and the year's decided ones, an approved
    one cancelled from a day (the business day, within the leave, unless changed), the days it
    gives back counted first. `+ Request leave for someone` for whoever changes things on the
    tab.
  - **Balances**: everyone employed in a year, a column each paid type, what is left of what is
    given, taken and asked; a manager changes one's days, in whole or half days.
  - **Leave types**: a manager adds, changes, or takes off one not in use.
  - **Public holidays**, as they were.

  Deciding is a manager's alone: an accountant, or an employee holding the tab in the portal,
  sees no Approve, Reject, Cancel or pencil.
- **The payslip** takes unpaid leave off on its own line, its days named, apart from absence; the
  slip says which days they were (`unpaidLeaveDays`). The payroll's deductions count it in.
