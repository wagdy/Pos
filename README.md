# Otantik POS

The till for Otantik restaurants. It runs on one machine in the restaurant, and the counter
screen and tablets open it in a browser over the restaurant's network. It keeps taking orders,
sending them to the kitchen printer and taking payment when the internet is down, and catches up
with the delivery system when it returns.

## What's here

| Folder | What |
|---|---|
| `otantik-pos/backend` | The till server: .NET 10, its own PostgreSQL, an outbox to the delivery system, TCP kitchen and receipt printers |
| `otantik-pos/node-frontend` | The till app: Angular 21, standalone and zoneless, served by the till server |
| `otantik-pos/deploy` | Installing it at the restaurant with Docker: see [`INSTALL.md`](otantik-pos/deploy/INSTALL.md) |
| `shared` | The order model the till shares with the delivery system, and the rules both apply alike. [`shared/README.md`](shared/README.md) describes it and the contract between the two |
| `tests` | Unit and integration tests |

The delivery system (the website, its admin and the cloud API the till syncs with) is kept in a
separate repository. The till talks to it only through the contract in `shared/README.md`.

## Running it on a laptop

You need the .NET 10 SDK, Node 22, and Docker or a PostgreSQL 15+ of your own.

- Database: `docker compose -f dev/docker-compose.yml up -d` starts a PostgreSQL on
  `127.0.0.1:5434`, the one the development settings (`appsettings.Development.json`) use. Its
  data stays in a Docker volume between restarts. With a PostgreSQL of your own, set
  `ConnectionStrings:OtantikPos` to it instead.
- Till server: `dotnet run --project otantik-pos/backend/src/OtantikPos.Node.Api`, on
  `http://localhost:5081`. On its first start it creates its database and a manager with
  PIN 1234 (`Auth:BootstrapManager` in the development settings).
- Till app: `npm ci` and `npm start` in `otantik-pos/node-frontend` (`http://localhost:4210`,
  proxied to the server), or `npm run build` to build it into the server's `wwwroot`.
- Delivery system: the development settings look for one on `http://localhost:5080`. Without
  one, `otantik-pos/backend/tools/OtantikPos.DevCloud` plays one on `127.0.0.1:5099`: start the
  till server with `-- --DeliverySystem:BaseUrl=http://127.0.0.1:5099 --DeliverySystem:NodeKey=test-node-key`.

### Printers

Without real printers, `dev/fake-printer.py` (Python 3) plays the kitchen's on port 9101 and
the receipts' on 9102. Each ticket the till prints shows in its output as plain text and in
`dev/printouts/` (ignored by git):

```
python3 dev/fake-printer.py
```

The development settings already point the till server at them.

A round sent to the kitchen prints on the kitchen's printer; **Print receipt** on a paid bill
prints on the receipts' printer.

## Tests

```
dotnet test --solution Otantik.slnx
```

The integration tests need a PostgreSQL: set `OTANTIKPOS_TEST_DB` to a connection string with no
`Database=`. Without it they are skipped. With `dev/docker-compose.yml` running:

```
OTANTIKPOS_TEST_DB="Host=localhost;Port=5434;Username=otantik;Password=otantik-dev" dotnet test --solution Otantik.slnx
```

The till app's tests: `npx ng test --watch=false` in `otantik-pos/node-frontend`.
