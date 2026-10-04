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

You need the .NET 10 SDK, Node 22 and a PostgreSQL 15+.

- Till server: `dotnet run --project otantik-pos/backend/src/OtantikPos.Node.Api` (port 5080;
  development settings in `appsettings.Development.json`).
- Till app: `npm ci` and `npm start` in `otantik-pos/node-frontend` (port 4200, proxied to the
  server), or `npm run build` to build it into the server's `wwwroot`.
- Without a delivery system, `otantik-pos/backend/tools/OtantikPos.DevCloud` plays one on
  `127.0.0.1:5099`.

## Tests

```
dotnet test --solution Otantik.slnx
```

The integration tests need a PostgreSQL: set `OTANTIKPOS_TEST_DB` to a connection string with no
`Database=`. Without it they are skipped. The till app's tests: `npx ng test --watch=false` in
`otantik-pos/node-frontend`.
