# Panda Pocket: understanding your own system

Two levels, deliberately separated.

**Part 1 and 2** are what a shop owner or a clerk would need. No jargon.
**Part 3, 4 and 5** are what the engineer who built it knows: why each piece
exists, what was traded away, and what happens when things break.

Read Part 1 and 2 first even though they look simple. The video needs both
voices, and most people fail the demo by starting at Part 3.

---

# PART 1 · What it does

## The problem, in one paragraph

A coffee shop in Potchefstroom wants to accept Bitcoin. The owner does not want
to **own** Bitcoin. She has no interest in watching a price chart, no appetite
for holding something that can drop ten percent overnight, and no way to pay her
staff or her landlord in Bitcoin. So she is stuck: her customers want to pay in
crypto, and she can only accept rand.

Panda Pocket sits in the middle. The customer pays crypto. The shop receives
rand. The shop never touches a coin.

## The three people involved

**The merchant.** The coffee shop. Signs up, gets an API key, and their till
system uses that key to create invoices. They see a dashboard showing their
balance and their payments.

**The customer.** Buys a coffee, pays in Bitcoin. Has no account with us, no
password, no key. They get a payment link and a page.

**The platform.** You. You take roughly one percent of every payment. A card
machine charges two and a half to three and a half percent, so you are cheaper,
you settle in minutes rather than days, and crypto payments cannot be charged
back.

## The life of one payment

**1. The till creates an invoice.**
The customer's coffee costs R250. The shop's till system asks Panda Pocket for
an invoice for R250.

**2. We lock a price.**
Bitcoin is R1,756,327 right now. So R250 is 0.00014235 BTC. **We write that
price down and never change it.** The invoice is now fixed for fifteen minutes.

**3. The customer pays.**
They get a link showing the amount, an address to send to, and a countdown. They
pay from their wallet.

**4. We credit the shop in rand.**
The moment payment confirms, we write two lines into the shop's ledger:

```
Credit  +R250.00     the full amount the customer paid
Fee       −R2.50     our one percent
```

Their balance goes up by R247.50.

**5. We tell the shop's own system.**
The till needs to know the coffee is paid for so it can print a receipt. So we
send a message to the shop's server. If their server is down, we keep trying.

## Why the locked price is the whole product

Between step 2 and step 3, Bitcoin might move. If it drops, the crypto we
receive is worth less than R250.

**We absorb that, not the shop.** The shop was quoted R250 and gets R250,
guaranteed, for fifteen minutes.

That transfer of risk is what the merchant is actually buying. Everything else
in the system is machinery to support it.

## Why fifteen minutes

Long enough for a human to open a wallet app and send a payment. Short enough
that the amount the price can move against us is small and predictable. If the
customer takes longer, the invoice expires and they ask for a new one at the
current price.

---

# PART 2 · Running it

## The mental model

You do not install .NET, PostgreSQL or MongoDB. **Docker contains all of it.**

Think of Docker as nine sealed boxes running on your machine. Each box has one
job. They can talk to each other on a private network. None of them can
interfere with anything else installed on your laptop.

Starting the system means starting the nine boxes. Stopping it means stopping
them. Resetting means throwing the boxes away and building new ones.

## The nine boxes

| Box | Job |
|---|---|
| `pp-gateway` | The front door. Everything comes through here |
| `pp-merchant` | Accounts, API keys, logins |
| `pp-invoice` | Invoices and the payment lifecycle |
| `pp-rate` | Bitcoin prices |
| `pp-settlement` | The money ledger and notifications |
| `pp-consul` | The address book: which services are alive |
| `pp-postgres` | Database for merchants, invoices and money |
| `pp-mongo` | Database for price history |
| `pp-seq` | Log viewer: everything every service says |

## Every command you need

### Start it

Double-click `START-HERE.bat`, or:

```bash
docker compose up -d
```

`up` means start. `-d` means detached, so it runs in the background and gives
your prompt back. If the boxes do not exist yet it builds them first.

### See what is running

```bash
docker compose ps
```

Nine rows. You want `running` and `(healthy)`.

`healthy` is stronger than `running`. Running means the program started. Healthy
means the service checked it can actually reach its own database and answered
yes. A service can be running and unhealthy, and that is the interesting case.

### Stop it

```bash
docker compose stop
```

Stops the boxes. **Your data survives.** Start again with `docker compose up -d`
and everything is as you left it.

### Reset it completely

```powershell
.\infra\reset-demo.ps1
```

This is the one you asked about. It does four things in order:

1. `docker compose down -v` — stops everything and **deletes the data**. The
   `-v` is the important part: it removes the volumes, which is where PostgreSQL
   and MongoDB keep their files. Without `-v` the data survives.
2. `docker compose up -d` — starts fresh boxes.
3. Waits until the gateway answers.
4. Runs the seed script.

Takes about a minute. You end up with empty databases that then get refilled
with fresh demo data.

**When to use it:** before recording, or whenever your data has gone stale and
every invoice has expired.

**What you lose:** every invoice, every ledger entry, every API key you created
by hand. The demo merchant is recreated automatically because it is seeded.

### Fill it with demo data

```powershell
.\infra\seed-demo.ps1
```

There is a `seed-demo.sh` next to it that does the same thing on Linux or
macOS. On Windows, use the PowerShell one: WSL bash is not reliably present,
and when it fails it fails quietly.

Creates three merchants and a spread of invoices covering every state.

**How it does it matters.** It does not write to the database directly. It calls
the same public API a real merchant would use, through the gateway, with an API
key. So running it is itself proof the API works end to end. A script that
inserted rows with SQL would prove nothing and would drift out of date the
moment an endpoint changed.

### When Docker will not start

```powershell
.\infra\fix-docker-sockets.ps1
```

Docker Desktop leaves broken socket files behind when it does not shut down
cleanly, then refuses to start, naming a different file each time. This finds
them all, clears them, and restarts Docker. Takes thirty seconds.

**Run it before recording.** It has been needed on most days of this project.

### Prove the databases are isolated

```bash
bash infra/verify-isolation.sh
```

Tries every database user against every database. Each reaches exactly its own
and is refused the other two.

### Read the logs

```bash
docker logs pp-invoice --tail 30
```

The last thirty lines from one service. Swap the name for any container.

Or use Seq at `http://localhost:5341`, which has all nine in one place and lets
you search.

## The addresses

| | |
|---|---|
| Dashboard | http://localhost:5000 |
| Consul | http://localhost:8500 |
| Seq | http://localhost:5341 |
| API docs | http://localhost:5002/swagger |

## The demo account

| | |
|---|---|
| Login | `owner@democoffee.co.za` / `demo-password-123` |
| API key | `pk_live_demo0000000000000000000000000000000000` |

---

# PART 3 · How it is built

## Why four services instead of one program

You could build all of this as a single application. It would be simpler. The
reason not to is that different parts of a payment system have genuinely
different needs.

Prices change every few seconds and nobody cares if a price is lost. Money must
never be lost. Accounts change rarely. Notifications to outside servers fail
constantly and need patient retrying.

Splitting them means each part can be deployed, scaled and fail on its own. If
the price service crashes, invoices can still be paid. In a single program, one
crash takes everything.

The cost is that services now have to talk over a network, and networks fail.
Most of the engineering in this system is dealing with that cost.

## What each service owns

### Merchant service

Owns accounts, API keys and dashboard logins.

**The thing worth knowing:** API keys are never stored. Only a SHA-256 hash of
each key is stored. When a key is created the plain text is shown once and never
again. If somebody stole the entire database they would get a list of hashes,
which cannot be turned back into working keys.

This is why there is no "show me my key again" button. It is not an oversight.
It cannot exist.

Passwords are hashed differently, with PBKDF2 at 100,000 iterations. That is
deliberately slow. Passwords are short and human-chosen so someone with the
database could guess them; making each guess slow makes that expensive. API keys
are 256 bits of randomness with nothing to guess, so they use a fast hash,
because that hash is computed on every single API request and a slow one would
add delay to everything.

### Invoice service

Owns the payment lifecycle. The heart of the system.

An invoice moves through states:

```
Pending ─── paid in full ──────────────→ Paid ──→ Settled
   │                                       ↑
   ├─── paid partially ──→ Underpaid ──────┘
   │                            │
   ├─── 15 minutes pass ────────┴─────────→ Expired
   │
   └─── merchant cancels ─────────────────→ Cancelled
```

`Settled`, `Expired` and `Cancelled` are final. Nothing leaves them.

**The thing worth knowing:** the rules for which moves are legal live in exactly
one file, as a table rather than scattered `if` statements. So "is this move
allowed" is asked once, in one place. Any move not in that table is both an
error to the caller and a logged security event.

### Rate service

Owns prices.

**The thing worth knowing:** prices come from a simulator running inside the
service, not from a real exchange. This is deliberate. An external API that
rate-limits you or goes down during a live demo is an unacceptable risk. The
simulator uses geometric Brownian motion, which is the standard mathematical
model for how asset prices move, so the numbers behave realistically.

Each currency pair has its own settings. USDTZAR barely moves because it is a
stablecoin. BTCZAR wanders. That is configured per pair, not one blanket
formula.

### Settlement service

Owns the money and the notifications.

**The thing worth knowing:** the ledger is insert-only. Rows are never edited or
deleted. The balance is a consequence of the entries, so it can always be
recalculated and checked. A ledger you can edit is one nobody can audit.

`/reconcile` does exactly that check: it re-adds every entry and compares the
total to the stored balance.

## The gateway

One public front door. Nothing else is reachable from outside.

It does four things to every request:

1. **Strips** any `X-Merchant-Id` header the caller sent
2. **Validates** the API key by asking the Merchant service
3. **Sets** `X-Merchant-Id` itself, from the validated key
4. **Stamps** a correlation id so the request can be traced

Step 1 is the one that matters and is easy to miss. Services downstream trust
that header to know who is calling. If a caller could set it themselves, someone
with a valid key for shop A could create invoices billed to shop B. Stripping it
first is what makes the header an assertion by the gateway rather than by the
client.

## The registry

Consul. An address book.

When a service starts it tells Consul: *"I am invoice-service, I am at this
address, here is a URL you can call to check I am alive."* Consul polls that URL
every ten seconds.

The gateway never has a hardcoded address. It asks Consul.

**Why this matters:** containers get new IP addresses every time they restart.
With hardcoded addresses, every restart means editing config. With a registry,
the service re-announces itself and everything keeps working.

## The databases

Three PostgreSQL databases and one MongoDB, and no service can read another's.

```
merchant_db     ← merchant_svc     only
invoice_db      ← invoice_svc      only
settlement_db   ← settlement_svc   only
rate_db         ← MongoDB, price history
```

Enforced at the database level. PostgreSQL grants connection rights to everyone
by default, so that grant is explicitly revoked and then given back to exactly
one user per database. `verify-isolation.sh` proves it.

**Why MongoDB for one of them:** price ticks are append-only, have almost no
structure, and are read by time range. That is what document databases are good
at. Invoices and ledgers have relationships and constraints worth enforcing,
which is what relational databases are good at. The split follows the workload.

## How a request actually travels

Creating one invoice:

```
Browser
  │  POST /api/invoices  with X-API-Key
  ▼
Gateway ──── "is this key real?" ────→ Merchant service ──→ merchant_db
  │      ←──── "yes, shop 1111" ──────
  │
  │  ──── "where is invoice-service?" ──→ Consul
  │      ←──── "172.18.0.6:8080" ────────
  ▼
Invoice service ──── "what is BTC worth?" ──→ Rate service
  │              ←──── "R1,756,327" ─────────
  │
  │  writes the invoice ──→ invoice_db
  ▼
201 Created, back through the gateway to the browser
```

Four services, one button press. Every step logged with the same correlation id,
so filtering the logs by that one id shows the whole journey in order.

Paying it adds:

```
Invoice service ──── "credit this merchant" ──→ Settlement service
                                                   │  writes 2 ledger rows
                                                   │  queues a webhook
                                                   ▼
                                              settlement_db
```

## Webhooks, since you asked

The shop's own server needs to know a payment landed, so it can print a receipt.

Two ways that could work. The shop's server could phone us every ten seconds
asking "anything yet?" — thousands of wasted calls. Or we phone them the moment
it happens. The second is a webhook.

A webhook is just: *you gave us a URL, we POST to it when something happens.*

Three complications, because it carries money news:

**They might be down.** We already took the money. If we call once and they miss
it, they never learn they were paid. So we retry: 3 seconds, 6, 12, 25, 45. The
gap doubles so we do not hammer a server that is already struggling. After six
attempts we mark it failed and keep the record, so a human can see what was
never delivered.

**Anyone could fake it.** If someone learned the shop's webhook URL they could
POST "you have been paid R10,000" and walk out with goods. So every message is
signed with HMAC-SHA256 using a secret only we and that shop know.

**A real message could be captured and replayed.** So the signature covers a
timestamp too, and anything older than five minutes is rejected.

---

# PART 4 · Why it is built this way

Every decision here has a reason you can defend.

## Two ledger lines, not one

A settled R250 invoice writes a `+250.00` credit and a `−2.50` fee, not a single
`247.50` line.

"You were paid R250 and we took R2.50" is checkable by the merchant. A single
net line hides where the difference went, and makes it impossible to sum the
platform's fee income as a column.

## The balance is stored even though it is calculable

Strictly it is redundant: add up the ledger and you have it. It is stored anyway
so that showing a balance is one row read rather than a sum over the merchant's
entire history.

The redundancy is deliberate and checkable. If the stored figure and the
recalculated figure ever disagree, something wrote the ledger wrongly, and
`/reconcile` catches it.

## Crypto amounts round up

R250 divided by the rate rarely lands on a round number. It rounds **up**, not
to nearest. Rounding down would ask the customer for fractionally less than the
invoice is worth, which across thousands of invoices is a systematic loss.

## Money is `decimal`, never `double`

Binary floating point cannot represent 0.1 exactly. A payment system that loses
fractions of a cent to rounding error is not one anyone should use. Crypto
columns go to eight decimal places because that is one satoshi, the smallest
unit of Bitcoin.

## Cancel is an action, not a field edit

`POST /api/invoices/{id}/cancel`, not `PATCH` with `{"status": "Cancelled"}`.

A state change is not a field edit. If status were writable a client could set
any value it liked, including `Settled`, which would be a client claiming it had
been paid out. Stripe does the same thing.

## Three different status codes for three different failures

- **409** duplicate transaction, or paying something already finished. Stop.
- **410** paying an expired invoice. Ask for a new one.
- **422** underpayment. The customer still owes money.

All three mean "not accepted", but a merchant's software should behave
differently in each case. Differentiated codes are only worth it if the
difference is actionable, and these are.

## Transaction hashes are unique in the database

One unique index does two jobs.

It makes payment submission **idempotent**: if a confirmation arrives twice, the
second is rejected, so a merchant cannot be credited twice for one payment.

And it is the **replay detector**: an attacker resubmitting a captured payment
hits the same constraint.

Doing this in the database rather than in code matters. An application check can
race with itself if two requests arrive at once. A unique index cannot.

## Three service calls, three different strategies

| Call | Strategy | Why |
|---|---|---|
| Invoice → Rate | Circuit breaker, cached fallback | On the critical path. A slightly stale price beats a checkout that hangs |
| Invoice → Settlement | Retry, idempotent endpoint, sweeper | It is money. Losing it means paid but not credited |
| Settlement → merchant | Durable queue, backoff, dead letter | Outside our control. Cannot be fixed by us, must not be hammered |

This table is the strongest single thing in the project. The point is not that
three patterns are used, it is that each was chosen from what failure costs.

## The checkout page has no login

The customer is not the merchant. They have no account and no key. Requiring one
would mean either putting the merchant's key into a page the customer can read,
or building a second login system for shoppers.

Instead the invoice id is the credential. It is a version 4 GUID, which is 122
bits of randomness, so the link cannot be guessed. Holding it is the
authorisation. BitPay and Coinbase Commerce work the same way.

That is also why the checkout response contains only what a payer needs and
deliberately leaves out the merchant id and everything about their account.

## Migrations run at startup

Each service creates its own database tables when it starts.

For coursework that must come up from a clean clone with one command, this is
the difference between `docker compose up` working and a marker having to run
database tooling by hand.

**A production system would not do this.** Two copies of a service starting at
once would race each other. There, migration is a deliberate step in the
deployment. Say this out loud rather than being caught by it.

---

# PART 5 · What happens when it breaks

Each of these is demonstrable, and each is a mark.

## The price service dies

```bash
docker compose stop rate-service
```

Invoices are still created, priced from the last rate the Invoice service saw.
The audit trail records that a cached rate was used and how old it was.

If the price service had been down since startup, with no cached price ever
seen, invoice creation returns **503**. There is no honest number to use, so it
refuses rather than inventing one.

The circuit breaker stops Invoice queuing requests against a dependency that is
already failing. After a few failures it stops trying entirely for fifteen
seconds, then lets one request through to test, then reopens or recovers.

## The merchant's server is down

Nothing breaks. Delivery retries with a widening gap and dead-letters after six
attempts, keeping the record. The invoice is still settled and the merchant is
still credited. They just have not been told yet.

## The settlement service dies

Payments still succeed. The invoice sits in `Paid` rather than `Settled`, which
is the honest state: the customer's money arrived, the merchant is not yet
credited. A background sweeper picks it up when Settlement returns.

## A database is unreachable

That service's health check turns unhealthy within a few seconds and says which
dependency failed. The service stays running and degrades rather than crashing:
Rate, for instance, keeps serving prices from memory while its history database
is unreachable.

## Someone tries a stolen API key

Rejected with **401** at the gateway. The request never reaches any service. A
security event is logged with the source address.

The response never says whether the key is unknown or revoked, because that
would tell an attacker which of their guesses had once been real.

## Someone floods the API

Thirty invoice requests per minute per merchant. The 31st gets **429** with a
`Retry-After` header, and a security event.

**An honest gap:** requests with no key at all are rejected before the rate
limiter sees them, so someone brute-forcing keys is not throttled here. Every
attempt is logged, but logging is not blocking. Say this rather than hoping it
is not noticed.

## Someone replays a payment

Rejected with **409** by the unique index on the transaction hash, and logged as
a replay attempt.

---

# The four sentences that matter most

If you remember nothing else:

**"The rate is locked at creation and never recalculated. That single decision
is the product: the merchant is quoted R250 and receives R250 whatever the price
does, because the platform carries the risk rather than the shop."**

**"Nothing in this system has a hardcoded address. Routes name a service, and the
gateway asks Consul where that service currently is."**

**"Each service owns its own database and no service can read another's. That is
enforced by the database, and I can prove it."**

**"The three service-to-service calls get three different resilience strategies,
because losing a price is cheap and losing money is not."**
