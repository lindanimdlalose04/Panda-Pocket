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

| Service  | URL                     | Credentials                     |
|----------|-------------------------|---------------------------------|
| Postgres | `localhost:5432`        | see `infra/postgres/init.sql`   |
| MongoDB  | `localhost:27017`       | `rate_svc` / `rate_pw_dev`      |
| Seq      | http://localhost:5341   | none (auth disabled, see compose) |

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
`%LOCALAPPDATA%\Programs\pgAdmin 4untime\psql.exe`, which the verification
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

## Design notes

The decisions worth defending out loud are recorded in `CLAUDE.md`: why the
rate feed is a local simulator rather than an external API, why `/cancel` is an
action endpoint rather than a `PATCH`, why three different call types get three
different resilience strategies, and why one Postgres container with three
roles is an honest trade rather than a shortcut.
"# Panda-Pocket" 
