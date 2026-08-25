# Build log

A running record of what was built each day and, more usefully, what was
decided and why. The report draws on this.

---

## Day 1, Saturday 22 August 2026 — tooling and skeleton

### Built

- Solution `PandaPocket.sln` with six projects: the Ocelot gateway, four
  service APIs and a shared contracts library.
- `docker-compose.yml` with Postgres 16, MongoDB 7 and Seq. No application
  services yet; they are added as they are built.
- `infra/postgres/init.sql` — three databases, three login roles, `CONNECT`
  revoked from `PUBLIC`.
- `infra/verify-isolation.sh` and `.ps1` — the isolation proof, scripted.
- Shared contracts: correlation header names, the invoice state machine as
  data, and the SOC event catalogue.

### Decisions

**`global.json` pins the SDK to 8.0.x.** This machine has both .NET 8.0.302 and
.NET 10.0.202 installed. Without the pin, `dotnet build` silently selects the
newest SDK, which would work locally and then fail against a `mcr.microsoft.com
/dotnet/sdk:8.0` build image on day 2. Pinning now converts a confusing
mid-week failure into a non-event.

**Service roles own their databases.** The brief's script creates roles and
databases separately. From Postgres 15 onwards `PUBLIC` no longer holds
`CREATE` on schema `public`, so an EF Core migration running as `invoice_svc`
would fail with "permission denied for schema public". Making each service role
the owner of its own database, and explicitly the owner of that database's
`public` schema, fixes this without granting anything broader. The isolation
property is unchanged: the `REVOKE CONNECT ... FROM PUBLIC` is still what does
the work.

**Seq is published on 5341, mapped to container port 80.** Seq serves both its
UI and its ingestion API on port 80 inside the container. 5341 is the
conventional Seq port and is what the brief specifies, so the mapping is
`5341:80` rather than the two-port arrangement older Seq versions needed.

**HTTP only, no HTTPS, for local development.** The `launchSettings.json` files
were rewritten to a single `http` profile on a fixed port per service (gateway
5000, Merchant 5001, Invoice 5002, Rate 5003, Settlement 5004). Fixed ports mean
the `.http` request collection does not need editing between runs. Dropping the
HTTPS profile avoids the dev-certificate problem inside containers, where the
certificate is not trusted and Ocelot's forwarding fails for reasons that look
like routing bugs. In a real deployment TLS terminates at the gateway and
service-to-service traffic on the private network is plain HTTP anyway, so this
is the production shape rather than a shortcut.

**The state machine lives in `Shared/Contracts` as a lookup table.** Expressing
the allowed transitions as data rather than as `if` statements inside the
Invoice service means the rule is stated exactly once, can be unit tested with
no database, and is directly readable as the statechart diagram. It also means
"is this transition legal" and "should this be a security event" are the same
question asked in one place.

**SOC event types are constants, not strings at the call site.** The next phase
loads these into Neo4j as labels. Constants make the catalogue greppable and
stop a typo becoming a silently missing edge in the graph.

**Seq needs an authentication decision, not just an EULA.** The brief says
`ACCEPT_EULA=Y` is enough. It is not, on Seq 2026.1: the container exits with
"No default admin password was supplied" unless either
`SEQ_FIRSTRUN_ADMINPASSWORD` or `SEQ_FIRSTRUN_NOAUTHENTICATION` is set. This is
a local demo log sink on a private compose network with no production data, and
a login wall standing in front of the logging evidence would only slow the
demo down, so authentication is explicitly declined. A deployed instance would
set a password instead. Worth stating out loud rather than hiding, because "why
is your log server unauthenticated" is an obvious question to be asked.

**Seq port mapping: `5341:80` is correct, and the reason is not obvious.**
Inside the container Seq serves the UI and management API on port 80 and
accepts log ingestion on 5341. Publishing `5341:80` therefore puts the UI on the
conventional Seq port on the host. Mapping `5341:5341` instead yields a server
that answers every UI request with a Seq-formatted 404, which looks like a
broken install rather than a port mismatch. Services on the compose network
ship to `http://seq` and never touch the published port.

**Postgres stays in the container, and the host install is a red herring.**
`C:\Program Files\PostgreSQL` on this machine contains a `data` directory
and nothing else: no `bin`, no server, no client, no registered service. The
cluster was created in June 2024, last ran on 28 January 2026, and was then
uninstalled, with the uninstaller leaving the data directory behind as it is
designed to. There is therefore no local Postgres to use even if it were
wanted, and it is not wanted: the deployment criterion rewards a system that
comes up from a clean clone with `docker compose up`, and moving the database
onto the host would mean running `init.sql` by hand and losing exactly the
automated bootstrap that makes the isolation demo reproducible.

**The isolation proof runs over TCP from the host, not inside the container.**
pgAdmin 4 was already installed under `%LOCALAPPDATA%\Programs`, which is why a
check of `C:\Program Files` missed it, and it bundles psql 16.3 against a 16.15
server. Running the proof through that client rather than through `docker exec`
improves the demo twice over. The refusal message gains a second line,
`DETAIL: User does not have CONNECT privilege`, which names the exact privilege
`init.sql` revokes; and a client crossing the container network boundary is a
more honest demonstration of isolation than one already inside the container.
The scripts auto-detect a host psql and fall back to `docker exec`, so they run
anywhere. They also now assert the expected outcome and exit non-zero on
surprise, rather than printing results for a human to eyeball.

### Environment notes

- Docker Desktop installed via winget; Docker CE 29.7.2.
- .NET 8.0.302 SDK already present.
- pgAdmin 4 (release 8) already present, with psql, pg_dump, pg_dumpall and
  pg_restore bundled in its `runtime` directory.
- draw.io desktop already present, so nothing outstanding for day 8.
- DBeaver not installed and not needed; pgAdmin covers it.

### Done when

- [x] Solution builds against net8.0
- [x] `docker compose up` starts Postgres, Mongo and Seq, all reporting healthy
- [x] Each role connects to its own database
- [x] Cross-database connection refused; captured in
      `docs/evidence/day1-isolation-proof.txt`, still to be screenshotted
- [x] Each role can run DDL in its own database, so EF Core migrations on day 3
      will not hit the schema-permission wall
- [x] Mongo accepts `rate_svc` and `rate_db` is writable

### Verified state at end of day 1

```
pp-postgres   running (healthy)   0.0.0.0:5432->5432/tcp    PostgreSQL 16.15
pp-mongo      running (healthy)   0.0.0.0:27017->27017/tcp  MongoDB 7
pp-seq        running             0.0.0.0:5341->80/tcp      Seq 2026.1.17114
```

### A trap avoided for tomorrow

Docker Desktop failed to start on first launch with "initializing Inference
manager: remove .../run/dockerInference: The file cannot be accessed by the
system". This machine had a previous Docker Desktop installation, and its
uninstall left orphaned AF_UNIX socket reparse points in
`%LOCALAPPDATA%\Docker
un` dated December 2025. Windows cannot delete a
reparse point whose backing kernel object is gone, so the Docker backend aborted
at startup because it could not clear the socket before binding it. Renaming the
whole `run` directory worked where deleting the individual files did not; Docker
recreated it cleanly on the next start. Recorded here because the same symptom
would recur if Docker Desktop is ever reinstalled before the demo.

---

## Day 2, Sunday 23 August 2026 — Rate service

### Built

- Rate service end to end: Mongo repository with the compound index, a
  geometric Brownian motion price simulator, a background tick generator with
  startup backfill, three endpoints, Swagger, Serilog to Seq, a health check
  that probes MongoDB, and a Dockerfile.
- `rate-service` added to compose, reachable as `http://rate-service:8080` on
  the compose network and published on 5003.
- Correlation id middleware in `Shared/Contracts`, so the three services still
  to be written inherit it.
- `requests/rate.http`, doubling as API documentation.
- `infra/fix-docker-sockets.ps1`, see below.

### Decisions

**The backfill exists so the history endpoint can be demonstrated.** On first
start, if the ticks collection is empty, the generator walks the simulation from
24 hours ago to now in one-minute steps and bulk inserts the result: 1 440 ticks
per pair. Without it `/api/rates/{pair}/history` returns an empty array until
the service has been running for hours, the compound index has nothing to work
against, and the endpoint cannot be shown. The backfill uses the same
mathematics as the live generator, so history and live data form one continuous
series rather than meeting at a visible seam.

**A restart resumes from the last stored rate rather than the configured start
price.** Otherwise every container restart puts a discontinuity in the series,
which looks like a bug during a demo. Verified: the container picked up from
1 784 878 where the previous run left off, instead of resetting to 1 800 000.

**USDTZAR is configured with near-zero volatility.** It is a stablecoin, so one
blanket volatility figure applied to all three pairs would be wrong. Over the
same 24 hours BTCZAR moved thousands of rand and USDTZAR moved two cents. The
per-pair configuration is what makes the simulation defensible rather than
decorative.

**A fixed random seed, offset per pair.** The seed makes a demo reproducible;
the per-pair offset stops all three pairs walking in lockstep, which would
otherwise produce three identically shaped lines.

**Health checks and correlation middleware were pulled forward from day 6.**
Both get copied into three more services, so building them properly now costs
about half an hour and removes work from the heaviest day of the week.

**Mongo driver timeouts lowered from 30 seconds to 3.** With MongoDB stopped,
the default settings made the health check take 60 seconds to report Unhealthy
and made the history endpoint hang rather than fail. A health check that takes a
minute to report a failure is not a health check, and a demo where everyone
watches a spinner is not a demo. Now the failure surfaces in about six seconds.

**Rate degrades rather than dying when MongoDB is unavailable.** The first
version let the background service throw, and .NET's default
`BackgroundServiceExceptionBehavior.StopHost` took the whole process down with
it. That is the wrong trade: the rate book is seeded from configuration and the
quote endpoint needs no database at all, so losing history should not mean
losing the service. Initialisation now retries with a capped backoff, quotes
keep returning 200, `/health` reports 503, and history returns a 503 with a
`Retry-After` header rather than a 500. When MongoDB returns, the service
recovers on its own with no restart. This behaviour is worth demonstrating in
its own right and it is the same failure philosophy the day 6 circuit breaker
will apply to the Invoice to Rate call.

### Two bugs worth recording

**Middleware ordering silently cost the tracing demo.** `UseSerilogRequestLogging`
was registered before `UseCorrelationId`, so the correlation property was pushed
into the log context inside the request-logging middleware and had already been
popped by the time that middleware wrote its summary line. Everything appeared
to work: handler logs carried the correlation id. But the single most useful
line, the one carrying method, path, status code and elapsed time, did not. The
order is now correlation first, request logging second.

**Catching `MongoException` was not enough.** A server selection timeout, which
is what "the database is down" actually looks like, surfaces as
`System.TimeoutException`, which does not derive from `MongoException`. The
history endpoint returned 500 until the catch was widened to both.

### The port conflict

Rate could not authenticate against MongoDB when run locally, while the same
credentials worked from inside the container. The cause was a **local MongoDB
service running on this machine** and also listening on 27017, so a host process
connecting to `localhost:27017` reached the local server, which has no
`rate_svc` user. The container's published port was moved to **27018** rather
than stopping the local service, since that leaves the developer's own software
alone and removes the ambiguity permanently. Nothing inside the compose network
changed: services still reach MongoDB as `mongo:27017`.

This is the second instance of the same pattern on this machine, after the
leftover PostgreSQL data directory found on day 1. Worth checking what else is
listening before assuming a configuration error.

### Docker Desktop keeps orphaning sockets

Docker failed to start twice more, each time naming a different socket:
`sailor-ingest.sock`, then `docker-secrets-engine/engine.sock`. The cause is the
one identified on day 1: unclean shutdown leaves AF_UNIX reparse points that
Windows cannot delete, and the backend aborts at whichever it reaches first.
Fixing them one at a time is whack-a-mole, so `infra/fix-docker-sockets.ps1` now
finds every orphaned socket across all of Docker's runtime directories, renames
the directories holding them, restarts Docker and waits for the engine. Run it
if Docker will not start, and run it before the demo.

### Done when

- [x] Rate runs as a container alongside the data stores, reporting healthy
- [x] Its logs appear in Seq at `localhost:5341`, filterable by `Service = 'Rate'`
- [x] A supplied `X-Correlation-Id` is echoed and stamped on the request summary
      line in Seq, alongside status code and elapsed time
- [x] All three endpoints return real data; unknown pair gives 404, inverted
      range gives 400
- [x] History returns a populated series, 1 440 backfilled points per pair plus
      live ticks, as one continuous series
- [x] `/health` reports Unhealthy within seconds when MongoDB is stopped, and
      recovers automatically when it returns
- [x] Swagger UI and the OpenAPI document are served by the running container

### Verified state at end of day 2

```
pp-postgres   running (healthy)   5432->5432    PostgreSQL 16.15
pp-mongo      running (healthy)   27018->27017  MongoDB 7
pp-seq        running             5341->80      Seq 2026.1.17114
pp-rate       running (healthy)   5003->8080    Rate service
```

---

## Day 3, Monday 24 August 2026 - Invoice service and gateway

### Built

- Invoice service: EF Core over Postgres with `invoices`, `payments` and
  `invoice_status_history`, the state machine enforced on every transition, an
  expiry sweeper, six endpoints, Swagger, Serilog to Seq and a health check that
  probes Postgres.
- Ocelot API gateway routing to Invoice and Rate by compose service name, minting
  correlation ids and serving the browser client.
- `src/Gateway/wwwroot/index.html`: plain HTML and fetch, no framework, no build
  step. Create form, live rates, invoice table with countdown, and buttons for
  pay, underpay, cancel and replay.
- Both containerised and added to compose. Six containers now come up from one
  command.
- `requests/invoice.http`.
- `HealthResponseWriter` moved from Rate into `Shared/Contracts` now that a
  second service needs it.

### The first end-to-end moment

The HTML page creates an invoice through the gateway, in Docker: browser to
Gateway to Invoice to Rate to Postgres, every hop resolved by Docker DNS. This
was the day's stated goal and the point at which the system stopped being parts.

### Decisions

**The locked rate is written once and never recalculated.** One assignment in
`InvoiceService.CreateAsync` is the entire product. The merchant is quoted R250
and receives R250 regardless of what the price does inside the window, because
the platform holds the risk for those fifteen minutes rather than the shop.

**Crypto amounts round up, not to nearest.** Rounding down would ask the customer
for fractionally less than the invoice is worth, and across many invoices that is
a systematic loss to the platform rather than a rounding artefact.

**Money is `numeric`, never `double`.** Binary floating point cannot represent
0.1 exactly. Eight decimal places on crypto columns because that is one satoshi.

**Status is stored as text, not an integer enum.** An integer is more compact and
makes the table unreadable during a demo, and worse, any future reordering of the
enum silently corrupts existing rows.

**`/cancel` is an action endpoint, not a `PATCH` on status.** A state transition
is not a field edit. Exposing status as writable would let a client set any value
it liked, including `Settled`, which is a client asserting it has been paid out.

**Differentiated status codes, and the difference is actionable.** 409 for a
duplicate transaction hash, 410 for a payment against an expired invoice, 422 for
an underpayment. A merchant integration can act on each differently: stop, fetch
a fresh invoice, or wait for the rest of the money. All verified.

**Migrations run at startup.** For a coursework artefact that must come up from a
clean clone with one command, an automatic migration is the difference between
`docker compose up` working and a marker running EF tooling by hand. A production
system would make this a deliberate deployment step, because automatic migration
under multiple replicas is a race. The retry loop exists because a container
reported healthy is not always immediately accepting connections.

**An expiry sweeper as well as a lazy check.** The payment path checks expiry
directly as a safety net, but lazy evaluation alone would leave an invoice nobody
looks at sitting in `Pending` for ever, so status filters would report stale
figures and the audit trail would have no record of the moment the window closed.
Sweeper-driven expiries carry a **null correlation id**, which is honest: no
caller's request caused them, time did.

### The bug that mattered most

**EF Core relationship fixup silently marked underpaid invoices as fully paid.**

The first version added the payment to both `db.Payments` and
`invoice.Payments`, then summed the navigation collection. That looks correct and
is not. EF performs relationship fixup: `db.Payments.Add` sees a tracked parent
and appends the payment to `invoice.Payments` itself, so adding it manually put
it in the collection twice and the total doubled. A payment of 0.0001 against a
required 0.00014109 was accepted as **Paid**.

That failure is silent, produces a valid-looking invoice, and in a real system
would credit a merchant for money never received. The running total is now
computed from the existing payments plus this one, before anything is attached,
which is deterministic regardless of fixup.

Caught only because the status codes were tested one by one rather than assumed.

### Two smaller ones

**Non-nullable `int` query parameters are required in minimal APIs.** `int page`
meant `GET /api/invoices` with no query string returned 400 complaining that
"page" was not provided. Paging must be `int?` with defaults applied inside.

**Ocelot is terminal middleware, so `app.MapGet("/health")` was unreachable.**
When no route matches, Ocelot returns 404 itself rather than calling the next
middleware, so endpoint routing never runs. The gateway health check had to
become a `Map` branch registered before Ocelot. Anything the gateway serves
itself, including static files, has to sit above it.

### The client re-render flaw

The invoice table rebuilt its entire `innerHTML` on every fifteen-second poll.
That destroys and recreates every row, so a button being clicked at that instant
vanishes from under the cursor and the click is lost, and the countdown cells the
local ticker is updating visibly stutter. It now compares a signature of id,
status and received amount, and only re-renders when something actually changed.

Found while driving the page with a browser tool, which kept losing element
references. Worth having fixed: the same thing would have happened live.

### On service discovery

Ocelot routes to `invoice-service` and `rate-service`, never to an IP or to
localhost. Docker's embedded DNS resolves those names to whichever container
currently holds them, verified in the evidence file:

```
172.18.0.6      invoice-service
172.18.0.3      rate-service
```

Containers can be stopped, rebuilt and given a different IP with no gateway
reconfiguration. `ocelot.json` holds the deployed routing and
`ocelot.Development.json` repoints the same routes at localhost for running the
gateway outside Docker. The routes are repeated in full rather than patched,
because .NET configuration merges JSON arrays by index and a partial override
would silently depend on both files keeping their routes in the same order.

### Done when

- [x] The HTML page creates an invoice through the gateway
- [x] Gateway routes to Invoice and Rate by compose service name
- [x] Invoice calls Rate and locks the returned rate on the invoice
- [x] All three tables created by migration, owned by `invoice_svc`
- [x] Status codes verified: 201, 400, 404, 409, 410, 422, 503
- [x] Audit trail records every transition with its correlation id
- [x] One correlation id visible across Gateway, Invoice and Rate in Seq
- [x] Six containers healthy from a single `docker compose up`

### Verified state at end of day 3

```
pp-postgres   running (healthy)   5432->5432    PostgreSQL 16.15
pp-mongo      running (healthy)   27018->27017  MongoDB 7
pp-seq        running             5341->80      Seq 2026.1.17114
pp-rate       running (healthy)   5003->8080    Rate service
pp-invoice    running (healthy)   5002->8080    Invoice service
pp-gateway    running (healthy)   5000->8080    Ocelot gateway + client
```
