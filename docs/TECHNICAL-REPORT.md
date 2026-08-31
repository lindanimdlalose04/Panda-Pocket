# Panda Pocket: a merchant crypto payment gateway

**Technical report**
ITRI 623, Databases 2
North-West University

---

## 1. Introduction

### 1.1 The problem

A South African coffee shop wants to accept Bitcoin. It does not want to own
Bitcoin. The owner has no interest in watching an exchange rate, no appetite for
holding an asset that can move ten percent overnight, and no way to pay staff in
satoshis.

Panda Pocket resolves that. A customer pays in crypto, the merchant receives
rand, and the merchant never touches a coin or carries a minute of exchange rate
risk. The platform takes roughly one percent per transaction, against the two
and a half to three and a half percent a card processor charges, and settles in
minutes rather than days with no possibility of a chargeback.

The commercial comparables are BitPay, Coinbase Commerce and, locally, Luno Pay.

### 1.2 The one decision that is the product

Everything else in this system exists to support a single line of code:

```csharp
LockedRate = quote.Rate,
```

The rate is fixed when the invoice is created and never recalculated. The
merchant is quoted R250 and receives R250 whatever Bitcoin does over the next
fifteen minutes, because the platform absorbs the movement rather than the shop.
That transfer of risk is what a merchant is actually buying.

### 1.3 What was built

Four independently deployable services behind an API gateway, with per-service
databases, on a single Docker Compose network:

| Service | Owns |
|---|---|
| Merchant | Business accounts, hashed API keys, dashboard authentication |
| Invoice | The payment lifecycle and its state machine |
| Rate | ZAR conversion quotes and tick history |
| Settlement | The merchant ZAR ledger, platform fee income, webhook delivery |

Nine containers, twenty six documented endpoints, three PostgreSQL databases and
one MongoDB database, a Consul service registry, eight microservices patterns,
and eleven security event types feeding a centralised log.

### 1.4 Constraints that shaped the work

This is a graded coursework artefact built in seven days, not a startup. Ninety
percent of the marks are for infrastructure and API design; domain sophistication
earns almost nothing directly. Every decision below was taken with that in mind,
and where a shortcut was taken this report says so rather than hiding it.

The full day-by-day record, including the bugs and what caused them, is in
`docs/BUILD_LOG.md`. Verification output is in `docs/evidence/`.

---

## 2. Functional application

### 2.1 The business flow

The system performs its complete business function from a browser client with no
framework and no build step.

1. A merchant's system POSTs to `/api/invoices` with an amount in rand, a
   reference and an asset, authenticated with an API key.
2. The gateway validates the key, stamps a correlation id, and routes.
3. Invoice asks Rate for a quote, fixes that rate on the invoice, computes the
   crypto amount, and persists the invoice as `Pending` with a fifteen minute
   expiry. It returns 201.
4. The customer pays. In this artefact that is an endpoint standing in for a
   blockchain confirmation.
5. Invoice validates the payment, moves to `Paid`, calls Settlement, which writes
   two ledger entries and queues a signed webhook. The invoice becomes `Settled`.

This is diagram 2, `docs/diagrams/02-payment-happy-path.drawio`.

### 2.2 The state machine

The invoice lifecycle is the heart of the system.

```
                 merchant cancels
  Cancelled  <---------------- Pending ----------------> Underpaid
                                 |  |                        |
              window elapsed     |  | payment within         | top-up
  Expired  <---------------------+  | tolerance              |
                                    v                        |
                                  Paid <--------------------+
                                    |
                                    | ledger written
                                    v
                                 Settled
```

`Cancelled`, `Expired` and `Settled` are terminal. Diagram 3 gives the full
version with the HTTP status attached to every transition.

Two properties matter more than the diagram itself.

**The rules live in exactly one place.** `InvoiceStatusRules` in
`Shared/Contracts` expresses the permitted transitions as a lookup table rather
than as `if` statements scattered through the service. It can be unit tested
without a database, it reads directly as the diagram, and it means "is this
transition legal" and "should this be a security event" are the same question
asked once.

**Every transition not in the diagram is a rejected request AND a logged security
event.** That relationship is what drives the status code design in section 3.

### 2.3 The two client screens

**Merchant view** at `/`. Create an invoice, watch the list with live
countdowns, read the ZAR ledger with its reconciliation proof, and inspect the
webhook delivery log with attempt counts.

**Customer checkout** at `/checkout.html?id=...`. What a shopper sees: the rand
amount, the crypto equivalent, the address to pay, and a countdown on the locked
rate.

The checkout is deliberately public and requires no API key. The person paying
is not the merchant and holds no credential; requiring one would mean putting
the merchant's key into a page the customer can read. The invoice id is the
bearer token, which is why invoice ids are version 4 GUIDs rather than
sequential integers: 122 bits of randomness make the link unguessable, so
holding it is the authorisation. BitPay and Coinbase Commerce use the same
approach. It follows that the checkout response is scoped to what a payer may
see, and deliberately excludes the merchant id and everything about the merchant
account.

### 2.4 Evidence

`docs/evidence/day3-end-to-end.txt` records the full lifecycle exercised through
the gateway. `docs/evidence/day7-freeze.txt` records the final seeded state:
eight invoices covering `Settled`, `Underpaid`, `Cancelled` and `Pending`, a
balance of R2 048.31, and a passing reconciliation.

---

## 3. Endpoint design

### 3.1 Twenty six endpoints across four services

The complete OpenAPI documents are exported to `docs/api/`, one per service,
generated by the running containers rather than written by hand.

```
Merchant     POST   /api/merchants                     register
             POST   /api/auth/login                    dashboard JWT
             GET    /api/merchants/{id}
             PUT    /api/merchants/{id}
             POST   /api/merchants/{id}/api-keys       issue, returned once
             GET    /api/merchants/{id}/api-keys       prefixes only
             DELETE /api/api-keys/{id}                 revoke
             POST   /api/internal/keys/validate        internal only
             GET    /api/internal/merchants/{id}       internal only

Invoice      POST   /api/invoices
             GET    /api/invoices/{id}
             GET    /api/invoices?status=&page=
             POST   /api/invoices/{id}/payments
             POST   /api/invoices/{id}/cancel
             GET    /api/invoices/{id}/history
             GET    /api/checkout/{id}                 public
             POST   /api/checkout/{id}/simulate-payment

Rate         GET    /api/rates
             GET    /api/rates/{pair}
             GET    /api/rates/{pair}/history

Settlement   POST   /api/settlements
             GET    /api/settlements/{merchantId}/balance
             GET    /api/settlements/{merchantId}/ledger
             GET    /api/settlements/{merchantId}/reconcile
             GET    /api/settlements/webhooks
             POST   /api/settlements/webhooks/{id}/retry
```

### 3.2 Actions are not field edits

`POST /api/invoices/{id}/cancel` is an action endpoint rather than a `PATCH` on
a status field. A state transition is not a field edit, and exposing status as
writable would let a client set any value it liked, including `Settled`, which
would amount to a client asserting it had been paid out. Stripe uses the same
shape with `POST /v1/invoices/{id}/void`.

### 3.3 Status codes chosen so the difference is actionable

Differentiated codes are only worth the effort if a client can act on the
difference. These can.

| Code | When | What the integrator should do |
|---|---|---|
| 201 | Invoice created | Redirect the customer to checkout |
| 400 | Validation failed | Fix the request; the offending fields are named |
| 401 | Missing or invalid API key | Check the credential |
| 403 | Another merchant's resource | Stop; this is not yours |
| 409 | Duplicate `tx_hash`, or payment against a terminal invoice | Stop, do not retry |
| 410 | Payment against an expired invoice | Request a fresh invoice |
| 422 | Underpayment | Wait; the customer still owes money |
| 429 | Rate limit exceeded | Back off, `Retry-After` says how long |
| 503 | Rate unavailable and no cached fallback | Retry shortly |

The 409 and 410 distinction is the sharpest example. Both mean "this payment was
not accepted", but 409 says stop for good and 410 says this particular invoice is
gone, ask for another. A merchant integration behaves differently in each case.

Validation failures return the specific fields rather than a generic message,
because an integrator debugging at two in the morning needs to know which field
was wrong.

### 3.4 Request collections as documentation

`requests/*.http` holds every call with commentary explaining why each endpoint
is shaped as it is. These are version controlled deliberately: they are
executable from Rider and VS Code, they double as the API documentation
deliverable, and unlike an exported Postman collection they cannot drift out of
the repository.

---

## 4. API gateway

### 4.1 Responsibilities

Ocelot sits in front of every service as the single public entry point. It
routes by path, authenticates API keys, enforces rate limits, mints the
correlation id that ties a request together across services, and serves both
client pages.

Ten routes are configured in `src/Gateway/ocelot.json`.

### 4.2 The gateway is the only source of merchant identity

This is the most important security property in the system, and it depends on
two things working together.

**First, the gateway strips any inbound `X-Merchant-Id` header before doing
anything else.** Without that step, a caller could simply send their own header
and act as any merchant they liked. Holding a valid API key of their own would
not help them be caught, because the key would be genuinely theirs and the header
would name somebody else.

**Second, the gateway then sets that header itself**, from the key it just
validated against the Merchant service, which owns that data. The gateway never
reads the merchant database directly.

Only after both steps can a downstream service trust the header. Demonstrated:

```
POST /api/invoices
X-API-Key: pk_live_demo0000...
X-Merchant-Id: 99999999-9999-9999-9999-999999999999   <- forged

-> merchantId in the response: 11111111-1111-1111-1111-111111111111
```

The forged header is discarded and the invoice belongs to the real merchant.

### 4.3 Two credential types, deliberately not interchangeable

**API keys** authenticate a merchant's server. Long lived, because a server
cannot retype a password. Sent as `X-API-Key`, validated at the gateway.

**JWTs** authenticate a person on the dashboard. Short lived, because a browser
session should not outlive the human holding it. Validated by the Merchant
service itself.

Key management sits behind the JWT and only the JWT. If an API key could manage
API keys, a leaked key could mint replacements for itself and revoke the real
ones, locking the merchant out of their own account.

### 4.4 Caching, and its honest cost

Validated keys are cached at the gateway for thirty seconds. Without it every
API call becomes two network hops and a database read; with it a burst from one
merchant costs one validation.

The cost is that a revoked key keeps working for up to thirty seconds. The
window is deliberately short for that reason. Failures are never cached, so
brute forcing gets no cheaper and a newly issued key never appears broken.

### 4.5 Rate limiting

Thirty invoice requests per minute per merchant, returning 429 with
`Retry-After`. Measured: forty rapid requests produced twenty seven allowed and
thirteen throttled.

The quota is keyed on `X-Merchant-Id`, which the gateway sets, so it is per
merchant rather than per key or per IP address. A merchant with three keys shares
one quota, which is the honest unit: the limit protects the platform from one
customer, not from one credential. Per-IP would lump every merchant behind a
corporate NAT into a single bucket.

Rates are exempt. They are public market data, a checkout page polls them
legitimately every few seconds, and nothing about a rate is merchant specific.

### 4.6 A gap, stated plainly

Unauthenticated requests are rejected by the API key middleware before Ocelot
ever sees them, so they carry no `X-Merchant-Id` and are never rate limited.
Someone brute forcing API keys is throttled by nothing at this layer.

Every such rejection is logged as `API_KEY_INVALID` with its source address, so
the SOC layer can see it, but detection is not prevention. Closing this properly
needs an IP-based limiter in front of authentication, which is the obvious next
piece of work.

---

## 5. Deployment

### 5.1 One command

```bash
docker compose up -d
```

Eight containers, each service independently built from its own Dockerfile,
sharing one Compose network. Verified from nothing: `docker compose down -v`
followed by `docker compose up -d` produced a fully working system in about a
minute, applying three EF Core migrations and seeding a demo merchant with no
manual step at any point. `infra/reset-demo.ps1` automates that and
`infra/seed-demo.sh` populates realistic data through the public API.

### 5.2 Container design

Every service uses the same two-stage Dockerfile, settled once on the Rate
service and copied. The SDK image restores and publishes; the ASP.NET runtime
image, roughly a fifth of the size, runs the result with no compiler or package
cache shipped. Services run as the non-root `app` user the .NET images provide.

Two details worth recording:

**The build context is the repository root, not the service directory**, because
each service references `Shared/Contracts`. Building from the service folder
fails during restore with an error that reads like a corrupt solution rather than
a context problem.

**Layer caching is deliberate.** Project files are copied and restored before the
source is copied, so editing a source file does not re-download every NuGet
package.

### 5.3 Migrations at startup

Each service applies its own EF Core migrations on start, with a retry loop
because a container reported healthy is not always immediately accepting
connections.

This is a deliberate trade. For a coursework artefact that must come up from a
clean clone with one command, an automatic migration is the difference between
`docker compose up` working and a marker having to run EF tooling by hand. A
production system would make migration a deliberate deployment step instead,
because automatic migration under multiple replicas is a race between them.

### 5.4 Health checks

Every service exposes `/health` that genuinely probes its database rather than
returning 200 because the process is alive. The interesting failure is the one
where a service is up and its database is not, and a check that cannot see that
is decorative.

The response names each check with its status and duration, so a failure says
which dependency is at fault.

Compose uses these for ordered startup: Invoice waits for Rate and Settlement to
be healthy, not merely started.

The Rate service's health check exposed a real problem. With MongoDB stopped, the
driver's default thirty second server selection timeout meant health took sixty
seconds to report a failure and the history endpoint hung outright. Timeouts were
lowered to three seconds. A health check that takes a minute to tell you
something is broken is not a health check.

### 5.5 Configuration across environments

Base values live in `appsettings.json` for local development; Compose overrides
them with environment variables using service names. The gateway keeps
`ocelot.json` as the deployed truth and `ocelot.Development.json` to repoint the
same routes at localhost.

The development routes are repeated in full rather than patched, because .NET
configuration merges JSON arrays by index. A partial override would silently
depend on both files keeping their routes in the same order, and the failure mode
would be a request quietly reaching the wrong service.

### 5.6 A deployment hazard worth recording

Docker Desktop failed to start on five of the seven build days on the
development machine. Unclean shutdown leaves orphaned AF_UNIX socket reparse
points that Windows cannot delete, and the Docker backend aborts because it
clears each socket before binding it. The socket named in the error varies.

`infra/fix-docker-sockets.ps1` finds every orphaned socket across Docker's
runtime directories, renames the containing directories (renaming works where
deleting does not, because it does not touch the children), and restarts the
engine.

Recorded because it is a genuine operational hazard rather than a curiosity, and
because it will recur.

---

## 6. Service registry and discovery

### 6.1 Mechanism

HashiCorp Consul, running as a container on the Compose network, with each
service registering itself.

On startup every service PUTs its own entry to Consul's agent API: a service
name, a unique instance id, the address and port other services should use, and
an HTTP health check pointing back at its own `/health`. On shutdown it
deregisters. Consul polls each health check every ten seconds and removes any
instance that stays critical for a minute.

Registration is self-service rather than a deployment step, because the instance
is the only thing that knows it has finished starting. It also means scaling a
service to three containers puts three instances in the catalogue with no
configuration change anywhere else.

### 6.2 What the catalogue holds

```
SERVICE              INSTANCE ID                    HEALTH
invoice-service      invoice-service-d8d9af58       passing
merchant-service     merchant-service-0c0e6620      passing
rate-service         rate-service-f62e5b52          passing
settlement-service   settlement-service-cb6256b8    passing

rate-service         -> rate-service:8080
merchant-service     -> merchant-service:8080
invoice-service      -> invoice-service:8080
settlement-service   -> settlement-service:8080
```

The UI is at `http://localhost:8500`.

### 6.3 The gateway resolves through the registry

Ocelot routes name a **service**, never a host or an IP:

```
/api/rates                    -> service rate-service
/api/invoices                 -> service invoice-service
/api/auth/{everything}        -> service merchant-service
/api/settlements/{everything} -> service settlement-service
```

Ten routes, none with a hardcoded downstream host. Ocelot asks Consul which
instances are registered under a name and healthy, then load balances across
them with round robin. With one instance that is a no-op; with three it is load
balancing for nothing extra.

### 6.4 Why a registry rather than DNS

An earlier version used Docker Compose DNS. It resolved names correctly and was
permitted, but it is name resolution and nothing more. The difference is visible
in one test:

```
docker compose stop settlement-service
  -> the instance deregisters itself and leaves the catalogue
  -> settlement routes stop resolving; every other route is unaffected

docker compose start settlement-service
  -> it re-registers with a NEW instance id, health check passing
  -> settlement routes resolve again, with no gateway restart and no config change
```

Compose DNS would have resolved `settlement-service` to a container whether or
not that container could serve a request. Consul returns only instances whose
health check is passing, so an instance whose database has gone away is taken
out of rotation by the registry rather than by a caller discovering it the hard
way.

### 6.5 One thing that had to be overridden, and why

Ocelot's bundled Consul provider builds the downstream address from the Consul
**node** name, falling back to the service address. That is correct in the
deployment Consul is normally run in, where every host runs its own agent and
the node name is the routable hostname of the machine an instance sits on.

This system has a single shared agent for the whole Compose network, so the node
name is the Consul container's own hostname. Every lookup resolved there and
every route answered 502:

```
Connection refused (266bb74f110f:8080)
```

`PandaPocketConsulServiceBuilder` overrides one virtual method to prefer
`Service.Address`, which is what each instance actually registers as its
reachable address.

The alternative was to register through Consul's catalogue API with a fabricated
per-service node, which would have produced correct addresses but given up
Consul's actively polled health checks. Those checks are the more valuable half
of running a registry at all, so the override was the better trade.

### 6.6 Honest limits

One Consul server in bootstrap mode, so the registry is itself a single point of
failure. A production deployment runs three or five servers in a Raft cluster,
and an agent on every host rather than one shared agent, which is also what would
make the override above unnecessary.

Services continue to run if Consul is unreachable at startup: registration
retries with backoff and then gives up rather than preventing the service from
serving requests. The gateway is the component that genuinely depends on Consul,
since it cannot resolve a route without it.

## 7. Microservices patterns

Two were required. Eight are implemented, and each is justified by what failure
costs rather than included to be counted.

### 7.1 Database per service, with polyglot persistence

Each service owns its data and no service reads another's tables. Settlement
needs the merchant's fee percentage, webhook URL and signing secret, and fetches
them from the Merchant service rather than copying them, because a copy goes
stale the moment a merchant changes their webhook.

**Polyglot on purpose.** Three services use PostgreSQL, because invoices,
ledgers and accounts are relational data with constraints worth enforcing. Rate
uses MongoDB, because tick history is append-only, schema-light and read by time
range. That is a workload-driven choice rather than variety for its own sake.

**Isolation is enforced and provable.** One Postgres container hosts three
databases, each with one login role, with `CONNECT` revoked from `PUBLIC`.
`infra/verify-isolation.sh` tries every role against every database and asserts
the outcome:

```
=== invoice_svc ===
  REFUSED    merchant_db     -> permission denied for database "merchant_db"
  CONNECTED  invoice_db      -> invoice_svc@invoice_db
  REFUSED    settlement_db   -> permission denied for database "settlement_db"

PASS: each role reached exactly its own database.
```

The trade is stated openly: one container means the three databases share a
failure domain, so this buys logical isolation and not failure isolation. It was
chosen for local resource cost. Separating them is a Compose change, not a code
change.

### 7.2 Circuit breaker with a cached fallback

Invoice to Rate sits on the critical path of creating an invoice. A merchant's
checkout must not hang waiting for a price lookup.

A breaker on its own would only convert a slow failure into a fast one, which
helps the system and does nothing for the merchant. The cached last-known-good
rate is what turns it into a genuine degradation: while Rate is down, invoices
are still created, priced from the last rate seen, and marked as such.

Measured with the Rate container stopped:

```
Cold start, no cached rate    -> HTTP 503, refuses to invent a price
Warm cache, six attempts      -> HTTP 201 x6, all on the cached rate

Circuit to rate-service OPENED for 15s after 50 % failures
Circuit to rate-service HALF-OPEN; probing with a single request
Circuit to rate-service CLOSED; the dependency is answering again
```

Three design points:

**Fallback use is written into the audit trail**, with the staleness: `Invoice
created on a cached rate, 53s old (rate-service unavailable)`. Months later it is
still possible to answer why an invoice was priced as it was.

**There is a staleness ceiling**, thirty minutes. Past that a stale rate stops
being a degradation and becomes a liability, because the platform would be
locking a merchant to a price that no longer reflects the market and absorbing
the difference at settlement.

**A 404 from Rate is not a fallback case.** That is Rate working correctly and
saying the pair does not exist, so the cache is deliberately not consulted.

### 7.3 Retry with exponential backoff and dead-lettering

Settlement to a merchant's webhook endpoint is the one dependency genuinely
outside our control. It can be down for deployment, slow, or behind a firewall
that drops packets silently.

The queue is a database table, not memory, so a restart resumes rather than
losing every pending notification. Measured backoff:

```
attempt 1/6 failed; next attempt in 3s
attempt 2/6 failed; next attempt in 6s
attempt 3/6 failed; next attempt in 12s
attempt 4/6 failed; next attempt in 25s
attempt 5/6 failed; next attempt in 46s
attempt 6/6 -> Failed, dead-lettered, CRITICAL event raised
```

**Jitter matters more than it looks.** Without it, a hundred deliveries that
failed together would retry together for ever, arriving as synchronised bursts
that are themselves a small denial of service against an endpoint already
struggling.

**Retries are bounded and the failure is kept.** Retrying for ever ties up
resources on an endpoint that may never return. After six attempts the row is
marked `Failed` and retained, so somebody can see what was never delivered and
requeue it once the merchant has fixed their side.

**Webhooks are HMAC-SHA256 signed**, over the timestamp and the raw body. The
timestamp is inside the signed material on purpose: signing only the body would
let anyone who ever captured a valid callback replay it verbatim for ever. A
test receiver at `/demo/webhook-sink` verifies signatures the way a real
integration should, and returns 401 rather than 200 to anything it cannot verify.

### 7.4 Retry against an idempotent endpoint

Invoice to Settlement is money. Losing it means a merchant was paid and never
credited, which is the worst failure this system has.

So it retries rather than failing fast, and the Settlement endpoint is idempotent
per invoice specifically to make retrying safe: a second call returns the
existing settlement rather than crediting twice. That is enforced by a unique
index on `(invoice_id, entry_type)` in the database, not by an application check,
because an application-level check races with itself under concurrent requests
and a unique index cannot.

A background sweeper picks up any invoice left in `Paid`, so a failure defers the
work rather than losing it.

### 7.5 Three call types, three strategies

The distinction is the point:

| Call | Strategy | Why |
|---|---|---|
| Invoice to Rate | Circuit breaker, cached fallback | Critical path. A stale rate beats a hung checkout. |
| Invoice to Settlement | Retry, idempotent endpoint, sweeper | It is money. It must not be lost. |
| Settlement to merchant | Durable queue, backoff, dead letter | External, untrusted, must not be hammered. |

### 7.6 The remaining four

**Centralised logging with correlation ids.** One id is minted at the gateway and
propagated on every downstream call. Filtering Seq by a single id reconstructs a
payment across Gateway, Invoice, Rate and Settlement in order.

**Health checks that probe their dependency**, section 5.4.

**API key and JWT authentication at the gateway**, section 4.

**Rate limiting at the gateway**, section 4.5.

---

## 8. Preparing for a SOC and knowledge graph layer

### 8.1 The event catalogue

Eleven security event types are emitted as structured JSON, all verified present
in Seq:

```
AUTH_FAILED                    API_KEY_INVALID
RATE_LIMIT_EXCEEDED            INVOICE_CREATED
PAYMENT_CONFIRMED              PAYMENT_UNDERPAID
PAYMENT_ON_EXPIRED_INVOICE     PAYMENT_REPLAY_ATTEMPT
WEBHOOK_DELIVERY_FAILED        CIRCUIT_OPENED
MERCHANT_WEBHOOK_URL_CHANGED
```

The schema is fixed and flat, because it becomes the graph ingest format in the
next phase:

```json
{ "eventType", "severity", "correlationId", "timestamp",
  "merchantId", "invoiceId", "metadata" }
```

Event types are constants rather than strings at call sites, so the catalogue is
greppable and a typo cannot become a silently missing edge in the graph.

### 8.2 Why these events

`MERCHANT_WEBHOOK_URL_CHANGED` is in the catalogue on purpose. Repointing where
payment notifications are delivered is a classic account takeover step: take over
the account, change the webhook, and the real merchant stops hearing about
payments while the attacker starts.

`PAYMENT_REPLAY_ATTEMPT` comes from the unique constraint on `tx_hash`. That one
line of DDL is simultaneously the idempotency guard, so a duplicated confirmation
cannot credit a merchant twice, and the replay detector.

The traffic is machine traffic rather than human, which makes rate anomalies easy
to simulate on demand and easy to reason about.

### 8.3 The audit trail as a graph edge source

`invoice_status_history` records every state transition with `from_status`,
`to_status`, a reason, a correlation id and a timestamp, written in the same
transaction as the transition so the trail cannot drift from the invoice it
describes.

It is three things at once: the audit trail, the SOC event source, and the edge
source for a Neo4j layer, where each row becomes a timestamped edge carrying the
correlation id that links it to everything else in the same request.

It is indexed on `correlation_id` specifically so a future graph loader can read
by session.

Sweeper-driven expiries carry a **null** correlation id, which is deliberate and
honest: no caller's request caused them, time did.

### 8.4 Cross-merchant edges

The interesting graph structure is not within one merchant but across several.
One customer wallet address paying several merchants creates real cross-merchant
edges, which is how a fraudster testing stolen funds across shops would be
detected. The seeded demo includes three merchants so those edges exist rather
than being hypothetical.

### 8.5 What observability found

Auditing that every catalogue event actually reached Seq turned up
`PAYMENT_ON_EXPIRED_INVOICE` missing. Chasing it exposed a correctness bug, not a
logging gap: payments against expired invoices were returning 409 instead of the
specified 410, because the 410 branch only fired in the narrow window before the
expiry sweeper ran.

Every endpoint test had passed. The bug surfaced only from asking whether every
event in the catalogue was genuinely being emitted. That is an argument for
treating observability as a correctness tool rather than an afterthought.

---

## 9. Conclusion

### 9.1 What was achieved

A complete, working merchant crypto payment gateway: four containerised services
behind an API gateway, twenty six documented endpoints, per-service databases
with provable isolation, eight microservices patterns, and a security event
catalogue built for a SOC and knowledge graph layer that does not exist yet.

The system rebuilds from nothing with one command in about a minute, and
everything claimed here is backed by captured output in `docs/evidence/`.

### 9.2 What was traded away

**One Postgres container** hosting three databases. Logical isolation, not
failure isolation, chosen for local resource cost.

**A single Consul server rather than a Raft cluster**, and one shared agent
rather than an agent per host. The registry is therefore a single point of
failure, and the shared agent is what forced the address-resolution override in
section 6.5.

**A local price simulator rather than a real exchange feed.** Deliberate: an
external dependency that rate-limits or goes down during a live demo is an
unacceptable risk, and a local service is still a genuine dependency for circuit
breaker purposes.

**Migrations at startup**, which a production system would not do.

**Unauthenticated requests are not rate limited**, section 4.6.

**A committed JWT signing key and a fixed demo API key**, so the stack works from
a clean clone. Both are documented as development values.

### 9.3 What the bugs taught

Three are worth carrying forward.

**EF Core relationship fixup silently marked underpaid invoices as fully paid.**
Adding a payment to both the DbSet and the navigation collection double-counted
it, because EF appends to the collection itself. The failure was silent and
produced a valid-looking invoice that would have credited a merchant for money
never received. Caught only because each status code was tested individually
rather than assuming the happy path generalised.

**The circuit breaker defeated its own fallback.** The breaker opening throws
`BrokenCircuitException`, which does not derive from the `HttpRequestException`
the fallback was catching. So the fallback worked right up until the breaker
tripped and then stopped working at precisely the moment the breaker started
doing its job.

**Global gateway configuration is not a local change.** Adding rate limiting
silently broke every route without an explicit opt-out, and it survived a full
day because only the routes that had been changed were re-tested.

The common thread is that all three were invisible to the tests that existed and
visible only to a test that asked a different question.

### 9.4 Next steps

In order of value:

1. An IP-based rate limiter in front of authentication, closing the gap in 4.6.
2. A Consul cluster with an agent per host, removing the single point of failure
   and the need for the address override.
3. The SOC layer itself, consuming the event catalogue already being emitted.
4. The Neo4j knowledge graph, loading `invoice_status_history` as edges.
5. Separate Postgres containers, converting logical isolation into failure
   isolation.

The spin-offs that follow naturally from this base are recurring billing, a
payout API for gig workers, donation pages for NGOs, and cross-border merchant
settlement, where a Zimbabwean merchant sells to South African customers and is
paid in a currency that holds its value.

---

## Appendix A: repository map

```
PandaPocket.sln
├─ src/
│  ├─ Gateway/                  Ocelot, auth middleware, Consul discovery, client pages
│  ├─ Services/
│  │  ├─ Merchant.Api/          accounts, hashed keys, JWT
│  │  ├─ Invoice.Api/           lifecycle, state machine, checkout
│  │  ├─ Rate.Api/              GBM simulator, tick history
│  │  └─ Settlement.Api/        ledger, fees, webhook dispatch
│  └─ Shared/
│     ├─ Contracts/             DTOs, SOC schema, correlation middleware
│     └─ Persistence/           snake_case naming convention
├─ infra/
│  ├─ postgres/init.sql         databases, roles, CONNECT revoked
│  ├─ verify-isolation.sh/.ps1  the isolation proof, self-asserting
│  ├─ seed-demo.sh              demo data, through the public API
│  ├─ reset-demo.ps1            full teardown and rebuild
│  └─ fix-docker-sockets.ps1    Docker Desktop recovery
├─ requests/                    .http files, doubling as API documentation
├─ docs/
│  ├─ TECHNICAL-REPORT.md       this document
│  ├─ BUILD_LOG.md              day-by-day decisions and bugs
│  ├─ api/                      exported OpenAPI documents
│  ├─ diagrams/                 the three draw.io diagrams
│  └─ evidence/                 captured verification output
└─ docker-compose.yml
```

## Appendix B: evidence index

| File | Shows |
|---|---|
| `day1-isolation-proof.txt` | Each role reaches exactly its own database |
| `day2-rate-verification.txt` | Rate service, and graceful degradation with Mongo down |
| `day3-end-to-end.txt` | Full lifecycle through the gateway, correlation across services |
| `day4-authentication.txt` | 401 without a key, 201 with one, forged header discarded |
| `day5-settlement.txt` | Two ledger rows, reconciliation, webhook backoff and dead letter |
| `day6-patterns.txt` | Circuit breaker states, all eleven SOC events, rate limiting |
| `day7-freeze.txt` | Rebuild from nothing, every route, final seeded state |
| `service-registry-consul.txt` | Consul catalogue, self-registration, deregistration on shutdown |
