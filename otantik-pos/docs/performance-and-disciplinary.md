# Performance & Disciplinary (تقييم الأداء والإنذارات)

Each employee's administrative record: evaluations of their work, and disciplinary actions, a
penalty deduction among them reaching their pay through Corrections. Part of HR & Payroll, in the
`hr` schema (migration `LeaveAndPerformance`, with Leave management). A manager records both; employees see their
own in the portal, read-only.

## Decided

- **KPIs are a list a manager sets**: Punctuality, Hygiene, Service and so on. Each evaluation
  scores the ones it takes from 1 to 5, and its total is their average as a percent.
- **A penalty deduction is an administrative penalty in money.** Corrections' penalties, hours on
  a day until now, can be an amount as well. A disciplinary action's deduction makes one of its
  own on the action's day, listed on Corrections with a link back to it, and deducted from that
  month's pay. It is refused once that month's pay is finalized (the API's check, as for a
  manager's own penalties).
- **Managers alone** record evaluations, issue disciplinary actions and withdraw them.
- **Withdrawing**: an action issued by mistake is withdrawn by a manager, with why, while its
  month's pay is not finalized. It is kept, marked withdrawn, counts for nothing in the record, and
  its penalty is taken off Corrections and the pay. The employee's portal leaves it out.

## The database layer (built)

- **`PerformanceKpi`** (`hr.PerformanceKpis`): a name (unique) and whether it is still in use.
  One that evaluations have scored is retired, not deleted: they keep its name.
- **`PerformanceEvaluation`** (`hr.PerformanceEvaluations`):
  - the employee, the day, the reviewer (`ReviewerId`, their till account, and their name);
  - the scores (`hr.EvaluationScores`, the brief's `KpiDetails`): a KPI each, scored once, 1 to
    5, with the KPI's name as it was;
  - `TotalScore`, the scores' average as a percent to one decimal place, kept as worked out then
    (5, 4 and 3 is 80.0);
  - notes.

  A retired KPI is not scored.
- **`DisciplinaryAction`** (`hr.DisciplinaryActions`):
  - the employee, the day, the issuer (`IssuerId`, their till account, and their name), the
    violation as written (negligence, absence, behaviour), and notes;
  - `SeverityLevel`, in the order they escalate: `VerbalWarning`, `FirstWritten`,
    `SecondWritten`, `PenaltyDeduction`, `Termination`;
  - `PenaltyAmount`: a penalty deduction has one, more than nothing and to the piastre; no other
    level has one (the database checks it too).

  A termination is recorded here; ending their employment is the Employees tab's leaving, as
  ever.
- **The penalty's link**: `DisciplinaryAction.Penalty()` makes the deduction's
  `AdministrativePenalty`:
  - an amount (`Amount`), no hours, on the action's day, reason "Penalty deduction: Absence";
  - `DisciplinaryActionId` linking it to the action.

  `hr.AdministrativePenalties` now holds hours or an amount, one or the other. A manager's own
  penalty is still one a day; an action's is its own beside it, one to an action.
- **Through to the pay**: a day's penalties are summed. The day carries `PenaltyHours` and
  `PenaltyAmount` (kept on `hr.DailyRecords` too). The payslip deducts the hours at the hourly
  rate plus the amounts as they are (`AdministrativePenalty`), and shows the amounts apart
  (`AdministrativePenaltyAmounts`). Corrections' list of the month's penalties shows an amount's
  penalty with its action's id.
- **Escalation** (`DisciplinaryRecord`): an employee's actions counted by level, their warnings
  in all, and the level that comes next: verbal, then first written, then second written, then a
  penalty deduction.
- **Repositories**: the KPIs; an employee's evaluations and actions, the latest first; whether a
  KPI has been scored; and a disciplinary action's penalty.

## The API (built)

| Endpoint | What it does |
| --- | --- |
| `GET /api/performance/kpis` | The KPIs, retired ones too, and whether each has been scored |
| `PUT /api/performance/kpis/{id}`, `POST …/{id}/retire`, `…/restore`, `DELETE …/{id}` | Added or renamed, retired or brought back; deleted while unscored: a manager's |
| `GET /api/performance/employees/{id}` | Their evaluations and disciplinary actions, the latest first, and their record: each level's count, their warnings, and the level that comes next |
| `POST /api/performance/employees/{id}/evaluations` | An evaluation, dated today or before while they were employed: a manager's |
| `POST /api/performance/employees/{id}/disciplinary` | An action; a penalty deduction's penalty made with it, in a month whose pay is open: a manager's |
| `POST /api/performance/disciplinary/{id}/withdraw` | Withdrawn with its reason, its penalty taken off: a manager's |
| `GET /api/portal/performance` | The employee's own, withdrawn ones left out |

Seen by whoever holds the Employees tab (an accountant too); each action says whether its
penalty is still to come off a payslip not finalized (`PenaltyPending`).

## The screens (built)

- **The profile's Performance & Disciplinary** (التقييم والإنذارات), on `/hr/employees/{id}`: the
  record (each level's count, their warnings, the next level), then the evaluations and actions,
  the latest first. A manager has `KPIs`, `+ New evaluation` (each KPI in use scored 1 to 5 or
  left out, the total shown as the till works it out) and `+ Issue warning` (the next level to
  start with, a penalty deduction's amount to the piastre), and withdraws an action, with why:
  not once a penalty deduction's month is finalized. Anyone else holding the Employees tab sees
  it all, withdrawn ones marked, with no buttons. The payslip beside it is read again when an
  action is issued or withdrawn.
- **The KPIs**, a manager's, in their own dialog: added, renamed, retired once scored (deleted
  otherwise), brought back.
- **The portal's My record** (سجلي), read-only: their evaluations and actions, a penalty
  deduction said to be still to come off a payslip or taken off; withdrawn ones are never sent.
- **The payslip** shows the administrative penalties' hours apart from the disciplinary penalty
  deductions, each of these dated with its reason (`penalties` on the slip, the action's id on
  its own).
- **Corrections** lists a penalty deduction beside the hours, as money, marked Disciplinary, with
  a link to the action on the profile (`#performance`), where it is withdrawn: no pencil for it
  there.
