# HR, attendance and payroll

The till's HR module: who is on the payroll, their punches on the ZKTeco K40 Pro, their days,
and their monthly pay. This page is the design. Phase 1 (the domain, its tables and this plan)
is built. Phases 2 and 3 are below.

## The rules

- **Employees** (`hr.Employees`): name, job title, base monthly salary, shift start, weekly
  days off, their user number on the device, an optional link to a till account, hired/left
  dates. They are not the till's staff accounts: most never sign in to a till.
- **The shift is nine hours** for everyone, from the employee's shift start, past midnight if
  need be.
- **Rates come from a 30-day month**, whatever the month's length:
  - a day = salary ÷ 30;
  - an hour = a day ÷ 9;
  - a minute = an hour ÷ 60.
- **A day's punches** are taken from six hours before the shift starts to the same time the
  next day, so an evening shift that ends after midnight stays one day.
  - They are paired in order (in, out, in, out). The In/Out key pressed on the device is kept
    but not relied on.
  - Two scans within 3 minutes count as one (a setting).
- **Measured against the shift:**
  - **late**: from the shift's start to the first punch, unless within the grace (a setting,
    0 by default). Past the grace, it counts from the shift's start;
  - **leaving early**: from the last punch to the shift's end;
  - **away**: time punched out between pairs while the shift ran;
  - **overtime**: time worked after the shift's end. Coming in early is not overtime.
  - So late, early, away and the time worked within the shift add up to nine hours.
- **Days:**
  - no punches on a working day is an **absence**, unless a manager marked it as leave or a
    day off;
  - weekly days off are per employee;
  - punches on a day off or on leave are hours worked, with nothing late or overtime;
  - an odd number of punches is a **missing punch**: the month's slip cannot be finalized
    until a manager enters the missing one (a manual punch, with a reason) or ignores the
    extra one.
- **Advances (سلفيات)** leave the safe or a day's takings at once, as a safe entry of kind
  `EmployeeAdvance` that names the employee and the payroll month it is deducted in.
  - **Not an expense:** the employee owes it until their payslip deducts it. The wages are
    counted once, at payroll.
  - It counts in the month it is paid, or the next open one when that month's payroll is
    finalized.
- **Incentives (حوافز) and leave allowances (بدل إجازات)** are adjustments to one month's pay,
  entered by a manager (or, later, made by the till), voided rather than changed.
- **The payslip (مسير الرواتب)**, per employee per month:

  ```
  Net = base salary + incentives + leave allowances + overtime pay
        − advances − last month's shortfall − lateness − absence
  ```

  - Overtime and lateness (late + early + away) are at the minute rate; an absence costs a
    day's pay.
  - A month started or left part way pays its share of the calendar month.
  - When deductions are more than the pay, nothing is paid and the rest carries to next month.
  - What the month's work cost is `WagesCost`: the pay less what was deducted for time not
    worked. Advances settle a debt, so they are not a cost.
  - A slip is a draft, worked out again as the month changes, until it is finalized. After
    that it does not change.

## The domain (Phase 1, built)

`otantik-pos/backend/src/OtantikPos.Hr.Domain`, its own module referencing only the building
blocks. Tables are in the `hr` schema; migration `HrFoundation`.

| Type | Table | What it is |
| --- | --- | --- |
| `Employee` | `hr.Employees` | Someone on the payroll, with their rates |
| `AttendanceDevice` | `hr.AttendanceDevices` | A K40 Pro known by its serial number, approved by a manager |
| `AttendanceLog` | `hr.AttendanceLogs` | One raw punch, from a device or by hand; ignored, never removed |
| `DayMark` | `hr.DayMarks` | A manager's "leave" or "day off" for one day |
| `Timesheet`, `DailyRecord` | `hr.DailyRecords` | A day's hours, lateness and overtime, worked out from its punches |
| `PayrollAdjustment` | `hr.PayrollAdjustments` | An incentive or a leave allowance for a month |
| `PayrollCalculator`, `PayrollSlip` | `hr.PayrollSlips` | A month's payslip and every figure in it |
| `HrSettings` | `hr.Settings` | The grace, and how close two scans are to be one |
| `SafeTransaction` (treasury) | `inventory.SafeTransactions` | New kinds `EmployeeAdvance` and `SalaryPayment`, with `EmployeeId` and `PayrollMonth` |

Punch times are the device's wall clock, stored as `timestamp without time zone`. A punch sent
twice is kept once: it is unique by device, user number and time.

## Receiving the device's punches (Phase 2): ADMS

The K40 Pro pushes to us over HTTP with ZKTeco's ADMS ("iclock") protocol. Nothing Windows-only
(no `zkemkeeper.dll`, no COM) is needed. The till's API answers it at these paths, outside
`/api` because the device's paths are fixed:

| Request | When | Our answer |
| --- | --- | --- |
| `GET /iclock/cdata?SN=…&options=all` | The device starts or reconnects | Its options, text/plain: `GET OPTION FROM: <SN>`, `ATTLOGStamp=<last stamp>`, `OPERLOGStamp=9999`, `ErrorDelay=30`, `Delay=10`, `TransTimes=00:00;14:05`, `TransInterval=1`, `TransFlag=TransData AttLog OpLog`, `Realtime=1`, `Encrypt=None` |
| `POST /iclock/cdata?SN=…&table=ATTLOG&Stamp=…` | New punches | Each line `PIN⇥YYYY-MM-DD HH:MM:SS⇥status⇥verify⇥…` becomes an `AttendanceLog`. Answer `OK`, then keep the stamp |
| `POST /iclock/cdata?SN=…&table=OPERLOG` | Operations, users enrolled | `OK`. `USER PIN=…⇥Name=…` lines can suggest links to employees. Fingerprint templates (`FP …`) are never stored |
| `GET /iclock/getrequest?SN=…` | Every few seconds | `OK`, or a queued command `C:<id>:<command>` (for example fetching a period's punches again, or putting an employee's number and name on the device) |
| `POST /iclock/devicecmd?SN=…` | A command's result | `OK`; the result is recorded |

The exact option set and command syntax vary by firmware. They are to be checked against our
unit with a capture of its first requests before they are fixed.

**What happens to a punch:**
1. The line is parsed strictly. A malformed one is logged and skipped, never the whole batch.
2. The user number is looked up to an employee. A number nobody has is kept, waiting, and
   counted once linked.
3. The punch is inserted unless already there (the unique index).
4. The device's stamp and last-seen time are updated.
5. The days the punches fall in are worked out again (`Timesheet.Evaluate`, saved as
   `DailyRecord`), and the tills are told through the existing hub.
6. Absences show once a day is over: a background job works out each day just ended, and the
   payroll works its month out again before it is finalized.

**Security.** The device names itself only by its serial number, over plain HTTP, with no key:
- The `/iclock` endpoints are anonymous, as the device cannot sign in. The check is the serial
  number: a device first seen is recorded as waiting and refused (403), and nothing it sends
  is taken until a manager approves it by name. The device keeps its own log and sends
  everything after its stamp once approved, so nothing is lost.
- Requests are size-limited and rate-limited per serial number. A punch far from the current
  time (a device whose clock was reset) is kept but flagged.
- **Address checks inside Docker are unreliable.** On macOS, Docker Desktop's port forwarding
  makes every connection appear to come from its own gateway, not the device's real address.
  The serial number and the network's own separation are what protect it.
- Anyone on the till's network who reads the serial off the device's label could post punches.
  Keep guest Wi-Fi off that network, and the till's port off the internet. Punches are raw
  evidence a manager reviews, with manual corrections recorded by name and reason.

**Network: the till in Docker on a Mac.**
- The compose file publishes the till on `TILL_PORT` (5080) on all of the Mac's interfaces, and
  Docker Desktop forwards it into the container. The device reaches the same port and app as
  the tablets do: there is no second port.
- Give the Mac a fixed address (a DHCP reservation on the router), and allow Docker's incoming
  connections in the macOS firewall.
- On the device: Main Menu → COMM. → Cloud Server Setting.
  - Server mode ADMS, Enable Domain Name off.
  - Server Address = the Mac's address, Server Port = `TILL_PORT`.
  - Proxy off, HTTPS off.
  - Menu names vary by firmware.
- If the Mac sleeps or the till is down, the device holds its punches and sends them when it
  reconnects.
- **Plan B**, if our unit's firmware has no Cloud Server Setting: pull from the device over its
  TCP port 4370 (ZKTeco's binary protocol) from a small client in the till. No Windows DLL is
  needed for that either.

## Phase 2: the backend's use cases

- The ADMS endpoints above, device approval, and simulated-device tests (a script that posts
  ATTLOG lines).
- Commands and queries:
  - employees;
  - manual punches and ignoring one;
  - day marks;
  - the month's timesheet;
  - adjustments;
  - working out, finalizing and paying slips.
- An accountant's go through the existing approvals, as purchases do.
- New permissions for viewing HR and managing it.
- **Advances from the treasury:** a voucher kind that requires an employee and goes to their
  open month.
  - The daily cash and reconciliation screens count advances and salaries paid out of a day's
    takings (they count invoices and supplier payments today, `TreasuryService`).
  - The safe's ledger names the employee.
- **KPIs:** wages from finalized slips (`WagesCost`). The old "سلفيات" expense item is retired
  once advances are entered as advances; what is already entered stays as it is.
- **To decide:** whether a day off worked makes a leave allowance by itself (the adjustment has
  `Source = Automatic` and `ForDay` for it), and public holidays.

## Phase 3: the screens

Employees; devices (approve); attendance (the month's timesheet per employee, with missing
punches to fix); advances in Financials; the payroll run (draft, finalize, pay); a printable
payslip.
