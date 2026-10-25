# Custody & Assets (عهدة وأصول), and the employee portal

What is in whose custody, the vouchers that move it, breakage as a day's waste in Costing, and
a self-service portal where an employee sees their own details, payslip and custody.

Asked for on 2026-10-25. Built in phases: **the database layer and the API are done**
(entities, rules, migration `CustodyAndAssets`, the endpoints below, tests); the manager
screens and the portal's screens come next.

## The choices made

- **A quantity per asset** (not one row per piece): "Dinner plates × 40" at a cost a piece; a
  fixed asset × 1. Vouchers move a number of them.
- **Breakage of a consumable becomes a breakage entry in Costing → Waste**, the second kind of
  waste beside spoiled stock: at its cost, no stock movement.
- **The portal is reached on the restaurant Wi-Fi**, through a port of its own that answers only
  the employee login and the portal's own API (as 5082 answers only `/iclock`). Five wrong PINs
  lock it for five minutes, as a till PIN does.

## The model (built)

**`AssetCategory`** (`hr.AssetCategories`): a name (unique) and `IsConsumable`: the fragile and
worn (plates, glasses) whose breakage is a cost of the day, or a fixed asset (a fryer, a laptop).

**`AssetItem`** (`hr.AssetItems`): a name, a category (or none), a cost a piece (or none known),
a `Quantity`, the employee whose custody it is in (`EmployeeId`, none in the store or at
maintenance), its `Origin` and `Status`.
- `Origin`: `HrManual` (entered in HR) or `FinancialPurchase` (bought on a posted invoice, which
  it names with its line: `SourceInvoiceId`, `SourceInvoiceLineId`; one asset to a line).
- `Status`: `InStore` (with no one: bought and not handed out, or handed back), `InCustody`,
  `Maintenance`, `BrokenWaste` (all of it broken), `Discarded` (all of it lost or thrown away).
  `InStore` is added to the spec's four: an asset can be with no one.
- It changes hands only by vouchers. Nothing is deleted: what is gone stays, with none left.

**`AssetTransaction`** (`hr.AssetTransactions`), a custody voucher (إذن), made by the asset:
- `CheckIn` (إذن دخول): all of it into an employee's custody: from the store, back from
  maintenance, or from no one. One in someone else's custody is checked out from them first.
- `CheckOut` (إذن خروج), with a `Reason`:
  - `Returned` (مرتجع) to the store, or `Maintenance` (صيانة): all of it, from whoever held it;
  - `Breakage` (كسر), `Lost` (فقد) or `Discarded` (استبعاد): some of it, the rest staying where it
    is; with none left, it is `BrokenWaste` or `Discarded`, with no one.
- It keeps the date (a business day), who held it (`EmployeeId`), how many, the cost a piece as
  it was then, the notes, and `WasteEntryId`: the breakage entry it made.

**`BreakageEntry`** (`inventory.BreakageEntries`), Costing → Waste's second kind: what broke (its
name as it was), how many, at what cost, on which business day, who held it, made by a voucher
(`AssetTransactionId`, one entry to a voucher) and voided with it.

**`Employee`**: the portal PIN, kept hashed (`PinHash`; never stored or shown as typed), 4 to 6
digits, with `FailedPinAttempts` and `PinLockedUntilUtc` (five misses lock it five minutes; a
manager setting a new PIN unlocks it). A PIN needs a mobile number, which they sign in with:
**one employee to a mobile number** (a unique index, and the employee form says who has it).

**`ExpenseItem`**: `AssetCategoryId` marks an expense whose purchases are assets: each posted
invoice line of it will be an asset in the store, of that category, its quantity at the line's
cost a piece (phase 2).

## The API (built)

**Who:** seen by whoever sees HR (`hr.view`). Categories are a manager's. Assets and vouchers are
whoever manages HR's: a manager's at once (200), an accountant's asked of a manager (202,
approval module `Custody`), the asset locked until decided. A portal PIN is a manager's to set.

| Endpoint | What it does |
| --- | --- |
| `GET /api/custody/categories` | The categories, with how many assets are in each and in custody |
| `PUT /api/custody/categories/{id}`, `DELETE …/{id}` | Added or renamed, consumable or not; deleted only with nothing in it and no expense bought as it: a manager's |
| `GET /api/custody/assets?withFinancial=` | The assets entered in HR; with `true` (Link with Financials), those bought on invoices too |
| `GET /api/custody/assets/{id}` | One asset with its vouchers, the latest first |
| `PUT /api/custody/assets/{id}` | Added (entered in HR) or changed: name, category, cost a piece, notes; how many is set when added, and changes by vouchers after. A bought one's cost is its invoice's |
| `POST /api/custody/assets/{id}/check-in` | إذن دخول: into an employee's custody (`employeeId`, `date` today unless given, none after it) |
| `POST /api/custody/assets/{id}/check-out` | إذن خروج: `reason` (Returned, Maintenance, Breakage, Lost, Discarded) and `quantity`; a consumable's breakage makes a breakage entry, said on the voucher (`routedToWaste`, `wasteEntryId`) |
| `GET /api/custody/vouchers?from=&to=` | The vouchers of those business days (the last 30 unless said) |
| `GET /api/hr/employees/{id}/custody` | What is in their custody now, since when, and what it cost: for whoever sees HR |
| `PUT /api/hr/employees/{id}/pin`, `DELETE …/pin` | Their portal PIN set (4 to 6 digits; it signs them out and unlocks them) or taken off: a manager's. `EmployeeDto` says `hasPin` and `pinSetAtUtc`, never the PIN |
| `GET /api/costing/breakage?days=` | Costing → Waste's breakage, beside `…/spoilage` |
| `PUT /api/inventory/expense-items/{id}/asset-category` | An expense bought as an asset of a custody category, or (none) a plain expense: apart from the item's own form, so an edit there never clears it |
| `POST /api/auth/employee-login` | `mobileNumber` and `pinCode` → a token with the role `Employee` and their id; one answer, "Wrong mobile number or PIN.", whether or not the number is anyone's |
| `GET /api/portal/me`, `…/id-photo`, `…/payslip-months`, `…/payslip?month=`, `…/custody` | The portal, read-only, only ever the signed-in employee's own, from their token |
| `GET /api/portal/custody` | Their custody as `EmployeeCustodyDto`: `name`, `category` and `quantity` alone, those of one name and category (handed over apart) on one line, added up. No cost, id or date: what an asset cost never reaches the employee's phone |

**Bought assets:** posting an invoice (alone or in the daily batch) makes, for each line of an
expense marked as an asset, an asset in the store of its category: its quantity at the line's
cost a piece (a quantity that is not a whole number is one, at the line's cost), one asset to a
line. Voiding (or correcting) the invoice takes its assets with it while none has a voucher;
once one has been handed out, the invoice is not voided.

**Breakage in the KPIs:** the month's statement says the breakage (`breakage`) apart, and does not
add it to the costs or take it off the profit: an asset bought on an invoice was counted as an
expense when it was bought, and counting its breaking too would count it twice.

**The portal's security:**
- An employee token holds the role `Employee`, never a till role: the till's default and fallback
  policies now require a staff role, so it passes nothing but the `Employee` policy on
  `/api/portal`. A staff token does not pass that one.
- Every request with it checks the employee is still employed and the token carries the PIN they
  have now (when it was set): a new PIN, none, or their leaving signs them out at once.
- Five wrong PINs lock it five minutes; ten sign-ins a minute from one address at most.
- **The portal's port** (`Portal:Port`, 5083, which `start.sh` listens on) answers only
  `POST /api/auth/employee-login`, `/api/portal/*` and the app's files (GET); the rest of the
  API, the hub, `/iclock` and the health checks are not found there. Its address alone, or any
  screen of the till's (`/costing/waste`), sends a phone to `/portal`.

## The screens

- **Custody & Assets** (HR's view to see it, HR's manage to change it; an accountant's changes go
  to a manager): Categories, Assets (the Link with Financials toggle adds those bought on
  invoices, and is remembered), and Vouchers (a breakage that went to Waste says so).
- **Costing → Waste** lists the breakage beside the spoilage; **Costing → Expense items** marks
  an item's purchases as assets of a category.
- **The employee profile**: an Assigned custody card, and a Portal card where a manager sets,
  changes or takes off the PIN and sees the portal's Wi-Fi address.
- **The portal** (`/portal`, then `/my-profile`): the employee's details and ID card, their
  payslip (this month's draft, or one before), and their custody as Asset | Category | Quantity,
  with nothing of what it cost; read-only, and nothing of the till's navigation. On a phone:
  details, custody, then the payslip.

## Not decided yet

Undoing a voucher made in error; adding units to an asset (a new purchase is a new asset for
now).
