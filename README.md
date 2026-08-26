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

| Service      | URL                     | Notes                               |
|--------------|-------------------------|-------------------------------------|
| **Client**   | http://localhost:5000   | start here                          |
| Gateway      | http://localhost:5000   | Ocelot, the only public entry point |
| Merchant API | http://localhost:5001   | `/swagger` for the API docs         |
| Invoice API  | http://localhost:5002   | `/swagger` for the API docs         |
| Settlement   | http://localhost:5004   | `/swagger` for the API docs         |
| Rate API     | http://localhost:5003   | `/swagger` for the API docs         |
| Seq          | http://localhost:5341   | logs, auth disabled, see compose    |
| Postgres     | `localhost:5432`        | see `infra/postgres/init.sql`       |
| MongoDB      | `localhost:27018`       | `rate_svc` / `rate_pw_dev`          |

### Seeded demo account

Created automatically on first start so the stack is usable immediately:

| | |
|---|---|
| Dashboard login | `owner@democoffee.co.za` / `demo-password-123` |
| API key | `pk_live_demo0000000000000000000000000000000000` |

That key is a fixed seed value, and the only one in the system that is not 256
bits from a cryptographic RNG. It exists so the client works from a clean clone
with nothing to configure. Every key, including this one, is stored only as a
SHA-256 hash.

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

## Authentication

Two credential types, deliberately not interchangeable.

**API keys** authenticate a merchant's *server* calling the invoice API. Long
lived, because a server cannot retype a password. Sent as `X-API-Key`.

**JWTs** authenticate a *person* on the dashboard. Short lived, because a browser
session should not outlive the human holding it. Sent as `Authorization: Bearer`.

Key management sits behind the JWT and only the JWT. If an API key could manage
API keys, a leaked key could mint replacements for itself and revoke the real
ones, and the merchant would have no way back in.

### How the gateway makes identity trustworthy

Services downstream read the merchant id from `X-Merchant-Id`. Two things make
that safe, and both are required:

1. The gateway **strips** any inbound `X-Merchant-Id` before doing anything
   else. Without this, a caller could send their own and act as any merchant,
   and holding a valid key of their own would not help them be caught.
2. The gateway then sets it from the API key it just validated against the
   Merchant service, which owns that data. The gateway never reads the merchant
   database itself.

Try it. The forged header below is discarded and the invoice belongs to the demo
merchant, not to the GUID asserted:

```bash
curl -X POST http://localhost:5000/api/invoices   -H "Content-Type: application/json"   -H "X-API-Key: pk_live_demo0000000000000000000000000000000000"   -H "X-Merchant-Id: 99999999-9999-9999-9999-999999999999"   -d '{"amountZar":99,"reference":"FORGED-1","asset":"BTC"}'
```

Validated keys are cached for 30 seconds, so a burst of traffic costs one
validation rather than one per request. The trade is that a revoked key keeps
working for up to that long, which is why the window is short. Failures are never
cached, so brute forcing gets no cheaper.

Rates are public on purpose: a checkout page must show a price before anyone has
authenticated, and nothing about a rate is merchant-specific.

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

## Resilience: what happens when things break

Three service-to-service calls, three different strategies, each chosen from what
failure costs:

| Call | Strategy | Why |
|---|---|---|
| Invoice to Rate | Circuit breaker with a cached fallback | Critical path. A slightly stale rate beats a checkout that hangs. |
| Invoice to Settlement | Retry against an idempotent endpoint, plus a sweeper | It is money. Losing it means a merchant was paid and never credited. |
| Settlement to merchant | Durable queue, backoff with jitter, dead letter | External and untrusted. Cannot be fixed by us, must not be hammered. |

### Watching the circuit breaker

```bash
curl -X POST http://localhost:5000/api/invoices   -H "Content-Type: application/json"   -H "X-API-Key: pk_live_demo0000000000000000000000000000000000"   -d '{"amountZar":250,"reference":"WARM-1","asset":"BTC"}'

docker compose stop rate-service
```

Now create another invoice. It still returns **201**, priced from the last rate
seen, and the audit trail records that:

```bash
curl http://localhost:5000/api/invoices/{id}/history   -H "X-API-Key: pk_live_demo0000000000000000000000000000000000"

# (new) -> Pending | Invoice created on a cached rate, 53s old (rate-service unavailable)
```

Filter Seq by `EventType = 'CIRCUIT_OPENED'` and the breaker's state is visible in
the `reason` field: `HttpRequestException` means the breaker was closed and the
call failed, `BrokenCircuitException` means it was open and the call was rejected
without being attempted.

`docker compose start rate-service` and the breaker half-opens, probes, and
closes on its own.

A cold start with Rate already down returns **503**, on purpose: there is no
cached rate to fall back on, and inventing a price would be worse than refusing.

### Rate limiting

Thirty invoice requests per minute per merchant. The 31st gets **429** with a
`Retry-After` header, and raises `RATE_LIMIT_EXCEEDED` in Seq.

The quota is keyed on the merchant, not the API key or the IP, so a merchant with
three keys shares one quota. Rates are exempt: a checkout page polls them
legitimately.

## Settlement, the ledger and webhooks

Paying an invoice credits the merchant and queues a signed notification.

```bash
curl "http://localhost:5000/api/settlements/11111111-1111-1111-1111-111111111111/ledger"   -H "X-API-Key: pk_live_demo0000000000000000000000000000000000"
```

One R250 invoice writes **two** entries, not one net entry:

```
Credit  +250.00   balance 250.00
Fee       -2.50   balance 247.50
```

"You were paid R250 and we took R2.50" is a statement a merchant can check. A
single R247.50 line hides where the difference went.

`merchant_balances` is a cache of the ledger, and `/reconcile` proves it has not
drifted by recomputing the sum and comparing.

### Watching the retry pattern

The seeded merchant's webhook points at `http://localhost:9999`, which is
deliberately unreachable. Pay an invoice, then watch the attempts climb:

```bash
curl "http://localhost:5000/api/settlements/webhooks?merchantId=11111111-1111-1111-1111-111111111111"   -H "X-API-Key: pk_live_demo0000000000000000000000000000000000"
```

Backoff runs at roughly 3s, 6s, 12s, 25s, 45s, each with jitter and capped, then
the delivery dead-letters as `Failed` with a CRITICAL security event. It stays in
the table rather than disappearing, and can be requeued.

The jitter matters: without it, deliveries that failed together would retry
together for ever, arriving as synchronised bursts.

### Watching a delivery succeed

`/demo/webhook-sink` is a test receiver standing in for a merchant's server. It
verifies the HMAC signature the way a real integration should, and returns 401 to
anything it cannot verify. Point the merchant at it and pay an invoice:

```bash
curl "http://localhost:5000/demo/webhook-sink"
```

Expect `"verdict": "verified"` on attempt 1. Full walkthrough in
`requests/settlement.http`.

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
