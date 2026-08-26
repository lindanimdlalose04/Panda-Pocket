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

---

## Day 4, Tuesday 25 August 2026 - Merchant service and authentication

### Built

- Merchant service: accounts, hashed API keys, dashboard users, JWT login, key
  issue and revoke, and an internal key-validation endpoint.
- API key middleware in the gateway, with a short-lived cache of validated keys.
- Invoice reworked so merchant identity comes only from the gateway, with reads
  and payments scoped to the authenticated merchant.
- Demo merchant seeded at startup so the stack is usable from a clean clone.
- API key field in the client, and `requests/merchant.http`.
- snake_case column naming across both schemas.

### Done when

- [x] An unauthenticated create returns 401 and an authenticated one succeeds

### The decision the whole day rests on

**A client must never be able to say which merchant it is.** Until today the
Invoice service would read a merchant id from the request body. That was
harmless while nothing was authenticated and is a privilege escalation the
moment anything is: a caller naming their own merchant could bill invoices to
another account and then read that account's invoices back.

Two things now make `X-Merchant-Id` trustworthy, and **both** are required:

1. The gateway **strips** the header from every inbound request before it does
   anything else. Without this, holding any valid key would be enough to act as
   any merchant, because the key would be genuinely yours and the header would
   name somebody else.
2. The gateway then sets it from the key it validated against the Merchant
   service, which owns that data. The gateway never reads the merchant database.

Invoice refuses a request with no merchant identity rather than defaulting to
one. Verified: a request carrying a valid key and a forged
`X-Merchant-Id: 99999999-...` produces an invoice belonging to the demo
merchant, not the asserted GUID.

### Decisions

**Two credential types, deliberately not interchangeable.** An API key
authenticates a merchant's *server* and is long lived, because a server cannot
retype a password. A JWT authenticates a *person* and expires in an hour,
because a browser session should not outlive the human. Key management sits
behind the JWT and only the JWT: if an API key could manage API keys, a leaked
key could mint replacements for itself and revoke the real ones, locking the
merchant out of their own account.

**Fast hash for keys, slow hash for passwords.** API keys are 256 bits from a
cryptographic RNG, so there is no dictionary to attack and SHA-256 is correct;
the hash is computed on every authenticated request, where PBKDF2 at 100 000
iterations would add real latency to every API call. Passwords are low entropy
and human chosen, so they get PBKDF2 with a per-user salt, computed once per
login. Using one algorithm for both would be wrong in one direction or the other.

**`RandomNumberGenerator`, not `Random`.** `Random` is a deterministic generator
seeded from the clock: predict the seed and you predict every key it will ever
issue. Fine for simulating a Bitcoin price, catastrophic for issuing
credentials. The same codebase does both, which is exactly why the distinction
is commented where it is made.

**Failed logins are constant-time.** The password is verified against a dummy
PBKDF2 hash even when no user was found. Returning early on an unknown email
makes the response measurably faster than for a known one, which turns login
into an oracle for enumerating valid accounts by timing alone.

**Authentication failures never say why.** "No such key" and "revoked key"
return the same message, because distinguishing them tells an attacker which of
their guesses had once been real.

**403 for another merchant's resource, not 404.** The resource exists and the
caller is authenticated; what they lack is authorisation. Hiding existence would
be worth it against guessable ids, but these are v4 GUIDs, so there is nothing
to enumerate and the honest code is more useful to an integrator.

**Validated keys are cached for 30 seconds.** Otherwise every API call becomes
two network hops and a database read. The trade is that a revoked key keeps
working for up to that long, which is why the window is short. Failures are
never cached, so brute forcing gets no cheaper and a newly issued key never
appears broken.

**Key validation fails closed.** If the Merchant service is unreachable the
gateway refuses the request. Failing open would mean an outage in one service
silently removing authentication from the entire system.

**The validation endpoint is not routed through Ocelot.** A public
`/keys/validate` would be an oracle letting anyone test guessed keys at will. It
is reachable only from inside the compose network.

**Rates stay public.** A checkout page must show a price before anyone has
authenticated and nothing about a rate is merchant-specific, so requiring a
credential would protect nothing while forcing the browser to hold a key just to
draw a number. BitPay and Coinbase Commerce both publish theirs openly.

**API keys are returned exactly once.** Only the SHA-256 hash is stored, so the
"show me my key again" button every merchant asks for cannot exist. Losing a key
means issuing a new one and revoking the old. Demonstrable: the raw `api_keys`
table is in the evidence file and contains nothing usable.

### snake_case column naming

EF Core names columns after .NET properties, so the tables had `KeyPrefix` and
`MerchantId`. In Postgres those are case-sensitive and need double-quoting in
every hand-written query, and worse, the live schema disagreed with the data
model documented in the report.

A naming convention now rewrites columns, keys, foreign keys and indexes to
snake_case, applied at the end of `OnModelCreating` so explicit names still win.
EF generated rename migrations rather than drops, so existing data survived.

Done today rather than later on purpose. Settlement adds a third schema
tomorrow, so fixing two contexts now is cheaper than fixing three on day 6, and
the working rules bar refactoring after day 6 entirely.

### A wasted hour worth recording

The 401 test kept returning 201 against what looked like a correctly built
stack. The cause was that a batched `docker compose build` had timed out and
Docker Desktop restarted mid-build, so `docker compose up` cheerfully started
**four-hour-old images** while reporting every container healthy. Healthy means
the process answers its health endpoint, not that it is running the code just
written.

Checking image age with `docker images` and its CreatedSince column is the fix,
and it is worth doing before concluding that a security control is broken.
Building one image per command, rather than three in one, also avoids the
timeout that caused it.

### Addendum: the stale-build failure recurred, and it is worth understanding

The wasted hour recorded above happened a second time, in a nastier form, and
the pattern is worth recognising before the demo.

A `docker compose build` covering three services was started, timed out at the
tool level, and kept running in the background. Roughly forty minutes later,
long after the snake_case work had been finished, built and verified, that
queued build **completed and overwrote the correct images with ones built from
an older source snapshot**. It then recreated the containers.

The symptom was not an obvious failure. Postgres held correctly renamed
snake_case columns, because the migration had genuinely run and the rename is
persistent. But the Merchant assembly now running was the pre-convention build,
so its queries asked for `m."Id"` against a table whose column is `id`:

```
Npgsql.PostgresException 42703: column m.Id does not exist
```

Every container reported healthy throughout, because `/health` only proves the
process is alive and can reach its database, not that the code matches the
schema. The visible effect was that a valid API key started returning 401, which
looks like an authentication bug and is actually a stale binary.

Three things to take from it:

**Never leave a long build running in the background while continuing to edit.**
A build that finishes later than you expect can silently replace newer images
with older ones. Build one service per command, in the foreground.

**Healthy does not mean current.** Check what is actually running:
`docker images` with its CreatedSince column, and compare
`docker inspect <container> --format '{{.Image}}'` against the tagged image id.

**A schema and a binary can disagree.** Migrations persist; images do not
necessarily. When a database error names a column that clearly exists, suspect
the code rather than the database.

Recovery was `docker compose build --no-cache` per service, then
`docker start`, and re-running the acceptance tests. `--no-cache` was used
deliberately rather than a plain rebuild, to guarantee no cached layer from the
bad build survived.

One further quirk observed on this machine: `docker compose up -d
--force-recreate` sometimes leaves containers in `created` state without
starting them, and the command hangs. `docker start <name>` completes
immediately and is the reliable fallback.

### Verified state at end of day 4

```
pp-postgres   running (healthy)   5432->5432    PostgreSQL 16.15
pp-mongo      running (healthy)   27018->27017  MongoDB 7
pp-seq        running             5341->80      Seq 2026.1.17114
pp-rate       running (healthy)   5003->8080    Rate service
pp-merchant   running (healthy)   5001->8080    Merchant service
pp-invoice    running (healthy)   5002->8080    Invoice service
pp-gateway    running (healthy)   5000->8080    Ocelot gateway + client
```

Security events reaching Seq from both Gateway and Merchant: `AUTH_FAILED` and
`API_KEY_INVALID`, each carrying correlation id, path, method and remote IP,
which is the shape the SOC layer will ingest.

---

## Day 5, Wednesday 26 August 2026 - Settlement and webhooks

### Built

- Settlement service: `ledger_entries`, `merchant_balances` and
  `webhook_deliveries`, with balance, ledger, reconciliation and delivery-log
  endpoints.
- Webhook dispatcher: durable queue, HMAC-signed payloads, exponential backoff
  with jitter, and dead-lettering after six attempts.
- Invoice calls Settlement on payment, then moves the invoice to Settled, with
  a sweeper that picks up anything the inline retries missed.
- `PandaPocket.Shared.Persistence`, so the snake_case convention lives in one
  place rather than being copied into a third service.
- A demo webhook receiver at `/demo/webhook-sink` on the gateway.
- `requests/settlement.http`, and ledger plus delivery panels in the client.

### Decisions

**Two ledger entries per settlement, not one net entry.** A R250 invoice writes
`Credit +250.00` and `Fee -2.50`. "You were paid R250 and we took R2.50" is a
statement a merchant can check; a single R247.50 line hides where the difference
went and makes platform fee income impossible to sum. Amounts are signed, so the
balance is a plain `SUM` of the column rather than a conditional that has to
know which entry types subtract, which means a sign error shows up as visibly
wrong arithmetic instead of a silently wrong total.

**`balance_after` is stored even though it is derivable.** Redundant on purpose:
a statement renders without recomputing a running total across the merchant's
whole history, and any divergence between the column and the recomputed sum is
proof that something wrote the ledger incorrectly. Redundancy you can check is a
feature. `/reconcile` is that check, and it raises a CRITICAL event on mismatch.

**Ledger rows are insert-only.** Nothing updates or deletes them. That is what
makes this a ledger rather than a balance table with history bolted on: the
balance is a consequence of the entries and can always be recomputed. A ledger
you can edit is one nobody can audit.

**Settlement is idempotent per invoice, enforced by a unique index.** The index
on `(invoice_id, entry_type)` is what makes Invoice's retry safe. This matters
more than a comment can convey: an application-level "have I already settled
this" check races with itself under concurrent requests, and a unique index does
not. The service checks first for a clean 200, and catches the constraint
violation for the case where two calls raced and both passed the check.

**The webhook row is written in the same transaction as the ledger.** Queueing
the notification only after a successful commit would leave a window where a
crash means a merchant is credited and never told. Committing them together
makes the intent to notify exactly as durable as the money.

**The stored payload is signed, and is never regenerated per attempt.** The HMAC
covers those exact bytes. Rebuilding the JSON on retry risks a different
serialisation, a different signature, and a retry the merchant correctly rejects
as forged.

**The timestamp is inside the signed material.** Signing only the body would let
anyone who ever captured one valid callback replay it verbatim for ever, since
the signature would stay valid. With the timestamp signed and checked, a
captured callback stops being useful after five minutes.

**Three call types, three resilience strategies.** This is the distinction that
belongs in the report, and each choice follows from what failure costs:

| Call | Strategy | Why |
|---|---|---|
| Invoice to Rate | Circuit breaker, cached fallback (day 6) | Critical path. A slightly stale rate beats a checkout that hangs. |
| Invoice to Settlement | Retry, plus a sweeper, against an idempotent endpoint | It is money. Losing it means a merchant was paid and never credited. |
| Settlement to merchant | Durable queue, backoff with jitter, dead letter | External and untrusted. Cannot be fixed by us, must not be hammered. |

**Jitter, not plain exponential backoff.** Without it, a hundred deliveries that
failed together retry together for ever, arriving as synchronised bursts that
are themselves a small denial of service against an endpoint already struggling.

**Bounded retries with a dead letter, not infinite retries.** Retrying for ever
ties up resources on an endpoint that may never return. After six attempts the
row is marked Failed and kept, so somebody can see what was never delivered and
requeue it once the merchant has fixed their side.

### The ordering bug

The credit and fee were written with `now` and `now.AddTicks(1)`, so that
ordering by `created_at` would put the credit first. It did not. A .NET tick is
100 nanoseconds and Postgres timestamps have microsecond resolution, so the
added tick is rounded away on write: both rows landed on an identical timestamp
and the database returned them in whatever order it liked. On a statement that
reads as the fee being charged before the money arrived. Changed to
`AddMilliseconds(1)`, which survives the round trip.

Visible in the client: entries written after the fix are correctly ordered,
while the older rows still show the arbitrary order they were stored with.

### A note on the demo webhook sink

`/demo/webhook-sink` is a test harness, not part of the product, and is
namespaced and routed accordingly. It exists because "the retry backs off
correctly" is only half the story: without a receiver, nothing ever demonstrates
a delivery succeeding, and nothing demonstrates the signature being verified by
the party it protects.

It does what a real integration should do, so it doubles as documentation of the
expected merchant side, and it returns 401 rather than 200 to a payload it
cannot verify. One difference from a real merchant: it looks the signing secret
up from the Merchant service, because it stands in for any merchant rather than
one. A real integration already knows its own secret.

### Done when

- [x] A payment produces exactly two ledger rows, a credit and a fee
- [x] The merchant balance updates and reconciles against the ledger sum
- [x] A webhook delivery record exists with a climbing attempt count
- [x] Backoff observed at 3s, 6s, 12s, 25s, 46s, capped and jittered
- [x] Exhausted deliveries dead-letter as Failed with a CRITICAL event
- [x] A failed delivery can be requeued manually and returns 202
- [x] A successful delivery is verified by the receiver's HMAC check
- [x] Settling the same invoice twice returns 200 and does not double-credit

### Verified state at end of day 5

```
pp-postgres     running (healthy)   5432->5432    PostgreSQL 16.15
pp-mongo        running (healthy)   27018->27017  MongoDB 7
pp-seq          running             5341->80      Seq
pp-rate         running (healthy)   5003->8080    Rate service
pp-merchant     running (healthy)   5001->8080    Merchant service
pp-invoice      running (healthy)   5002->8080    Invoice service
pp-settlement   running (healthy)   5004->8080    Settlement service
pp-gateway      running (healthy)   5000->8080    Ocelot gateway + client
```

All four services and the gateway are now built, containerised and healthy from
a single `docker compose up`. Day 6 adds no new services, only the circuit
breaker, rate limiting and the remaining observability work.

---

## Day 6, Thursday 27 August 2026 - patterns and observability

No new features, as planned. Three of the day's five items were already done,
because health checks and correlation middleware were pulled forward to day 2
and SOC emission grew through days 3 to 5. That left the circuit breaker and
rate limiting.

### Built

- Circuit breaker on Invoice to Rate, using
  `Microsoft.Extensions.Http.Resilience`, with a cached last-known-good rate as
  the fallback and a staleness ceiling.
- Ocelot rate limiting on the invoice write path, returning 429.
- The final two SOC events, `CIRCUIT_OPENED` and `RATE_LIMIT_EXCEEDED`. All
  eleven catalogue entries are now verified present in Seq.

### Decisions

**A breaker without a fallback would be pointless here.** On its own a circuit
breaker converts a slow failure into a fast one, which helps the system and does
nothing for the merchant: their checkout still fails, just sooner. The cached
rate is what turns it into a genuine degradation. While Rate is down, invoices
are still created, priced from the last rate we saw.

**The cache lives in memory, not in the database.** It is a cache of something
another service owns and is rebuilt within seconds of the first successful
quote. Persisting it would mean a container could start up and confidently serve
a rate from last week.

**There is a staleness ceiling, currently thirty minutes.** Past that a stale
rate stops being a degradation and becomes a liability: the platform would be
locking a merchant to a price that no longer reflects the market and absorbing
the difference at settlement. Beyond the ceiling, declining is the cheaper
mistake.

**A cold start with Rate already down returns 503, and should.** There is no
honest number to fall back on, so the service refuses rather than inventing one.
Verified.

**Fallback use is written into the invoice audit trail**, with the staleness:

```
(new) -> Pending | Invoice created on a cached rate, 53s old (rate-service unavailable)
```

Months later it is still possible to answer "why was this invoice priced at
that", and a degraded invoice stays distinguishable from a healthy one.

**A 404 from Rate is not a fallback case.** That is Rate working correctly and
saying the pair does not exist. Serving a cached rate for a pair Rate has
stopped publishing would be worse than refusing, so the cache is deliberately
not consulted on 404.

**Rate limiting is keyed on `X-Merchant-Id`, not the API key or the IP.** The
gateway sets that header itself after validating the key, so the quota is per
merchant: three keys share one quota, which is the honest unit, because the
limit protects the platform from one customer rather than from one credential.
Per-IP would lump every merchant behind a corporate NAT into a single bucket.

**Limits apply to the invoice write path only.** Rates are public market data
that a checkout page polls legitimately every few seconds.

### The bug that mattered: the breaker defeated its own fallback

The first version caught `HttpRequestException`, `TaskCanceledException` and
`InvalidOperationException` around the Rate call. Testing with Rate stopped gave:

```
attempt 1 -> HTTP 201     fallback worked
attempt 2 -> HTTP 201     fallback worked
attempt 3 -> HTTP 500
attempt 4 -> HTTP 500
```

While the breaker is CLOSED, a failing call surfaces as `HttpRequestException`
and the fallback runs. Once the breaker OPENS it stops calling Rate at all and
throws `BrokenCircuitException`, which derives from `ExecutionRejectedException`
and not from `HttpRequestException`. So the catch worked right up until the
breaker tripped, and then stopped working at exactly the moment the breaker
started doing its job.

This is worth stating plainly because it is the classic circuit breaker mistake:
the breaker opening throws a *different* exception type from the failure it was
created to handle, so a catch written for the underlying failure misses
precisely the case the breaker exists to produce. Two requests degrade
gracefully and every one after that returns 500.

Fixed by catching `ExecutionRejectedException`, the base type, which also covers
timeout and rate-limiter strategies if they are added to this pipeline later.

Both paths are now visible in Seq, and the difference between them is the state
of the breaker:

```
reason=HttpRequestException    breaker CLOSED, the call was attempted and failed
reason=BrokenCircuitException  breaker OPEN, the call was rejected without trying
```

### The spec bug the SOC audit found

Checking that all eleven event types actually appear in Seq turned up
`PAYMENT_ON_EXPIRED_INVOICE` missing. Chasing why exposed a real
spec-compliance bug rather than a logging gap.

The specification says a payment against an expired invoice returns **410 Gone**.
It was returning 409. The 410 branch only fired while an invoice was still
`Pending` or `Underpaid` and had just passed its deadline, which is a window of
at most one sweep interval. Once the ExpirySweeper moved it to `Expired`, the
payment fell into the generic terminal-state branch and got 409 instead. Since
the sweeper runs every thirty seconds, the specified 410 was effectively
unreachable and the event never fired.

The distinction matters to an integrator: 409 says stop, 410 says this one is
gone, request a fresh invoice. Fixed with an explicit `Expired` branch that
returns 410 and emits the event. Verified.

Worth noting how this was found. The bug was invisible from the endpoint tests,
which all passed, and only surfaced from asking "is every event in the catalogue
actually being emitted". Auditing observability found a correctness bug.

### Verification

Circuit breaker:

```
Cold start, Rate down, no cached rate  -> HTTP 503, refuses to invent a price
Warm cache, Rate stopped, 6 attempts   -> HTTP 201 x6, all on the cached rate
Locked rate matched the cached value exactly (R1743665.50)

Circuit to rate-service OPENED for 15s after 50 % failures
Circuit to rate-service HALF-OPEN; probing with a single request
Circuit to rate-service CLOSED; the dependency is answering again
```

Rate limiting: 40 requests against a 30 per minute limit gave 27 allowed and 13
throttled, first 429 at request 28, with a `Retry-After` header and the
configured quota message.

### An honest gap

Unauthenticated requests are rejected by the API key middleware before Ocelot
ever sees them, so they carry no `X-Merchant-Id` and are never rate limited.
Someone brute forcing API keys is throttled by nothing at this layer. Every
rejection is logged as `API_KEY_INVALID` with its source address, so the SOC
layer can see it, but detection is not prevention. Recorded in `ocelot.json` and
stated here rather than glossed over.

### Done when

- [x] Stopping the Rate container still allows invoice creation on a fallback rate
- [x] `CIRCUIT_OPENED` appears in Seq, with both rejection paths distinguishable
- [x] The breaker opens, half-opens and closes, all observable
- [x] Ocelot rate limiting returns 429 with `Retry-After`
- [x] `RATE_LIMIT_EXCEEDED` appears in Seq with merchant, path and source address
- [x] All eleven SOC catalogue events verified present in Seq
- [x] Health checks probe their database (done day 2)
- [x] Correlation ids propagate across services (done day 2)

### Patterns implemented, final count

1. Database per service, with polyglot persistence
2. Circuit breaker with a cached fallback (Invoice to Rate)
3. Retry with exponential backoff and dead-lettering (webhook delivery)
4. Retry against an idempotent endpoint, plus a sweeper (Invoice to Settlement)
5. Centralised logging with correlation ids
6. Health checks that probe their dependency
7. API key and JWT authentication, validated at the gateway
8. Rate limiting at the gateway

Two were required.

### Verified state at end of day 6

```
pp-postgres     running (healthy)   5432->5432    PostgreSQL 16.15
pp-mongo        running (healthy)   27018->27017  MongoDB 7
pp-seq          running             5341->80      Seq
pp-rate         running (healthy)   5003->8080    Rate service
pp-merchant     running (healthy)   5001->8080    Merchant service
pp-invoice      running (healthy)   5002->8080    Invoice service
pp-settlement   running (healthy)   5004->8080    Settlement service
pp-gateway      running (healthy)   5000->8080    Ocelot gateway + client
```

Code freezes tomorrow night. Day 7 is client polish, seed data and nothing else.
