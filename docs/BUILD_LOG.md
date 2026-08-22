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
