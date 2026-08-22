# Panda Pocket

## What this is

Panda Pocket is a merchant crypto payment gateway. A South African business accepts payment in crypto and receives ZAR in its account. The merchant never holds crypto and never carries exchange rate risk. The platform takes a percentage fee per transaction.

It is built as a microservices system for ITRI 623 (Databases 2) at North-West University. The demo and technical report are due the week of 31 August 2026.

## Constraints that shape every decision

This is a graded coursework artefact, not a startup. The marking weights are:

| Criterion | Weight |
|---|---|
| Functional application (working business flow from a basic client) | 20% |
| Endpoint design (simple, consistent, documented) | 15% |
| API gateway (routing plus real configuration) | 15% |
| Deployment (independently containerised services) | 15% |
| Service registry / discovery (working and explained) | 10% |
| Microservices patterns (at least two, implemented and explained) | 10% |
| Preparation for a future SOC and knowledge graph layer | 5% |

Ninety percent of the marks are for infrastructure and API design. Domain sophistication is worth almost nothing directly. **Favour finishing over elaborating.**

A later phase of this module extends the system with a Security Operations Centre layer and a Neo4j knowledge graph. The data model and logging must anticipate that now.

## Stack

- .NET 8, minimal APIs (not controllers, for speed)
- Ocelot API Gateway
- PostgreSQL via EF Core with Npgsql, for Merchant, Invoice and Settlement
- MongoDB for Rate tick history
- Serilog writing to Seq for centralised logging
- `Microsoft.Extensions.Http.Resilience` for retry and circuit breaker
- Swashbuckle for OpenAPI
- Docker Compose for local deployment
- Plain HTML plus fetch for the client. No framework, no build step.

## Architecture

One API Gateway and four independently deployable services, all on a single Docker Compose network.

```
Merchant system (client)
        |
   API gateway (Ocelot)          routing, API key auth, rate limiting, QoS
        |
   +----+-------------+---------------+
   |            |             |            |
Merchant     Invoice     Settlement      Rate
   |            |             |            |
merchant_db  invoice_db  settlement_db   MongoDB
   \____________|_____________/
     one Postgres container,
     one database and login role per service

All four services write structured logs to Seq.
```

### Service responsibilities

**Merchant** owns business accounts, hashed API keys, webhook URLs and the per-merchant fee percentage. It issues and revokes keys and validates them for other callers.

**Invoice** owns the payment lifecycle. This is the heart of the system. It creates invoices, locks a rate at creation time, accepts payment confirmations, and drives the state machine.

**Rate** provides ZAR conversion quotes and stores tick history. Prices come from a local geometric Brownian motion simulator, not an external API. This is deliberate: an external dependency that fails during a live demo is an unacceptable risk, and a local simulator is still a genuine dependency for circuit breaker purposes.

**Settlement** owns the merchant ZAR ledger, the platform's fee income, and webhook dispatch with retries.

## Data model

### merchant_db

- `merchants` — id, business_name, email, fee_percent, webhook_url, webhook_secret, status, created_at
- `api_keys` — id, merchant_id, **key_hash**, key_prefix, label, created_at, revoked_at, last_used_at
- `users` — id, merchant_id, email, password_hash, role, created_at

Store only the hash of an API key. Return the plaintext key exactly once on creation, and show only the prefix (`pk_live_a3f2…`) thereafter.

### invoice_db

- `invoices` — id, merchant_id, reference, amount_zar, asset, locked_rate, crypto_amount, pay_to_address, status, expires_at, created_at
- `payments` — id, invoice_id, **tx_hash UNIQUE**, amount_crypto, received_at
- `invoice_status_history` — id, invoice_id, from_status, to_status, reason, correlation_id, created_at

The unique constraint on `tx_hash` is the idempotency guard and the replay detector in one line of DDL. `invoice_status_history` is the audit trail, the SOC event source, and the future graph edge source. Build it from day one.

### rate_db (MongoDB)

One `ticks` collection: `{ pair, rate, source, ts }`, compound index on `{ pair: 1, ts: -1 }`.

### settlement_db

- `ledger_entries` — id, merchant_id, invoice_id, entry_type (CREDIT / FEE / PAYOUT), amount_zar, balance_after, correlation_id, created_at
- `merchant_balances` — merchant_id, available_zar, updated_at
- `webhook_deliveries` — id, merchant_id, invoice_id, url, payload, attempt_count, status, last_error, next_attempt_at, created_at

## Database isolation

One Postgres container hosts three databases, each with its own login role. This is a deliberate trade of failure isolation for local resource cost, and the report says so openly. Logical isolation is enforced at the role level and must be provable:

```sql
CREATE DATABASE merchant_db;
CREATE DATABASE invoice_db;
CREATE DATABASE settlement_db;

CREATE ROLE merchant_svc   LOGIN PASSWORD '...';
CREATE ROLE invoice_svc    LOGIN PASSWORD '...';
CREATE ROLE settlement_svc LOGIN PASSWORD '...';

REVOKE CONNECT ON DATABASE merchant_db, invoice_db, settlement_db FROM PUBLIC;

GRANT CONNECT ON DATABASE merchant_db   TO merchant_svc;
GRANT CONNECT ON DATABASE invoice_db    TO invoice_svc;
GRANT CONNECT ON DATABASE settlement_db TO settlement_svc;
```

Postgres grants `CONNECT` to `PUBLIC` by default, so the revoke is mandatory. Connecting as `invoice_svc` and being refused `merchant_db` is a demo asset.

## Invoice state machine

```
            merchant cancels
  cancelled <--------------- pending ----------------> underpaid
                                |  |                      |
             timer elapsed      |  | amount matches       | top-up received
  expired <---------------------+  |                      |
                                   v                      |
                                 paid <-------------------+
                                   |
                                   | ledger written
                                   v
                                settled
```

`cancelled`, `expired` and `settled` are terminal. `underpaid` can also expire.

Every transition that is *not* in this diagram is a rejected request, and every rejection is both an HTTP error and a logged security event. That relationship drives the status code design.

## Endpoints

**Merchant**
```
POST   /api/merchants
GET    /api/merchants/{id}
PUT    /api/merchants/{id}
POST   /api/auth/login
POST   /api/merchants/{id}/api-keys
GET    /api/merchants/{id}/api-keys
DELETE /api/api-keys/{id}
POST   /api/internal/keys/validate
GET    /health
```

**Invoice**
```
POST   /api/invoices
GET    /api/invoices/{id}
GET    /api/invoices?merchantId=&status=&page=
POST   /api/invoices/{id}/payments
POST   /api/invoices/{id}/cancel
GET    /api/invoices/{id}/history
GET    /health
```

**Rate**
```
GET    /api/rates
GET    /api/rates/{pair}
GET    /api/rates/{pair}/history?from=&to=
GET    /health
```

**Settlement**
```
GET    /api/settlements/{merchantId}/balance
GET    /api/settlements/{merchantId}/ledger
POST   /api/settlements
GET    /api/settlements/webhooks?merchantId=
POST   /api/settlements/webhooks/{id}/retry
GET    /health
```

`/cancel` is an action endpoint rather than a `PATCH` on the status field. This is deliberate: a state transition is not a field edit, and exposing status as a writable field would let a client set any value it liked. Stripe uses the same pattern (`POST /v1/invoices/{id}/void`).

### Status codes

Use these deliberately. Differentiated codes read as design rather than defaults.

| Code | When |
|---|---|
| 201 | Invoice created |
| 401 | Missing or invalid API key |
| 403 | Merchant requesting another merchant's resource |
| 409 | Duplicate `tx_hash`, or payment against an already-settled invoice |
| 410 | Payment against an expired invoice |
| 422 | Underpayment |
| 429 | Gateway rate limit exceeded |
| 503 | Rate service unavailable and no cached fallback |

## The core flow

1. Merchant system calls `POST /api/invoices` through the gateway with `X-API-Key` and `{ amountZar, reference, asset }`.
2. Gateway authenticates the key, attaches `X-Correlation-Id`, routes to Invoice.
3. Invoice calls Rate for a quote. **Circuit breaker on this call, falling back to the last cached rate.**
4. Invoice computes the crypto amount, persists `pending` with `lockedRate` and `expiresAt = now + 15 min`, returns 201.
5. Customer pays. In this system that is `POST /api/invoices/{id}/payments` with a `txHash` and amount, standing in for a chain confirmation.
6. Invoice validates: still `pending`, not expired, amount within tolerance, `txHash` unseen.
7. On a match the invoice moves to `paid` and Invoice calls Settlement.
8. Settlement writes two ledger entries (merchant credit, platform fee), updates the balance.
9. Settlement dispatches an HMAC-signed webhook to the merchant URL. **Retry with exponential backoff on this call.**
10. Invoice moves to `settled`.

### Three call types, three resilience strategies

This distinction matters and belongs in the report:

- **Invoice to Rate** is on the critical path and must not block indefinitely. Circuit breaker with a cached fallback.
- **Invoice to Settlement** must not be lost. Retry.
- **Settlement to merchant webhook** is external and untrusted. Backoff plus dead-lettering into `webhook_deliveries`.

## Microservices patterns implemented

Two are required. Six come nearly free:

1. Database per service (with polyglot persistence, Postgres and MongoDB chosen per workload)
2. Circuit breaker (Invoice to Rate)
3. Retry with exponential backoff (webhook delivery)
4. Centralised logging with correlation IDs (Serilog to Seq)
5. Health checks that probe their database dependency
6. Token and API key authentication, validated at the gateway

## Correlation IDs

Generate `X-Correlation-Id` at the gateway, propagate it on every downstream call, and stamp it on every log line. Filtering Seq by one ID and watching a single payment cross four services is the logging demo. It is also what the future SOC layer needs to reconstruct a session.

## SOC event catalogue

Emit these as structured JSON. The schema chosen here becomes the graph ingest format in the next phase.

```
{ eventType, merchantId, invoiceId, correlationId, severity, timestamp, metadata }
```

Event types:

```
AUTH_FAILED
API_KEY_INVALID
RATE_LIMIT_EXCEEDED
INVOICE_CREATED
PAYMENT_CONFIRMED
PAYMENT_UNDERPAID
PAYMENT_ON_EXPIRED_INVOICE
PAYMENT_REPLAY_ATTEMPT
WEBHOOK_DELIVERY_FAILED
CIRCUIT_OPENED
MERCHANT_WEBHOOK_URL_CHANGED
```

`MERCHANT_WEBHOOK_URL_CHANGED` is included on purpose. Changing where payment notifications are sent is a classic account takeover indicator.

## Service discovery

Docker Compose DNS. Ocelot routes to `http://invoice-service:8080`; Docker's embedded DNS resolves the service name to the container IP on the compose network. This is explicitly permitted by the specification and must be explained rather than assumed.

Consul with health-based registration is a stretch goal for Day 6 if everything else is on schedule. Do not start with Consul.

## Repository layout

```
PandaPocket.sln
├─ src/
│  ├─ Gateway/                      Ocelot, ocelot.json, auth middleware
│  ├─ Services/
│  │  ├─ Merchant.Api/
│  │  ├─ Invoice.Api/
│  │  ├─ Rate.Api/
│  │  └─ Settlement.Api/
│  ├─ Shared/
│  │  └─ Contracts/                 DTOs, event types, correlation middleware
│  └─ client/                       plain HTML plus fetch
├─ infra/postgres/init.sql
├─ requests/                        .http files, doubling as API documentation
├─ docs/                            diagrams, report
└─ docker-compose.yml
```

## Build order

Nine days. Code freezes Friday 28 August. The last two days are report and video, not code.

**Day 1, Sat 22 Aug — tooling and skeleton.** Install .NET 8 SDK, Docker Desktop, Rider or VS Code with C# Dev Kit, DBeaver, draw.io desktop. Pull `postgres:16-alpine`, `mongo:7`, `datalust/seq:latest`. Create the solution and all five projects. Write `docker-compose.yml` with only Postgres, Mongo and Seq. Write `infra/postgres/init.sql`.
*Done when:* the three containers start, all three roles connect to their own database, and a cross-database connection is refused. Screenshot the refusal.

**Day 2, Sun 23 Aug — Rate service, complete.** Mongo connection, GBM price generator, three endpoints, `/health`, Swagger, Serilog to Seq, Dockerfile. Build the smallest service first because its shape gets copied three more times.
*Done when:* Rate runs in Docker and its logs appear in Seq at `localhost:5341`.

**Day 3, Mon 24 Aug — Invoice service and gateway.** EF Core, the three tables, `POST /api/invoices` calling Rate. Ocelot in front. Also throw together an ugly `index.html` with a create form and a list table.
*Done when:* the HTML page creates an invoice through the gateway. This is the first end-to-end moment and it must happen on Monday.

**Day 4, Tue 25 Aug — Merchant service and auth.** Accounts, hashed API keys, JWT login, key validation. Wire API key checking into the gateway.
*Done when:* an unauthenticated create returns 401 and an authenticated one succeeds.

**Day 5, Wed 26 Aug — Settlement and webhooks.** Ledger entries, balances, `POST /api/settlements` called from Invoice, webhook dispatcher with the deliveries table. Point one webhook at a deliberately broken URL.
*Done when:* a payment produces two ledger rows and a webhook delivery record with a climbing `attempt_count`.

**Day 6, Thu 27 Aug — patterns and observability.** No new features. Circuit breaker, correlation ID middleware, health checks that probe the database, Ocelot rate limiting returning 429, SOC event emission.
*Done when:* stopping the Rate container still allows invoice creation on a fallback rate, and `CIRCUIT_OPENED` appears in Seq.

**Day 7, Fri 28 Aug — client, seed data, freeze.** Three screens: create invoice, checkout page with countdown, merchant view with ledger and webhook log. Seed enough data that the demo does not look empty. **Code freezes tonight.** Anything broken gets cut, not fixed.

**Day 8, Sat 29 Aug — report and documentation.** Rebuild the three diagrams in draw.io. Export Swagger. Write the nine required report sections.

**Day 9, Sun 30 Aug — record and rehearse.** Demo the five-stage flow, then the failure demos: kill Rate for the circuit breaker, break a webhook URL for retries, attempt a cross-database connection for role isolation. Record twice, use the second take.

## Working rules

- **Commit at the end of every day.** The commit history is evidence of repeatable, independent deployment.
- **No refactoring after Day 6.** The urge to tidy peaks exactly when it can least be afforded.
- **If Day 3 slips, cut scope immediately.** Settlement becomes a stub that only writes ledger rows and the webhook dispatcher is dropped. Two patterns are required, and database-per-service plus circuit breaker already satisfies that.
- **Design before code.** Talk through the approach before generating implementation.
- **Explain trade-offs rather than hiding them.** Everything in this system has to be defensible out loud in a demo.
- This is coursework. The author must understand every line well enough to explain it under questioning. Prefer simple, readable implementations over clever ones.

## Diagrams needed

Three, rebuilt in draw.io, black and white, standard notation:

1. **System architecture** — client, gateway, four services, databases, Seq, container boundary.
2. **Payment happy path** — the five-stage flow, which doubles as the demo script.
3. **Invoice state machine** — statechart notation, filled initial marker, double-bordered terminal states, every transition labelled.
