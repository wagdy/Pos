# Shared kernel

One order model for the delivery system (cloud, Railway) and the POS (the restaurant's own
machine), so an order taken by a captain in the delivery app or online arrives at the till as
the same object, with no mapping in between.

| Project | What | Used by |
|---|---|---|
| `Otantik.SharedKernel` | Orders, catalog (`MenuItem` is the "product"), users and roles, customer loyalty, RBAC, audit trail | Delivery system and POS |
| `Otantik.BuildingBlocks` | Entity, aggregate and domain-event base types, repository ports | Rich-model modules (Inventory) |

`SharedKernel` began as a literal copy of the delivery system's entities and enums. Every
property kept its name and type, so the database columns and JSON stay exactly as they are.
Everything the POS needed was added on top.

## Rules for changing it

These exist because the two systems are deployed separately. The cloud updates on every push;
the restaurant machine updates when someone installs a new build. For a while after any change,
one of them is running the old model.

1. **Additive only.** Never rename or remove a property, and never change its type. The names
   are the delivery database's column names and the JSON property names its clients read.
2. **Enums: append at the end, never insert.** The databases and the delivery system's
   messages use names, but anything that sends numbers (an older build, a client left on
   SignalR's default) would read a value inserted in the middle as the one after it.
3. **A system must understand a value before another one sends it.** When you add an enum value,
   update the side that receives it first. For anything the cloud sends to the till, that means
   the restaurant machine. An unknown role is refused (`RolePermissions` fails closed), and JSON
   with an unknown enum value fails to deserialize.
4. **Rules, not plumbing.** No EF Core, HTTP or SignalR code in here. Each system maps the model
   to its own database, and must `Ignore()` the computed properties (`Order.IsPickup`,
   `LineTotal`, `IsVoided` and the like).
5. **Tests run on every change:** `dotnet test --project tests/Otantik.Domain.Tests`. They pin
   the Captain Order restrictions, void classification, the loyalty formula and the sync JSON.

## Business decisions, and where each one lives

- **Captain roles.** `CaptainOrder` is dine-in floor staff only, taking orders in the delivery
  app: create and submit, nothing that touches money. Drivers are `DeliveryCaptain`. The delivery
  system's `MoveDriversToDeliveryCaptain` migration moves today's driver accounts; see below.
- **Captains need the internet.** They take orders in the cloud app, so with the restaurant's
  link down they cannot, and the cloud must say so rather than accept an order the till will
  not receive until later. The restaurant counts as online while its machine holds a connection
  to `/hubs/pos-sync`. The machine pings every 10 seconds (`CloudOrderListener`); with SignalR's
  default 30-second `ClientTimeoutInterval` the cloud notices a dead link within half a minute.
  The delivery system tracks this in `PosNodePresence`.
- **The loyalty rate is a system setting.** Discount = Points / 10 by default; the rate is the
  delivery system's existing `LoyaltySettings.RedemptionValuePer100Points` (L.E per 100 points;
  10 is Points / 10), edited in its Campaign Manager. The till receives it with the menu and
  prices points with the delivery system's own arithmetic (`LoyaltyRedemption`), so the two
  never disagree. Until the first sync the till uses 10.
- **A refund settles the customer's wallet.** A Void After on an order with a customer account
  queues `LoyaltyRefundDue` in the till's outbox: the refunded items' share of the redeemed
  points goes back. What the refunded money had earned is taken back when the refunded order
  reaches the delivery system, worked out from the order's own totals
  (`LoyaltyService.SettleTillOrderAsync`), so it comes out right whichever message arrives
  first. Offline both wait in the outbox; each is applied once.
- **A delivery can be paid after it is handed over.** Cash on delivery: the driver marks the
  order Delivered in the delivery app and brings the money back to the till later. Paying and
  points follow `OrderRules.AwaitsPayment` (not paid, not cancelled), not `IsOpen`, so the order
  stays on the till's open list, payable, until the cash is taken. Nothing more can be added to
  it.
- **The status only moves forward**, on both sides, by `OrderStatuses.Progress`: a copy of an
  order that left one system before a later change cannot undo that change in the other.

## The delivery system's side: api/pos-sync

Served by the delivery system (`PosSyncController`, `PosSyncHub`, `PosSyncService`) and spoken
by the POS's `OtantikPos.Node.Infrastructure/DeliverySystem` client. The fake in
`tests/OtantikPos.Node.Infrastructure.Tests/FakeDeliverySystem.cs`, also served by
`otantik-pos/backend/tools/OtantikPos.DevCloud`, plays the same part for tests and laptops.

- **Key.** Every request, the hub's included, carries `X-Pos-Node-Key`, which must equal the
  delivery system's `PosSync:NodeKey` (the till's `DeliverySystem:NodeKey`). A wrong or missing
  key gets 401; with no key configured, no till can connect. The key opens only `api/pos-sync`
  and `/hubs/pos-sync`, and no staff or customer token opens them.
- REST bodies and the hubs use enum names (the till reads numbers too). Errors are
  `{ "errors": ["..."] }`. A 5xx tells the till the cloud is down; it keeps the message in its
  outbox and tries again.
- `GET api/pos-sync/reference-data`: the tax rate, the menu (categories, sub-categories,
  add-ons, and items with their variants and add-on links; soft-deleted ones left out), every
  staff account (`id, fullName, role, isActive`), and `redemptionValuePer100Points`.
- `GET api/pos-sync/customers/by-phone/{phone}`: `CustomerProfile`, or 404. The number must
  match the account's exactly, as every phone lookup in the delivery system does.
- `GET api/pos-sync/customers/{userId}/points`: `{ "points": n }`, or 404.
- `POST api/pos-sync/loyalty/redemptions` with `{ customerUserId, points, orderPublicId }`: 204,
  and 204 again for an `orderPublicId` already redeemed; 409 `{ "errors": [...] }` when the
  balance is too low. Recorded in the ledger as `Till order <id>`, which a unique index keeps to
  one row per order.
- `POST api/pos-sync/loyalty/refunds` with `{ refundId, customerUserId, orderPublicId, points,
  refundedAmount }`: 204. Gives back `points`, once per `refundId` (`Till refund <id>`).
- `PUT api/pos-sync/orders/{publicId}`: the shared `Order`'s JSON, upserted by `PublicId` and
  marked `ExternalSource = "POS"` when new. The till owns the money and the voids, which are
  taken as sent; items are added or updated and never removed (a captain's newer round is kept);
  customer, table and notes are taken when present and never blanked; status and an item's
  "sent" mark only move forward. Once the order is paid it earns its points, net of refunds,
  and the punch cards. 422 when it names a menu item or add-on the delivery system does not
  have. Not sent back down to the till.
- `GET api/pos-sync/orders?changedSince={utc}`: every order the till holds a copy of (dine-in,
  and those the till created) with `UpdatedAt` at or after that time. Online orders are not
  sent; they are run from the delivery system's own screens.
- Hub `/hubs/pos-sync`, method `OrderChanged`: the whole `Order`, sent after any change saved in
  the delivery system to an order the till holds (`PosOrderChangeInterceptor`), once committed.
  This is how a captain's table reaches the till, which prints each round marked sent.
- In the delivery system's reports a till order is a sale once paid (`ClosedAt`), for what was
  charged less what was refunded; a full refund is no sale, and voided items are not sold items.

## Not done yet

Nothing deployed uses this kernel yet. Deploying changes production, so each step is its own
reviewed change.

**Delivery system**
- Done in code, not deployed: it uses `Otantik.SharedKernel` in place of its own copies. The
  `AdoptSharedOrderModel` migration adds `PublicId` (backfilled, unique) and `Type` (pickups
  become `Takeaway`), then the remaining new columns, then drops `IsPickup`; queries use `Type`.
  `AddTillLedgerReferenceIndex` adds the ledger index above. The app applies both on start-up.
- Done in code, not deployed: drivers are `DeliveryCaptain`. The `MoveDriversToDeliveryCaptain`
  migration moves every `CaptainOrder` account (all drivers today), and the app applies it on
  start-up. The driver screens, policies and push notifications follow `DeliveryCaptain`; the
  Staff tab creates both kinds of captain, and Cashier and Manager accounts for the till. A
  staff token whose role no longer matches its account is refused, so on the first request
  after the deploy each driver is signed out once and signs back in as a `DeliveryCaptain`.
  Deploy the backend and the Angular app together.
- Done in code, not deployed: `api/pos-sync` and `/hubs/pos-sync` above.
- **Railway:** the Dockerfile builds from the repository root, to reach `shared/`. The api
  service's Dockerfile path (Settings > Build) is `backend/Dockerfile`, and it is deployed with
  `railway up --service api` from the repository root (not from `backend/` as before). The web
  app builds `frontend/` on its own: `railway up ./frontend --path-as-root --service web`, also
  from the repository root. Without `--path-as-root` the CLI uploads the whole repository
  wherever it is run from, and the web build fails. `railway.json` is not used: Railway has deprecated it.
  When the till is installed, set `PosSync__NodeKey` to a long random secret, the till's
  `DeliverySystem:NodeKey`; until then no till can connect. Locally:
  `docker build -f backend/Dockerfile .` from the repository root.
- Done in code, not deployed: dine-in captains' screen, `/tables` in the Angular app. A captain
  opens a table with its first round and sends further rounds (`api/dine-in`, `DineInService`),
  priced with the shared `OrderItemBuilder` and `OrderPricing`; who may do what is
  `OrderAccessPolicy`. A round is put together on the phone and only sent whole, as a captain
  can never take an item off. Each round's items are marked sent and reach the till over
  `/hubs/pos-sync`. One open order per table number. While no till is connected the API answers
  503 `{ "errors": ["The restaurant's till is offline. Take this order at the till."] }`, and
  `/hubs/dine-in` tells the screens (`TillPresence`) so they say so first; a table paid or voided
  at the till reaches its captain's screen there too (`TableChanged`).
- Done in code, not deployed: the till's own orders live here too, so the drivers' list
  (`GET api/orders` as a `DeliveryCaptain`) is limited to deliveries, and the admin knows the
  `Served` status and each order's `type`.
- Done in code, not deployed: a round that reaches the till only after the bill was paid or
  cancelled there is ignored by the till, whose copy is final (`OrderRules.AwaitsPayment`, the
  same rule on both sides). When that final copy reaches the delivery system, any item it lacks is
  voided before the kitchen ("Not made") so the bill and reports leave it out, and the captain's
  screen says the round was not made.
- Tests: `tests/RestaurantDelivery.Api.Tests` runs the delivery API in process on its own
  PostgreSQL database (`OTANTIKPOS_TEST_DB`, as the POS tests) and plays the till with its own
  client, `DeliverySystemApi`, and real SignalR connections: pos-sync, points, the live link,
  captains' tables and the drivers' list. Part of `dotnet test --solution Otantik.slnx`.
- Done in code, not deployed: order access follows the shared rules. The order list, status
  changes and the order hub need `OrderViewAll` (admins, cashiers, managers, drivers), with an
  admin's custom Role able to narrow it by the Orders module as before; a status change is
  `OrderAccessPolicy.CanChangeStatus` (so a driver can no longer cancel: that is a void); one
  order is visible to `OrderViewAll` or its customer; a phone-in order counts as staff's when
  `CanCreate` allows it. Role names remain only where they say what an account is rather than
  what it may do: a customer's own checkout, the drivers' deliveries-only list, push
  notification audiences, and the admin module system itself.
- Done in code, not deployed: online orders are priced with `OrderItemBuilder` and
  `OrderPricing`, at checkout, in the checkout page's promo preview, and when an admin edits an
  order; `DeliveryDiscountAmount` is stored. An edit no longer adds a free-delivery promo's fee
  back or charges again for what points paid (it did both: L.E 25 too much on the test order),
  and refuses to cut a bill below the points already spent on it. Orders placed before this
  have no `DeliveryDiscountAmount` stored, so editing one of those still loses its delivery
  discount.

**POS**
- Done: the Application layer on the shared model, as the modules `OtantikPos.Ordering.*` and
  `OtantikPos.Inventory.*`.
- Done: `OtantikPos.Node.Infrastructure`, covering the node's PostgreSQL (15+), the outbox,
  the TCP printers, and the delivery-system client, listener and reference-data sync.
  Integration tests run against a real PostgreSQL when `OTANTIKPOS_TEST_DB` is set to a
  connection string with no `Database=`; without it they are skipped.
- Done: `OtantikPos.Node.Api`, the tills' REST endpoints and SignalR hub (`/hubs/till`), with
  PIN sign-in, permissions from `RolePermissions`, and errors in the delivery system's
  `{ "errors": [...] }` shape. Development uses its own database, `otantik_pos_node`.
- Done: the till app, `otantik-pos/node-frontend` (Angular 21.2, Angular Material and CDK
  21.2.8, standalone and zoneless). `npm run build` writes it into `OtantikPos.Node.Api/wwwroot`,
  which the API serves; `npm start` serves it on :4200 with `/api` and `/hubs` proxied to :5080.
  Buttons follow `GET /api/auth/me`'s permissions only. Fonts are bundled, so it looks right
  with no internet. Its Reports screen (`GET /api/reports/day`) shows one business day from the
  till's own database.
- Done: installing it at the restaurant, `otantik-pos/deploy` (see its `INSTALL.md`): one Docker
  image built from `otantik-pos/Dockerfile` with the till app inside, its own PostgreSQL and a
  daily backup, from a compose file and a `.env`, the same on Windows, macOS or Linux. Managers
  give the staff their till PINs on the till's Staff screen.
- Run against the real delivery system on 4 October 2026, locally, on scratch databases:
  reference data, customer lookup, redemption, refund, order push and reports, a driver's status
  change in the cloud reaching the till, and captains' tables in both directions.
- Removed (3 October 2026): the earlier POS on .NET 8 (`OtantikPos.Domain`, `.Application`,
  `.Infrastructure`, `.Api`, its `OtantikPos.slnx`) and its Angular app, `otantik-pos/frontend`.
  Its development database, `otantik_pos` in the `otantik-pos-db` container, was dropped too.
