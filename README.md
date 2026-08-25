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

| Service  | URL                     | Credentials                       |
|----------|-------------------------|-----------------------------------|
| Postgres | `localhost:5432`        | see `infra/postgres/init.sql`     |
| MongoDB  | `localhost:27018`       | `rate_svc` / `rate_pw_dev`        |
| Seq      | http://localhost:5341   | none (auth disabled, see compose) |
| Rate API | http://localhost:5003   | none yet (API keys arrive day 4)  |

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
