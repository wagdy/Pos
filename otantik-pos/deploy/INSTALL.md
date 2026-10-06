# Installing the till at the restaurant

The till runs on one machine in the restaurant: a PC, a Mac or a small Linux box that stays on
during opening hours. The counter screen and the tablets open it in a browser over the
restaurant's network. It keeps working when the internet is down; orders, payments and the
kitchen carry on, and everything catches up with the delivery system when the internet returns.

It runs in Docker as three parts, started together:

- **till**: the till server, with the till app built in;
- **db**: the till's own database (PostgreSQL);
- **backup**: a copy of that database every day, kept 30 days.

## 1. What you need

- **The machine.** Any of Windows 10/11, macOS or Linux, with 8 GB of memory or more. Give it a
  fixed IP address on the restaurant network: in the router, reserve its address (a "DHCP
  reservation"). Turn off sleep, or it will stop answering the tablets.
- **Docker.**
  - Windows or macOS: install [Docker Desktop](https://www.docker.com/products/docker-desktop/).
    In its settings, turn on **Start Docker Desktop when you sign in**, and set the machine to
    sign in by itself after a restart.
  - Linux: install Docker Engine and the compose plugin, and enable the service
    (`sudo systemctl enable --now docker`).
- **A network customers do not use.** The till, the counter screen, the tablets and the printers
  go on the staff network only. If customers get Wi-Fi, give them the router's guest network,
  which cannot see the staff one. Anyone on the till's network can open its sign-in screen and
  try PINs, lock the staff out by entering wrong ones, and read what the tablets send: the till
  is reached over plain `http`, as nearly every till on a local network is.
- **The printers** (optional at first): network ESC/POS printers, one for the kitchen and one
  for receipts, each with a fixed IP address. They usually listen on port 9100.
- **This repository** on the machine: `git clone https://github.com/wagdy/Pos.git`, or a copy of
  the folder.

## 2. Settings

In `otantik-pos/deploy`, copy `.env.example` to `.env` and fill it in. Each setting is explained
there. Make each secret with:

```
docker run --rm alpine sh -c "head -c 48 /dev/urandom | base64"
```

- `DB_PASSWORD` and `TILL_SIGNING_KEY`: a new random value each.
- `BOOTSTRAP_MANAGER_PIN`: 4 to 8 digits, for the first sign-in (step 4).
- `DELIVERY_SYSTEM_NODE_KEY`: see step 5. It can stay empty for now.
- `KITCHEN_PRINTER_HOST` and `RECEIPT_PRINTER_HOST`: the printers' IP addresses.

`.env` holds secrets. Keep it on the till machine only.

## 3. Start it

From `otantik-pos/deploy`:

```
docker compose up -d --build
```

The first time takes several minutes: it builds the till from source. After that it starts with
the machine, by itself. Check it is running:

```
docker compose ps
```

All three should say `running` (the database also `healthy`). On the very first start, the
till's log (`docker compose logs till`) shows one `Failed executing DbCommand` line about
`__EFMigrationsHistory`: that is the empty database being set up, and happens only once.

## 4. First sign-in, and the staff's PINs

1. Find the machine's IP address: `ipconfig` on Windows, `ipconfig getifaddr en0` on a Mac,
   `hostname -I` on Linux.
2. On the counter screen or a tablet, open `http://<that address>:5080`. On the machine itself,
   `http://localhost:5080` works too. On Windows, allow Docker through the firewall if asked.
3. Sign in as **Manager** with `BOOTSTRAP_MANAGER_PIN`.
4. Open **Staff**. The cashiers, managers and admins made in the delivery system's Staff tab are
   listed once the till is connected (step 5). Give each a PIN with **Set PIN**. Captains do not
   use the till: they take table orders on their phones.
5. Once a real manager has a PIN, empty `BOOTSTRAP_MANAGER_PIN` in `.env` and run
   `docker compose up -d` again. The first **Manager** then stops working and leaves the
   sign-in screen, so the PIN typed into `.env` at setup opens nothing. It stays until a manager
   from the delivery system has a PIN, so the till is never left with no manager.

On a tablet, add the page to the home screen so it opens like an app.

## 5. Connect it to the delivery system

The till and the delivery system share one secret key.

1. Make a key (the command in step 2).
2. In Railway, on the **otantik-delivery** project's **api** service, add the variable
   `PosSync__NodeKey` with that key. Railway restarts the api with it.
3. Put the same key in `.env` as `DELIVERY_SYSTEM_NODE_KEY` and run `docker compose up -d`.

The cloud icon in the till's top bar turns on within a minute. The menu, prices, tax rate and
staff arrive by themselves, and keep themselves up to date every few minutes. From then on,
customers' points work at the till, the till's sales appear in the delivery system's reports,
and the captains can take table orders on their phones (`/tables` in the delivery app).

If the cloud icon stays off, see Problems below.

## 6. Printers

With `KITCHEN_PRINTER_HOST` and `RECEIPT_PRINTER_HOST` set, every round sent to the kitchen
prints there, and **Print receipt** on a paid bill prints its receipt. A print that cannot reach
its printer waits and prints when the printer is back; nothing is lost. While tickets are
waiting, every till's top bar says so in red, for example **Kitchen printer: 2 waiting**, so the
counter knows before the kitchen asks.

## 7. Backups

Every day the database is copied to `otantik-pos/deploy/backups`, and copies older than 30 days
are deleted. Copy that folder off the machine now and then (a USB stick, a cloud folder): a
backup on the same disk does not survive the disk. The copies hold customers' names and phone
numbers, so keep them somewhere private, not a shared or public folder.

To restore one:

```
docker compose stop till
docker compose exec -T db pg_restore -U otantik -d otantik_pos_node --clean --if-exists < backups/till-YYYY-MM-DD.dump
docker compose start till
```

The till comes back as it was on that day. Orders taken since then that had already reached the
delivery system come back by themselves when it reconnects; only what never reached it (taken
while the internet was down, after the backup) is lost.

## 8. Updating

```
git pull
docker compose pull db backup
docker compose build --pull
docker compose up -d
```

The two middle lines also fetch the latest of what the till is built on: operating-system security
fixes, PostgreSQL's fixes within version 16, and the time-zone rules the business day is counted
by, should Egypt change its summer time again. Without them, `up --build` keeps using what the
machine first downloaded. The database is updated by itself when the new till starts. The
tablets pick up the new app on their next reload.

## Problems

- **See what the till is doing:** `docker compose logs -f till`.
- **"The till server cannot be reached" on a tablet:** the tablet is not on the restaurant
  network, the machine is asleep or off, or its IP address changed (reserve it in the router).
- **The cloud icon is off:** the restaurant has no internet, or the key differs between `.env`
  and Railway. The till's log says "refused this till's key" when it is the key.
- **"has not received the tax rate":** the till has never reached the delivery system yet.
  Connect it once, or set `TAX_PERCENTAGE_UNTIL_SYNCED` for now.
- **Nothing prints** (the top bar says **Kitchen printer: … waiting**): check the printer is on,
  has paper and is plugged in, that its IP in `.env` is right, and that the till machine can
  reach it (`ping <printer IP>`). The waiting tickets print by themselves, in order, once it is
  back.
