# HR, attendance and payroll

The till's HR module: who is on the payroll, their punches on the ZKTeco K40 Pro, their days,
and their monthly pay. This page is the design.

- **Phase 1, built:** the domain, its tables and this plan.
- **Phase 2, built:** every endpoint below, the ADMS receiver, and the screens:
  - HR & Payroll's Employees, Attendance, Corrections, Payroll, Holidays and Attendance devices
    tabs, with a payslip page;
  - advances in Financials and the daily batch.

## The rules

- **Employees** (`hr.Employees`): name, job title, base monthly salary, shift start, weekly
  days off, their user number on the device, an optional link to a till account, hired/left
  dates. They are not the till's staff accounts: most never sign in to a till.
- **Their profile** (ملف الموظف, migration `EmployeeProfiles`):
  - Their name is their full name (الاسم بالكامل), as on their ID card: no field apart.
  - National ID (رقم البطاقة): an Egyptian card's 14 digits, or a passport's letters and digits;
    one employee to a card (a unique index). Mobile (رقم الموبايل): 7 to 15 digits, a leading +
    from abroad. Both kept as digits however they are typed: Arabic digits, spaces and dashes
    are fine. Optional, and changed as the rest is: an accountant's change goes to a manager.
  - The ID card's photo or scan (صورة البطاقة): a JPEG, PNG, WebP or PDF of up to 5 MB, checked by
    its first bytes. Its bytes are kept in `hr.EmployeeIdPhotos` (one row an employee, so in the
    backups), apart from the employee, whose row says what it is and who uploaded it when.
    Whoever manages HR uploads, replaces or takes it off, at once: it is not money. Whoever sees
    HR sees it, served with `nosniff`.
  - Nobody is deleted: deactivating someone is their leaving, paid to their last day with every
    record kept, and they can come back.
- **Departments** (الأقسام, `hr.Departments`, migration `EmploymentTypesAndDepartments`): every
  employee is in one. General is made with the table, the employees already entered go to it,
  and it is renamed but never deleted. Another is deleted once its employees, those who left
  too, are moved to one that stays, its roles going with them (one the other department has by
  the same name becomes theirs there). A manager's to add, rename and delete.
- **Job roles** (الوظائف, `hr.JobRoles`, migration `JobRoles`): each department has its roles,
  one name to a department, and an employee holds one of their own department's, titled by its
  name. The job titles typed before roles were became their departments' roles. A title given
  with no role (the form's new one, or a request asked before roles were) becomes the
  department's role of that name. A role is renamed for everyone holding it, and deleted once
  they take another of the department's.
- **How they are paid** (نوع العمالة): monthly (عمالة شهرية), at a salary, or daily (عمالة يومية), at
  a wage for each day worked.
  - A daily worker's base pay is their wage for each day worked on a working day or a holiday.
    A day not worked, whatever it is, earns nothing and costs nothing: nothing is absent for
    them. A day off worked is paid as anyone's: a day's wage for half a shift or more, its
    hours for less. A holiday worked earns its allowance on top, and lateness and overtime count
    against their shift at their hourly rate (the wage ÷ 9), as anyone's.
- **The shift is nine hours**, from the employee's shift start, past midnight if need be.
- **Flexible hours** (an employee with no shift start, migration `FlexibleShiftsAndDeviceSync`):
  nothing is late, early or away for them, and the hours past nine worked in a day are overtime.
  Their day runs as the till's business day does, from 04:00 to 04:00, so a night past midnight
  is one day. A working day with no punches is still an absence, and their salary is paid as
  anyone's.
- **Rates come from a 30-day month**, whatever the month's length:
  - a day = salary ÷ 30 (a daily worker's: their wage);
  - an hour = a day ÷ 9;
  - a minute = an hour ÷ 60.
- **A day's punches** are taken from six hours before the shift starts to the same time the
  next day, so an evening shift that ends after midnight stays one day.
  - They are paired in order (in, out, in, out). The In/Out key pressed on the device is kept
    but not relied on.
  - Two scans within 3 minutes count as one (a setting).
- **Measured against the shift:**
  - **late**: from the shift's start (the employee's scheduled start) to the first punch,
    unless within the grace (a setting, 0 by default). Past the grace, it counts from the
    shift's start, however many hours are worked;
  - **leaving early**: from the last punch to the shift's end;
  - **away**: time punched out between pairs while the shift ran;
  - **overtime**: time worked after the shift's end, once nine hours are worked. Coming in
    early is not overtime, and neither is staying late to make up for coming in late (in at
    10:00 for 08:00, out at 19:00: two hours late, no overtime).
  - So late, early, away and the time worked within the shift add up to nine hours.
- **Days:**
  - no punches on a working day is an **absence**, unless a manager marked it as leave or a
    day off;
  - weekly days off are per employee;
  - punches on a day off or on leave are hours worked, with nothing late or overtime, and earn
    a **leave allowance** by themselves: a day's pay for half a shift (4½ hours) or more, the
    hours at the hourly rate for less;
  - a weekly day off worked in exchange for another is marked a **working day** (a swap): a
    normal shift, late and overtime counted, no allowance; the day taken instead is marked a
    day off;
  - a punch-in left **18 hours** without its punch-out is an abandoned shift: the day is
    **Suspended**, pending a manager, and its hours are not worked out. A manager resolves it by
    entering the punch-out (after the punch-in, within the day), or by ignoring the stray punch.
    The month's slip cannot be finalized while a day is suspended;
  - a manager can put an **administrative penalty** (جزاء إداري) on a day: hours, with the
    reason, one a day. It is deducted on the payslip at the hourly rate;
  - a **public holiday** (عطلة رسمية, `hr.PublicHolidays`, migration `PublicHolidays`) is
    everyone's paid day off, entered by a manager with its name and days (one to ten: the Eids
    run several). Not worked, it is a holiday whatever the day would have been, never an
    absence. Worked, it is measured as the day would have been (against the shift on a working
    day, with lateness, leaving early and overtime; nothing late on a weekly day off) and earns a
    **holiday allowance** by itself, as a day off worked does: a day's pay for half a shift or
    more, the hours for less. On a weekly day off it is paid once, as a holiday. Its lateness
    never takes more than its allowance gives: worked for less than half a shift, it counts no
    more short minutes than it worked, so coming in on a holiday never pays less than staying
    home. A month whose pay is finalized for anyone keeps its holidays.
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
  Net = base salary + incentives + leave allowances + days off worked + holidays worked
        + overtime pay − advances − last month's shortfall − lateness − absence
        − administrative penalties
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
| `Timesheet`, `DailyRecord` | `hr.DailyRecords` | A day's hours, lateness and overtime, worked out from its punches; kept as a finalized slip counted it |
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
| `POST /iclock/cdata?SN=…&table=ATTLOG&Stamp=…` | New punches, or all those it kept while the till was away | Each line `PIN⇥YYYY-MM-DD HH:MM:SS⇥status⇥verify⇥…` becomes an `AttendanceLog`, any number of them, saved a thousand at a time (up to 16 MB a request). Answer `OK:<lines>`, then keep the stamp |
| `POST /iclock/cdata?SN=…&table=USERINFO` (or `OPERLOG`) | Its users, when asked | `PIN=…⇥Name=…` (or `USER PIN=…⇥Name=…`) lines keep the user's number and name (`DeviceUser`), nothing else: no password or card. Fingerprint and face templates (`FP …`, `FACE …`, `BIODATA …`) are never stored |
| `GET /iclock/getrequest?SN=…` | Every few seconds | `OK`, or the oldest command a manager left for it, `C:<n>:DATA QUERY USERINFO` or `C:<n>:DATA QUERY ATTLOG StartTime=…⇥EndTime=…` (`DeviceCommand`), handed over once |
| `POST /iclock/devicecmd?SN=…` | A command's result | `ID=<n>&Return=<code>&CMD=…`: the command answered, 0 being done; `OK` |

The exact option set and command syntax vary by firmware. They are to be checked against our
unit with a capture of its first requests before they are fixed. A command the firmware does not
take comes back with a non-zero code, shown on the Devices tab.

**Syncing what the device held before the till** (built):
- Its attendance: on approval the till's options say `ATTLOGStamp=None`, so the device sends
  its whole stored log by itself. A manager can also have it read again from one day to another
  (`DATA QUERY ATTLOG`); what is kept already is kept once. Punches from before an employee's
  hire date count for nothing.
- Its users: a manager has it list them (`DATA QUERY USERINFO`). Each user nobody on the till has
  the number of is listed on the Employees tab, with the punches waiting under it, to add as an
  employee (name and number filled in) or link to one; their punches are theirs from then.

**Offline, and late punches** (built): the device keeps its punches while the laptop is off the
network and sends them all when it is back. The days are worked out from the punches' own
times, so past days come out as if the punches had arrived on time, and a punch sent twice is
kept once. One that arrives for a month whose pay is finalized is kept, the slip stays as it was,
and Corrections lists it until a manager marks it seen to (settling in a later month's pay what
it changes).

**What happens to a punch:**
1. The line is parsed strictly. A malformed one is logged and skipped, never the whole batch.
2. The user number is looked up to an employee. A number nobody has is kept, waiting, and
   counted once linked.
3. The punch is inserted unless already there (the unique index).
4. The device's stamp and last-seen time are updated.
5. The days are worked out from the punches whenever they are read (`Timesheet.Evaluate`).
   So a punch arriving, one entered or ignored, a day marked, a shift moved or the grace changed
   shows at once, with nothing stored to fall out of step. A month's days are kept
   (`DailyRecord`) when its slip is finalized: what the pay was worked out from.
6. A day is absent once it is over (its window has closed) with no punches on a working day.

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
- **The device's own port** (`Attendance:DevicePort`): when set, the till answers nothing but the
  device's `/iclock` paths on it, so its screens stay on its machine. `./start.sh` runs the
  development till this way: the screens on `localhost:5081`, the device on port 5082 of the
  Mac's network address. `GET /api/hr/devices/connection` gives the port and the addresses, and
  the Devices tab shows them. Unset, as in the installed till, every port answers everything.
- On the device: Main Menu → COMM. → Cloud Server Setting.
  - Server mode ADMS, Enable Domain Name off.
  - Server Address = the Mac's address, Server Port = `TILL_PORT`.
  - Proxy off, HTTPS off.
  - Menu names vary by firmware.
- If the Mac sleeps or the till is down, the device holds its punches and sends them when it
  reconnects.

**Calling in.** A device calling over ADMS asks for commands every few seconds, and its last
call is kept apart from when the till last read it (`LastCalledInAtUtc`, migration
`DeviceCallIns`). Quiet for three minutes, it is not reaching the till: its punches come only when
someone presses Sync. A device added by a Sync has never called in until it is pointed at the
till.

**Its clock.** A device sets its clock from the time of the till's answer (UTC) and a time zone:
left to its own, +8 as ZKTeco ships it, its punches run hours ahead, and count on the wrong day
(the K40 did, five hours fast, on 2026-10-22). The till sends the restaurant's zone as it starts
(`TimeZone=` in the options, whole hours, or minutes for a zone on the half hour), so it sets its
clock right each time it starts; a change of daylight saving reaches it at its next start. A
device whose last punch (ignored ones aside) came stamped more than ten minutes ahead of its
arrival is said to have a fast clock, on Now and the Devices tab, until a punch comes right or
those punches are ignored. A slow clock cannot be told from punches kept while it was away.

## Reaching the device on its own port (built): TCP 4370

Beside ADMS, the till can reach the device itself over ZKTeco's binary protocol on TCP port
4370. Nothing Windows-only is used: `ZkClient` (in `Node.Infrastructure/Hr/ZkTeco`) follows the
open-source pyzk client packet for packet.
- **Its frames:** `50 50 82 7D`, the length, then command, checksum, session and reply number.
- **A comm key**, when one is set on the device (COMM. → Comm Key), is sent as pyzk makes it.
- **Large answers:** the user table and the attendance log come through the device's buffer, in
  chunks of up to 0xFFC0 bytes.
- **Record sizes:** 72-byte users and 40-byte punches on the K40, or the older 28 and 16.
- **What is kept:** a user's number and name only, never passwords, cards or fingerprints.

Two buttons on the Devices tab (Connection settings · إعدادات التوصيل, a manager's), given the
device's IP address, port 4370 unless said otherwise, and its comm key (0 for none):
- **Test connection** connects, then reads the serial number, model, firmware and how many
  users and punches it holds. When it fails, it says why: nothing listening on the port, no
  answer, a closed connection, or a refused comm key.
- **Sync device** reads the users and the whole log at once and keeps them as ADMS does:
  - punches are kept once each, so a sync after ADMS, or ADMS after a sync, keeps nothing twice;
  - users are listed on Employees for linking;
  - a device read for the first time is approved by it, as a manager gave its address and asked;
  - a clash with an ADMS upload of the same punches is read again.
- **Only local addresses** are accepted: IPv4 in the private ranges, link-local or loopback.
  The till does not reach further than the restaurant's network.
- **Timeouts:** each answer is waited on for 5 seconds on a test and 20 on a sync, so a device
  that stops answering is let go.

ADMS goes on as before; this is for a device whose Cloud Server Setting is off or missing, and
for reading everything now.

## Excel (built)

ClosedXML, as the raw materials and prep items imports use, in `Node.Infrastructure/Excel`
(`EmployeeWorkbook`, `PayrollWorkbook`); what a row becomes is `ImportEmployeesCommand`'s.
- **The template:** Name, Employment Type, Base Salary, Department, Job Role, Shift Start, Days
  Off, Hired On and Device No., each headed in English and Arabic. Dropdowns list monthly or
  daily, the departments and the roles; a sheet says how to fill it, with examples.
- **Reading a sheet:** columns are found by heading, in either language and in any order. It
  takes times as cells or typed ("10:00", "4 PM", "٤ م"), and Flexible (مرن) for flexible hours.
  Days off are English or Arabic ("الجمعة والسبت"). Dates are cells, ISO or day first
  ("1/10/2026"), and Arabic digits are read too.
- **What a row becomes:**
  - a department not on the till is added (a manager's import only: anyone else's row waits for
    a manager to add it), and a role not in its department is made;
  - a name or device number already on the till, asked for, or earlier on the sheet is skipped,
    so a sheet imported twice adds no one twice;
  - a manager's rows are added at once; anyone else's are each a request a manager approves.
- **The export** is in the template's columns, so it can be added to and imported again (those on
  the till are skipped). The payroll export is for the accounts: a title, each slip's columns as
  the Payroll tab shows them, and a totals row of sums.

## Phase 2: the API (built)

**Permissions:**
- `hr.view` sees HR and salaries; `hr.manage` enters employees and advances.
- Managers and accountants have both. Cashiers have neither.
- An accountant's employee changes and advances are requests a manager approves
  (approval modules `Employee` and `Treasury`).
- Correcting attendance, the devices and their settings, incentives and allowances, and
  finalizing pay are a manager's alone.

**Endpoints:**

| Endpoint | What it does |
| --- | --- |
| `GET /api/hr/employees` | The employees, left ones too |
| `GET /api/hr/employees/{id}` | One employee, left or not: their profile (404 when there is none) |
| `PUT /api/hr/employees/{id}/id-photo` | Their ID card's photo or scan (multipart `file`, JPEG, PNG, WebP or PDF, 5 MB at most), replacing the one there was: whoever manages HR |
| `GET /api/hr/employees/{id}/id-photo` | The card's file (`?download=true` to save it under its name): whoever sees HR |
| `DELETE /api/hr/employees/{id}/id-photo` | The card's file taken off: whoever manages HR |
| `PUT /api/hr/employees/{id}` | Add or change an employee: 200 for a manager, 202 (a request) for anyone else |
| `POST /api/hr/employees/{id}/leave`, `…/return` | Leave on a last day; come back from a day |
| `GET /api/hr/employees/template` | The Excel template to bring employees in from, with the departments and roles as dropdowns: whoever manages HR |
| `POST /api/hr/employees/import` | A filled-in sheet (multipart `file`, 5 MB at most): each good row an employee, a manager's at once and anyone else's a request; the rows skipped and why, and the departments added |
| `GET /api/hr/employees/export` | Everyone employed now as a styled Excel table, in the template's columns plus their day and hour rates |
| `GET /api/hr/payroll/export?month=` | The month's payroll as an Excel sheet: each slip's columns as the Payroll tab shows them, its status and what is paid, with the totals |
| `GET /api/hr/attendance?date=` | Everyone's day, plus punches under numbers nobody has; each day says whether they forgot to check out, and the totals count them |
| `GET /api/hr/employees/{id}/timesheet?month=` | One employee's month, day by day, with totals |
| `POST /api/hr/punches`, `…/{id}/ignore`, `…/{id}/restore` | A manual punch with its reason; ignoring or restoring one |
| `PUT /api/hr/employees/{id}/days/{date}` | Leave, day off or working day (a swap); with no kind, the mark taken off |
| `GET /api/hr/attendance/suspended?from=&to=` | The suspended days, everyone's: the corrections dashboard |
| `POST /api/hr/attendance/resolve` | A suspended day's punch-out, from a manager: the day worked out again |
| `PUT /api/hr/employees/{id}/days/{date}/penalty` | An administrative penalty in hours, with the reason; with no hours, taken off |
| `GET /api/hr/penalties?month=` | The month's administrative penalties, everyone's, each with what it takes off the pay |
| `GET /api/hr/departments`, `PUT …/{id}`, `DELETE …/{id}?moveTo=` | The departments with how many are in each and their roles, and what each costs (monthly salaries and daily wages apart, of those employed now); added or renamed, or deleted with its employees moved first: a manager's |
| `PUT /api/hr/departments/{id}/roles/{roleId}`, `DELETE /api/hr/roles/{roleId}?moveTo=` | A department's role added or renamed, or deleted with its holders moved to another: a manager's |
| `GET /api/hr/devices/connection` | Where a device reaches the till: the device's own port, when set, and the machine's addresses |
| `POST /api/hr/devices/test`, `…/sync` | The till reaching the device at `{ address, port = 4370, commKey = 0 }`: whether it answers and what it holds, or its users and whole log read and kept (approved if new): a manager's |
| `GET /api/hr/attendance/now` | Who is in now: each employee on the day their own window puts now in, in (an odd number of scans), left (even), not arrived, late (their shift and its grace started) or off. As punches come or a day is put right, the till's hub sends `EmployeeStatusChanged` to whoever sees HR: each employee it touched, with their day, state, since when, when due, and the time it was worked out |
| `GET /api/hr/holidays?year=` | A year's public holidays |
| `POST /api/hr/devices/{id}/read-users`, `…/read-attendance` | A command left for the device: list its users, or send its attendance from a day to another again: a manager's |
| `GET /api/hr/device-users` | The users the devices listed, who on the till has each number, and the punches waiting under it |
| `GET /api/hr/attendance/late-punches`, `POST …/settle` | Device punches that came after their month's pay was finalized; marked seen to by a manager |
| `PUT /api/hr/holidays/{id}`, `DELETE …/{id}` | A public holiday added or changed, or taken off: a manager's |
| `GET`/`PUT /api/hr/settings` | The grace, and how close two scans are to be one |
| `GET /api/hr/devices`, `POST …/{id}/approve`, `…/revoke` | The devices, approved by name |
| `GET /api/hr/advances?month=` | The month's advances, each deducted from whose pay (paid to them, or their share of a lump sum); and the advances handed out with no one named, every one not all given out: how much, given out, left, and the shares |
| `POST /api/hr/advances`, `POST /api/hr/advances/{id}/void` | An advance paid outside the till, recorded in HR only (`{ advanceId, amount, date, employeeId?, notes }`): a manager's at once, anyone else's a request (module `Advance`); voided by a manager while its pay is not finalized, a lump sum once no share of it stands |
| `POST /api/hr/advances/{id}/distribute`, `POST /api/hr/advance-shares/{id}/take-back` | A lump sum given out, a share each (`{ shares: [{ employeeId, amount, notes }] }`), in parts and never more than is left; a share taken back while its pay is not finalized: a manager's |
| `GET /api/hr/payroll?month=` | Every slip as it stands, with the month's totals: its total salaries (gross) and administrative penalties among them |
| `GET /api/hr/payroll/daily-cost?date=` | A day's labour cost: who punched in, each at their day's rate, by department, the day's overtime pay beside it |
| `POST /api/hr/payroll/calculate?month=` | Saves the drafts |
| `POST /api/hr/payroll/{employeeId}/finalize?month=` | Finalizes one slip |
| `POST /api/hr/payroll/adjustments`, `…/{id}/void` | Incentives and leave allowances |

**The treasury:**
- An advance is a safe voucher of kind `EmployeeAdvance`, from the safe or a day's takings
  (`POST /api/financials/vouchers`), or a line of the daily batch.
- Its pay is a `SalaryPayment` against a finalized slip, never more than is owed of it.
- `GET /api/financials/advances` lists advances, for HR viewers only.
- An advance deducted by a finalized slip cannot be voided. One paid after its month's slip is
  finalized counts in the next month.
- The daily cash, reconciliation and takings preview count advances and salaries paid out of a
  day's takings. The safe's ledger names the employee.

**Where an advance is paid from:**
- **From the till:** Financials (the advance voucher), Purchases (its New advance button opens the
  same dialog), or the daily batch's advance block. A safe entry, out of the safe or a day's
  takings.
- **Outside the till:** HR → Advances → New advance, the same dialog without "Paid from". It covers
  an advance from the owner's own pocket, say. It is an `HrAdvance` (migration `HrAdvances`),
  recorded in HR only: no cash leaves the safe or the takings, and Financials never sees it.
- **Either way:** paid to an employee, it comes off their pay (the payslip says "outside the till"
  for these). Paid to no one yet, it is a lump sum given out a share each. A share
  (`AdvanceAllocation`) is of a safe entry or of an HR advance, never both.

**Advances handed out with no one named (السلفيات المعلقة):**
- An advance voucher, or a line of the daily batch, may name no employee. Its cash leaves the safe
  or the takings at once, as any advance's does.
- It waits on HR's Advances (`GET /api/hr/advances`). A manager gives it out to employees, a share
  each (`AdvanceAllocation`, migration `AdvanceAllocations`): in parts if need be, and never more
  than is left. It is fully allocated once the shares add up to it.
- Each share is deducted from its employee's pay as their own advance would be: from the month the
  money was handed out, or the first after it whose pay is not finalized for them. It shows on
  their payslip as a share of that day's advances.
- A share is taken back while that pay is not finalized. The lump sum is voided only once no share
  of it stands.
- Advances paid to an employee directly (from the daily batch or a Financials voucher) work as
  before. They are listed beside the shares as allocated.
- **Screens:**
  - The employee is chosen by typing part of their name or job (`EmployeePicker`), in the daily
    batch's advance block and the Financials advance voucher. "No one yet" makes the advance a
    lump sum, and Financials → Advances counts lump sums apart, with a link to HR → Advances.
  - HR → Advances (السلفيات) has two views. Pending (وضع معلق) shows each lump sum: what is given
    out and what is left, and its shares, each taken back by a manager. Allocated shows the
    month's advances off whose pay: paid to them, or a share.
  - A manager's Distribute dialog has a row an employee, offered only those employed on its day
    and each once. Split equally shares what is left in whole pounds, the odd ones on the first.
    It never gives out more than is left, and some may stay for later.

**KPIs:** a finalized month's slips count as its wages (`WagesCost`), on top of what is
entered. The old "سلفيات" expense item is archived once advances are entered as advances; what
is already entered stays as it is.

A day off worked earns its leave allowance by itself (the slip's `DaysOffWorkedPay`, migration
`DaysOffWorkedPay`), and a public holiday worked its holiday allowance (`HolidaysWorkedPay`,
migration `PublicHolidays`); leave allowances entered by a manager are for anything else.

## Employee permissions (صلاحيات الموظفين, built)

Each tab of HR & Payroll is given on its own: Now, Employees, Departments, Attendance,
Corrections, Payroll, Advances, Holidays and Devices, and Custody & Assets beside them
(`HrTabs`, a bit each).

- **Who holds which.** A manager holds every tab, always, and alone gives them (the
  Permissions tab, `GET /api/hr/access`, `PUT …/access/employees/{id}`,
  `PUT …/access/staff/{staffId}`).
  - An employee holds none until given some (`Employee.HrTabs`, stored by name). They work
    theirs in the portal, on their phone: `/hr` there is HR & Payroll's own screens, only their
    tabs, under the portal's bar.
  - An accountant holds every tab until a manager ticks theirs (`hr.StaffHrAccess`, by the till
    account's id; the account itself is the delivery system's).
- **What a tab lets them do.**
  - Someone's record, an advance or an asset: a change goes to a manager to approve, from an
    employee as from an accountant.
  - What only a manager does otherwise (punches put right, days marked, suspended days,
    penalties, departments and roles, holidays, the devices and their settings, incentives
    and allowances, finalizing a month, giving out and voiding advances, custody categories):
    an employee holding the tab does it at once, as a manager would (`[HrTab(..., Decide =
    true)]`). An accountant does not: that stays a manager's for them.
  - PINs, and who holds which tab, stay a manager's alone. Paying a salary is a safe entry,
    Financials', which an employee never reaches.
- **The token.** An employee's portal token names their tabs (`hr_tabs`), which the portal reads
  for its tabs. Every request checks them against the ones they have now, as it checks their
  PIN: a change of tabs signs them out, and they sign in again to see the new ones.
- **The API.** Each HR and Custody endpoint says its tabs (`[HrTab(...)]`; a test keeps every
  one saying so). The portal's port answers `/hr`, `/custody`, `/api/hr` and `/api/custody`
  too, and only an employee's token holds a tab there: a till account's, carried over the
  Wi-Fi, holds none.
- **What is never sent.** These come back as null to whoever lacks the tab that shows them:
  - what anyone earns, their national ID, mobile and ID card, without the Employees or Payroll
    tab (Attendance has the names and shifts, not the pay);
  - a department's costs, without Departments, Employees or Payroll;
  - what an asset cost, to an employee, always: Custody's screens show them none, and an asset
    they change keeps its cost when they send none.
- **The screens.** Only the tabs held show, and a typed address opens only those (the first one
  held otherwise). A link to another tab (an employee's month from Corrections, the timesheet
  from a payslip) shows only to whoever holds that tab; the profile's payslip is the Payroll
  tab's.

## The screens

**Built:**
- HR & Payroll in the top bar for whoever holds a tab of it, opening on the first one they hold, Now (الآن): who is in the restaurant right
  now, by department, green in since their last scan, grey left, yellow not arrived yet, red
  late, and off today; with the counts, and what waits on Corrections. A pushed card changes in
  place, lit up for a moment, without the page being read again; the whole page is read every
  minute for the clock, and after the connection was lost. Each card opens the employee's month.
  When an approved device has not called in over ADMS for three minutes, the page says so (its
  punches then come only with Sync) and where to point it; the Devices tab says it of each.
  - Employees, in two lists one at a time (a switch at the top, each side with its count; the
    second kept in the address as `?view=device`): System employees (الموظفين العاملين), and
    Device users (مسجلين على جهاز البصمة), the device's users nobody on the till has the number of
    yet, each leaving it once added or linked. Show who has left and Department filter the
    first; Excel, Sync device and Add employee are above both;
  - Employees: add, change, leave and come back; an accountant's go to a manager; a row (or the
    name) opens the employee's profile;
  - An employee's profile (`/hr/employees/{id}`): their personal details (full name, national
    ID, mobile, start), their work and pay, and their ID card shown (a photo as it is, a PDF in
    the browser's viewer, larger on View larger) with Upload, Replace and Take off; Edit opens
    the Employees form, Deactivate their leaving, Bring back their return, all locked while a
    change waits for a manager; beside them (below on a narrow screen), their payslip for the
    business month, a month back at a time, as Payroll has it (incentives, finalizing and paying
    included), printing alone;
  - Device users: add each as an employee, or link their number to one already here;
    the form takes the department, a role of it (picked, or a new one typed), monthly or daily
    pay, and flexible hours (no start time); the list shows each one's department, filters by
    one, and a daily worker's wage a day; Sync device reads each device directly over TCP 4370
    (at the address last used in this browser, with its port and comm key, and where each
    approved device last called from), its users listed and its new punches kept; an approved
    device not read that way (its port closed while it uses ADMS, or no address known) is asked
    for its users over ADMS instead, and the list read again as they come;
  - Employees' Excel menu: Download template (تحميل القالب), Import Excel (رفع إكسيل) and
    Export employees (تصدير الموظفين); what an import did shown row by row;
  - Departments, as a table of what they cost: each department, its roles a row each (and one
    for those holding none) with how many hold it and what they cost, then the department's
    total and its share of the salaries, and all of them at the foot; monthly salaries and daily
    wages said apart, a daily worker's month being the days they work; added, renamed, and
    deleted with its employees (or a role's holders) moved first; General kept;
  - Attendance devices: where to point the K40, approve or refuse it, have it list its users or
    send its attendance again (the command's progress shown as it goes), and the grace and
    double-scan settings. Connection settings (إعدادات التوصيل) above the list: the device's IP
    address, port and comm key (as last used in this browser, remembered once a test or sync
    reaches the device; else the address it last called from), with
    Test connection and Sync device over TCP 4370, each with progress, a message at the bottom
    and the outcome under the buttons.
  - Corrections also lists the punches that came after their month's pay was finalized, a day at
    a time, until a manager marks them seen to.
- Payroll's Export Excel (تصدير إكسيل): the month as a sheet for the accounts.
- Payroll in two views (`?view=daily` for the second): the month's, its Total salaries (إجمالي
  الرواتب, before anything is deducted) first among the figures, and lateness, absence and
  administrative penalties together as Deductions & penalties (خصومات وجزاءات) just before Net
  payable, each slip's parts under its amount; and Daily cost (تكلفة يوم): a day chosen (today so
  far, or one before it), who came in at their day's rate (a monthly salary over 30, or the
  daily wage), the total and each department's with its share, and the day's overtime beside it,
  not in the total.
- Financials → Advances (السلفيات): the month's advances, each employee's total, a new advance
  from the safe or a day's takings, and voids.
- The daily batch: an employee advance block beside the supplier payments.

- Attendance: the day's review (مراجعة يوم), everyone's in, out and status on a day, or one
  employee's month.
  - Forgot to check out (نسي تسجيل الانصراف): a punch-in with no punch-out once the shift is over
    (flexible hours, or a day off: once the day is), its row lit and its Out said so, counted
    at the top, where a click shows only them. A suspended day is one too; the 18-hour rule for
    the pay stays.
  - Each day opens to its punches.
  - A manager adds a missed punch with its reason, ignores one that should not count, marks
    a day as leave or a day off, enters a suspended day's punch-out, or gives the day an
    administrative penalty.
  - Punches under a number nobody has are listed under the day.
- Corrections (مراجعة وجزاءات), its tab counting the days waiting:
  - the suspended days since the first of last month (or a day chosen), each with its punches
    and the stray punch-in marked; a manager enters the punch-out (after the punch-in, within
    18 hours of it and before the next day starts; the shift's end a click away) or ignores the
    stray punch;
  - the month's administrative penalties, with what each takes off and the total; a manager
    adds one for whoever is employed in the month (told when it replaces the day's own), and
    changes or takes one off.
  - Accountants see both, with nothing to change.
- Holidays (العطلات الرسمية), a year at a time: the holidays entered, each changed or taken off
  by a manager; and Egypt's fixed-date holidays not entered yet (the seven fixed dates, and Sham
  El-Nessim, the Monday after Coptic Easter), each added once checked against the government's
  announcement. The Eids, Arafat, the Hijri New Year and the Prophet's Birthday follow the moon,
  and are added once announced. Attendance names the holiday on its days, and the payroll and
  payslip show the holidays worked.
- Payroll: the month's slips, each part of the formula a column, with totals.
  - Calculate drafts saves them.
  - Each row opens the employee's payslip: the formula line by line from base salary to net
    payable, the days it came from, incentives and allowances (a manager adds and voids them),
    advances, and salary paid.
  - Finalize (a manager's, once the month is over and no day is suspended; the payslip points
    to Corrections when one is), then Pay salary through the safe entry dialog.
  - It prints as a payslip.
