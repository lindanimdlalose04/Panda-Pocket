# Panda Pocket

A merchant crypto payment gateway. A South African business accepts payment in
crypto and receives ZAR. The merchant never holds crypto and never carries
exchange rate risk.

Built for ITRI 623 (Databases 2), North-West University.

## Running it

Prerequisites: .NET 8 SDK and Docker Desktop. Nothing else.

```bash
docker compose up -d
```

That brings up the three backing stores. Application services are added to the
compose file as they are built.

| Service     | URL                     | Notes                              |
|-------------|-------------------------|------------------------------------|
| **Client**  | http://localhost:5000   | start here                         |
| Gateway     | http://localhost:5000   | Ocelot, the only public entry point |
| Invoice API | http://localhost:5002   | `/swagger` for the API docs        |
| Rate API    | http://localhost:5003   | `/swagger` for the API docs        |
| Seq         | http://localhost:5341   | logs, auth disabled, see compose   |
| Postgres    | `localhost:5432`        | see `infra/postgres/init.sql`      |
| MongoDB     | `localhost:27018`       | `rate_svc` / `rate_pw_dev`         |

Open **http://localhost:5000** and the client is there: create an invoice, watch
the countdown, pay it, underpay it, replay a transaction. Every call it makes
goes through the gateway, never directly to a service.

MongoDB is published on **27018**, not the usual 27017, because a local MongoDB
service on the development machine already holds that port and a host process
would otherwise reach the wrong server. Inside the compose network it is still
`mongo:27017`.

### If Docker will not start

```powershell
./infra/fix-docker-sockets.ps1
```

Docker Desktop leaves orphaned socket files behind when it does not shut down
cleanly, and then refuses to start, naming a different socket each attempt.
Windows cannot delete those files. The script clears them all in one pass and
restarts the engine. Worth running before the demo.

Then build:

```bash
dotnet build PandaPocket.sln
```

`global.json` pins the SDK to 8.0.x. Without it a machine with .NET 10
installed silently builds against the newer SDK.

## Inspecting the databases

pgAdmin 4 connects to the containerised Postgres like any other server. Register
one connection per service role, since no role can see another's database:

| Field    | Value                                                |
|----------|------------------------------------------------------|
| Host     | `localhost`                                          |
| Port     | `5432`                                               |
| Database | `merchant_db` / `invoice_db` / `settlement_db`       |
| Username | `merchant_svc` / `invoice_svc` / `settlement_svc`    |
| Password | see `infra/postgres/init.sql`                        |

Set **Maintenance database** to the same value as Database. pgAdmin defaults it
to `postgres`, which a service role cannot connect to, and the resulting error
looks like a bad password rather than the isolation working as designed.

pgAdmin bundles its own `psql` at
`%LOCALAPPDATA%\Programs\pgAdmin 4
untime\psql.exe`, which the verification
script finds automatically.

## Proving database isolation

One Postgres container hosts three databases with one login role each. The
isolation is enforced at role level and is meant to be demonstrated, not
asserted:

```bash
bash infra/verify-isolation.sh
```

or on Windows:

```powershell
./infra/verify-isolation.ps1
```

Each role connects to exactly one database and is refused the other two, and
the script asserts that rather than leaving it to the reader. The refusals are
the evidence:

```
FATAL:  permission denied for database "merchant_db"
DETAIL:  User does not have CONNECT privilege.
```

The script prefers a real TCP connection from the host and falls back to
`docker exec`. Prefer the TCP path when demonstrating: the error names the
CONNECT privilege explicitly, and a client crossing the network boundary is a
more honest demonstration than one already inside the container.

## Layout

```
PandaPocket.sln
├─ src/
│  ├─ Gateway/                Ocelot, routing, API key auth, rate limiting
│  ├─ Services/
│  │  ├─ Merchant.Api/        accounts, hashed API keys, webhook config
│  │  ├─ Invoice.Api/         the payment lifecycle and state machine
│  │  ├─ Rate.Api/            ZAR quotes and tick history
│  │  └─ Settlement.Api/      ZAR ledger, fees, webhook dispatch
│  ├─ Shared/Contracts/       DTOs, SOC event schema, correlation headers
│  └─ client/                 plain HTML plus fetch, no build step
├─ infra/postgres/init.sql    database and role bootstrap
├─ requests/                  .http files, doubling as API documentation
├─ docs/                      diagrams and report
└─ docker-compose.yml
```

## The payment flow

```bash
curl -X POST http://localhost:5000/api/invoices   -H "Content-Type: application/json"   -H "X-Correlation-Id: my-trace-001"   -d '{"amountZar":250,"reference":"COFFEE-001","asset":"BTC"}'
```

The invoice comes back with a rate locked for fifteen minutes. That lock is the
product: the merchant is quoted R250 and receives R250 whatever the price does,
because the platform carries the risk rather than the shop.

Then filter Seq by `CorrelationId = 'my-trace-001'` and one request is visible
crossing Gateway, Invoice and Rate in order, with its SOC events alongside.

`requests/invoice.http` has the full set with commentary, including the status
codes worth knowing about:

| Code | Meaning |
|------|---------|
| 409  | Duplicate transaction hash, or a payment against a terminal invoice |
| 410  | Payment against an expired invoice: fetch a fresh one, do not retry |
| 422  | Underpayment: stored, invoice can still be topped up |
| 503  | Rate unavailable and no cached fallback |

## The Rate service

```bash
curl http://localhost:5003/api/rates/BTCZAR
curl http://localhost:5003/health
```

Swagger UI is at http://localhost:5003/swagger, and `requests/rate.http` holds
the full set of calls with commentary.

Prices come from a local geometric Brownian motion simulator rather than an
external exchange, so a demo cannot be broken by a third party outage or rate
limit, while still being a genuine dependency for the day 6 circuit breaker.
Each pair has its own drift and volatility, so USDTZAR behaves like the
stablecoin it is while BTCZAR wanders.

Worth demonstrating: stop MongoDB and the service degrades rather than failing.

```bash
docker compose stop mongo
curl http://localhost:5003/api/rates/BTCZAR   # 200, served from memory
curl http://localhost:5003/health             # 503, names mongodb as the failure
curl http://localhost:5003/api/rates/BTCZAR/history  # 503 with Retry-After
docker compose start mongo                    # recovers on its own, no restart
```

## Design notes

The decisions worth defending out loud are recorded in `CLAUDE.md`: why the
rate feed is a local simulator rather than an external API, why `/cancel` is an
action endpoint rather than a `PATCH`, why three different call types get three
different resilience strategies, and why one Postgres container with three
roles is an honest trade rather than a shortcut.
"# Panda-Pocket" 
