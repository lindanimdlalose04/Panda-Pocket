# Panda Pocket

A merchant crypto payment gateway. A South African business accepts payment in
crypto and receives ZAR. The merchant never holds crypto and never carries
exchange rate risk.

Built for ITRI 623 (Databases 2), North-West University.

It is built in two halves. The payment gateway itself, and a security layer on
top of it that records what every service does, detects patterns in that record,
and answers questions about it from a Neo4j knowledge graph. If you came here
for the retrieval-augmented part, jump to [The security layer](#the-security-layer-soc-knowledge-graph-and-rag).

## Running it

Prerequisites: .NET 8 SDK and Docker Desktop. Nothing else.

```bash
docker compose up -d
```

That brings up all twelve containers.

One extra prerequisite for the question answering only: [Ollama](https://ollama.com)
running on the host, with `ollama pull mxbai-embed-large` and `ollama pull llama3.2`.
Everything else works without it.

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
| **SOC console** | http://localhost:5000/soc.html | alerts, rules and events     |
| **Ask the graph** | http://localhost:5000/ask.html | questions, with evidence   |
| SOC API      | http://localhost:5005   | `/swagger`, events and alerts       |
| Graph API    | http://localhost:5006   | `/swagger`, the graph and the RAG   |
| Neo4j        | http://localhost:7474   | Browser. Bolt on 7687, `neo4j` / `panda_graph_dev` |

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

Open **http://localhost:5000** and the merchant view is there: create an invoice,
watch the countdown, read the ZAR ledger, inspect the webhook delivery log. Every
call it makes goes through the gateway, never directly to a service.

### Seeding a demo

```powershell
.\infra\seed-demo.ps1
```

On Linux or macOS, use `bash infra/seed-demo.sh` instead.

Creates three merchants and eight invoices covering every state the machine can
reach, including two left pending with checkout links printed. It works through
the public API rather than the database, so it doubles as an end-to-end test.

To start completely fresh, `./infra/reset-demo.ps1` removes the volumes, rebuilds
and reseeds. It prompts first, because it is destructive.

### The two screens

| | |
|---|---|
| **Merchant view** | http://localhost:5000 |
| **Customer checkout** | http://localhost:5000/checkout.html?id=... |

The checkout page is what a merchant sends their customer: the rand amount, the
crypto equivalent, the address to pay, and a live countdown on the locked rate.

It is **public and needs no API key**, which is deliberate. The person paying is
not the merchant and holds no credential; requiring one would mean putting the
merchant's key in a page the customer can read. The invoice id is the bearer
token, which is why it is a random GUID rather than a sequential number, and the
response is scoped to what a payer may see: no merchant id, nothing about the
merchant's account.

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
│  │  ├─ Settlement.Api/      ZAR ledger, fees, webhook dispatch
│  │  ├─ Soc.Api/             security events, detection rules, alerts
│  │  └─ Graph.Api/           the knowledge graph and the RAG
│  ├─ Shared/Contracts/       DTOs, SOC event schema, correlation headers
│  └─ Gateway/wwwroot/        plain HTML plus fetch, no build step
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

## The security layer: SOC, knowledge graph and RAG

The payment gateway above is the thing being watched. This half is the watching.

Three steps. Every service **records** what it does as a security event. A rule
engine **detects** patterns across those events and raises alerts. A knowledge
graph **connects** those alerts to published security knowledge, and a retrieval
layer answers questions about them in English, with the evidence attached.

The design rule that shapes all of it: the security layer is downstream of
everything and upstream of nothing. No payment service ever calls it, so it can
never delay or block a payment.

### Seeing it work

```powershell
.\infra\reset-for-demo.ps1      # clears the evidence, keeps the payment data
.\infra\simulate-attacks.ps1    # about two minutes
```

The simulator attacks the running system through its public interface: wrong API
keys, guessed checkout links, failed logins followed by a payout change, a real
outage of the rate service, and a flood past the rate limit. Nothing is inserted
into the database. Open http://localhost:5000/soc.html and six alerts appear,
one per rule, each one traceable to the events behind it.

Then build the graph from what just happened and ask it something:

```bash
curl -X POST http://localhost:5006/api/graph/load/all
```

Open http://localhost:5000/ask.html and try *What is the impact if the rate
service fails?*, then *How do I tell whether someone actually got in?*. Those two
questions are answered by different retrieval routes, which is the whole reason
there is more than one.

### Where the code is

Worth reading in this order.

| Step | File |
|---|---|
| An event is written | `src/Services/Invoice.Api/Domain/InvoiceService.cs` |
| It is queued and shipped | `src/Shared/Contracts/Soc/SocEventPublisher.cs` |
| It is received and stored | `src/Services/Soc.Api/Endpoints/SocEndpoints.cs` |
| A rule turns events into an alert | `src/Services/Soc.Api/Domain/Detection/DetectionRules.cs` |
| The rule engine and suppression | `src/Services/Soc.Api/Domain/Detection/RuleEngine.cs` |
| Neo4j connection and schema | `src/Services/Graph.Api/Program.cs`, `Graph/GraphSchema.cs` |
| The three graph sources | `Graph/TopologyLoader.cs`, `Graph/SocLoader.cs`, `Graph/KnowledgeSeed.cs` |
| **Retrieval, the three routes** | `src/Services/Graph.Api/Rag/Retriever.cs` |
| **The evidence envelope** | `src/Services/Graph.Api/Rag/EvidenceEnvelope.cs` |
| Retrieve then generate | `src/Services/Graph.Api/Rag/AnswerService.cs` |
| How it was scored | `src/Services/Graph.Api/Rag/Evaluation/` |

### What the graph is built from

Three sources, and two of them are read from the running system rather than
typed by hand.

**The system's own structure.** Services and their health come from the Consul
registry. Endpoints come from the gateway's own routing file, mounted read-only
into the container, so the graph cannot describe a route the gateway does not
have. Stop a service and the next load records it as absent.

**The security events and alerts**, read from the SOC service over HTTP. The
graph has no access to anyone's database.

**The security knowledge**, which is the one part a human wrote: six threats,
four CWE references, six controls and three written response procedures. MITRE
and OWASP publish what credential stuffing is; they know nothing about this
system, so somebody has to connect the two. Each control records whether this
system actually has it, which is how the graph can say what is *not* implemented.

Order matters when loading. The knowledge joins alerts to the threats they
indicate through the rules that detect them, so it has to load after the rules
and alerts exist. Use `/load/all`, which does them in the right order.

### The retrieval, honestly described

Three routes, because each one answers questions the others cannot.

**Cypher** matches a question against five known shapes and runs a prepared graph
query. Exact and fast, and useless for anything it does not recognise.

**Vector search** embeds the question with `mxbai-embed-large` and compares it
against the procedure text, chunked by paragraph and indexed by a Neo4j vector
index at 1024 dimensions with cosine similarity. This is what finds the right
paragraph for *how do I tell whether someone actually got in*, which shares
almost no words with the text that answers it.

**Hybrid** uses the vector search to find a starting point, then walks the typed
relationships out from there to the threat, the controls and any alert this
system actually raised.

Measured over eight questions whose correct answers were written down before
anything ran:

| Route | Evidence recall | Latency | Fabricated citations |
|---|---|---|---|
| Cypher | 56% | 23 ms | 0 |
| Vector | 29% | 263 ms | 0 |
| Hybrid | **64%** | 313 ms | 0 |

That last column is the one that matters. Every identifier in every answer is
checked against the graph, so a citation to something that does not exist would
be caught. There were none.

**Generation is the smaller half, and the template is the default.** The answer
is assembled from the retrieved evidence by a template rather than written by a
language model. Both are implemented and both were measured on the same question
with the same evidence: the template finished in 37 ms with all three sections,
`llama3.2` took 107 seconds and silently dropped two of them. The model is still
there behind `"useModel": true`, and it answers through the graph service
directly because it takes longer than the gateway will wait:

```bash
curl -X POST http://localhost:5006/api/graph/ask \
  -H "Content-Type: application/json" \
  -d '{"question":"Why was the critical alert raised?","useModel":true}'
```

A template cannot hallucinate, because it has no way to produce a sentence that
was not assembled from retrieved facts. That is the trade: fluency for
guaranteed grounding. On better hardware the balance might go the other way, and
the report says so.

### Every answer carries its evidence

Retrieval decides what is true; generation only decides how to say it. So every
answer comes back with the envelope it was built from: which nodes, with how
closely each matched, which relationships were walked, which passage was quoted
with its document and revision, and the Cypher that ran.

This is not decoration. An early answer confidently described CWE-307 as "a
reference weakness type", which is nonsense. A status label had been put where
the name belonged, and both the template and the model repeated it faithfully,
because both were told to use only what they were given. Nothing could have
caught that by reading the prose. The only way was to look at what had been
retrieved.

### What it does not do

- It answers security questions. Ask it when the last matcha was sold and it
  will reach for the nearest security evidence it has, because that is all it
  holds. The evidence block makes that obvious at a glance.
- The graph is not live. It reads the SOC service when told to, because the
  build is expensive and the join only works on a complete picture.
- Alerts can lag by up to twenty seconds, because the rules run on a timer.
- The operator pages sit behind the ordinary merchant API key. There is no
  separate operator role, so any merchant's key could read the alert feed.

## Design notes

The decisions worth defending out loud are recorded in the code itself, in a
comment above the thing being explained rather than in a document that drifts
away from it: why the
rate feed is a local simulator rather than an external API, why `/cancel` is an
action endpoint rather than a `PATCH`, why three different call types get three
different resilience strategies, and why one Postgres container with three
roles is an honest trade rather than a shortcut.
"# Panda-Pocket" 
