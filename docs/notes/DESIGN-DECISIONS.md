# Defending every decision

Part 4 of the system guide, in depth.

Each decision below has the same shape: what was chosen, what the obvious
alternative was, the concrete failure the alternative produces, and a sentence
you can actually say out loud.

The pattern to internalise is that **no decision here is "best practice".** Each
one is a trade with a named cost. Being able to name the cost is what separates
understanding a system from having built one.

---

## 1 · Two ledger lines, not one

**Chosen.** A settled R250 invoice writes two rows:

```
Credit  +250.00   balance 250.00
Fee       −2.50   balance 247.50
```

**The obvious alternative.** One row: `+247.50`. Same balance, half the storage,
half the writes.

### Why the alternative fails

**The merchant cannot verify it.** They know their customer paid R250. A row
saying 247.50 asks them to trust our arithmetic. Two rows let them check: did
you receive what I was owed, and what exactly did you take.

**Fee income becomes uncomputable.** With two rows, total platform revenue is
`SELECT SUM(amount) WHERE entry_type = 'Fee'`. One query. With net rows there is
no fee column, so you would have to recompute it from every invoice's amount
multiplied by that merchant's fee rate **at the time** — and fee rates change.
You would be reconstructing history from data that has moved on.

**They are two different facts.** "The customer paid you" and "we charged you
commission" happen to occur together, but they are separate economic events. A
ledger records events. Merging them loses information you cannot get back.

### The signed-amount detail

Credits are positive, fees negative. So the balance is a plain `SUM` of the
column, not a conditional that has to know which types subtract. A sign error
then shows up as visibly wrong arithmetic rather than a silently wrong total.

### Say this

> "Two lines, not one. 'You were paid R250 and we took R2.50' is something the
> merchant can check against their own records. A single net line would hide
> where the difference went, and it would make our own fee income impossible to
> sum as a column."

---

## 2 · The balance is stored even though it can be calculated

**Chosen.** `merchant_balances` holds a running figure, and `ledger_entries`
holds the entries it came from. Both.

**Two obvious alternatives**, and both are worse.

### Alternative A: calculate on every read

`SELECT SUM(amount_zar) FROM ledger_entries WHERE merchant_id = ...`

Correct, and it never drifts. But it is O(n) in the merchant's entire history.
A shop with 100,000 transactions pays for all 100,000 every time the dashboard
loads. That cost grows forever and never comes back down.

### Alternative B: store the balance only, no ledger

One row per merchant, updated on each payment. Fast.

And unauditable. When a merchant asks "why is my balance R2,745 and not R2,800",
there is no answer. You have the number and no way to explain it.

### Why storing both is not redundancy for its own sake

The stored balance is a **cache** of the entries. The entries are the truth.

That makes the redundancy checkable, which is the entire point:

```bash
curl .../reconcile
# stored R2745.27, recomputed R2745.27, matches true
```

If those ever disagree, something wrote the ledger incorrectly, and you find out
by asking rather than by a merchant complaining. A mismatch raises a CRITICAL
security event.

### Say this

> "The balance is a cache of the ledger, not a second source of truth. Storing
> it means showing a balance is one row read instead of summing a merchant's
> whole history. And because the entries are still there, I can recompute and
> compare, which is what the reconcile endpoint does."

---

## 3 · Crypto amounts round up, not to nearest

**Chosen.** `MidpointRounding.ToPositiveInfinity` at eight decimal places.

### The actual numbers

R250 at a rate of R1,756,327.53:

```
exact                0.000142342470712168...
to nearest, 8dp      0.00014234   worth R249.9957
rounded up, 8dp      0.00014235   worth R250.0132
```

Round to nearest and the customer is asked for **R249.9957**. Four tenths of a
cent short.

### Why that matters, when it is that small

It is not the size, it is the **direction**.

Random rounding error averages out: sometimes you gain a fraction, sometimes you
lose one. Rounding to nearest on a division that is always the same shape does
not average out. It is a systematic bias, and it always points the same way.

Over ten thousand invoices at this size, that is **R43.39** the platform quietly
absorbs. Not ruinous. But it is money leaving for no reason anybody decided.

### The principle

When money is rounded, somebody benefits. Decide who, on purpose. The
alternative is not "no bias", it is "a bias nobody chose".

Rounding up means the customer pays at most one satoshi more than exact, and the
merchant is never short.

### Say this

> "Rounding up rather than to nearest, deliberately. Rounding down would ask the
> customer for very slightly less than the invoice is worth, and because that
> bias always points the same way it does not average out over many invoices. It
> is fractions of a cent each time, but it is a systematic loss rather than
> random error."

---

## 4 · Money is `decimal`, never `double`

**Chosen.** `decimal` in C#, `numeric(18,2)` and `numeric(24,8)` in PostgreSQL.

### The demonstration

```
double   0.1 + 0.2  =  0.30000000000000004
decimal  0.1 + 0.2  =  0.3
```

That is not a bug. `double` is binary floating point, and 0.1 has no exact
binary representation, exactly as 1/3 has no exact decimal one. The error is
tiny and it is **always there**.

### Why it compounds here specifically

This system stores a running balance on every ledger row. Each row's
`balance_after` is derived from the one before it. Errors in a chain of derived
values accumulate rather than cancel.

After enough transactions the stored balance and the recomputed sum would
diverge, `/reconcile` would fail, and the cause would be invisible: every
individual number would look right.

### The cost of choosing `decimal`

It is slower. Base-10 arithmetic in software rather than base-2 in hardware,
roughly an order of magnitude.

Completely irrelevant here. This system does a handful of arithmetic operations
per payment, not millions per second. Trading speed you do not need for exactness
you do is an easy call.

### The eight decimal places

That is one satoshi, the smallest divisible unit of Bitcoin. Not an arbitrary
precision.

### Say this

> "Everything financial is decimal, never double. Binary floating point cannot
> represent 0.1 exactly, and this ledger stores a running balance where each row
> derives from the last, so those errors would accumulate rather than cancel.
> Eight decimal places on crypto because that is one satoshi."

---

## 5 · Cancel is an action endpoint, not a field edit

**Chosen.** `POST /api/invoices/{id}/cancel`

**The obvious alternative.** `PATCH /api/invoices/{id}` with
`{"status": "Cancelled"}`. More RESTful-looking. One endpoint instead of several.

### Why the alternative fails

**If status is writable, what stops this?**

```json
PATCH /api/invoices/{id}
{ "status": "Settled" }
```

That is a client asserting it has been paid out. You would have to add
per-value authorisation inside a generic field-update handler: this value is
allowed, that one is not, this one only from these states. That logic is easy to
get subtly wrong and hard to see when reading the code.

**A state transition is not a field edit.** It is an operation with
preconditions. `Pending → Cancelled` is legal; `Settled → Cancelled` is not. The
endpoint name carries the intent, and the server decides whether it is allowed.

**It is what real payment APIs do.** Stripe has `POST /v1/invoices/{id}/void`
and `POST /v1/charges/{id}/refund`, not PATCH with a status field. For the same
reason.

### Say this

> "Cancel is an action, not a field edit. If status were writable through a
> PATCH, a client could set it to Settled, which would be a client claiming it
> had been paid out. Stripe uses the same shape for the same reason."

---

## 6 · Three different status codes for three different failures

**Chosen.** 409, 410 and 422 for three ways a payment can be refused.

### The test for whether this is worth doing

Not "is it more correct". The test is: **does the client behave differently?**

Here it does:

| Code | Meaning | What the merchant's software should do |
|---|---|---|
| **409** | Duplicate transaction, or the invoice is already finished | **Stop.** Retrying will never work and may indicate a bug on their side |
| **410** | The invoice expired | **Create a new invoice.** This one is dead. Retrying this id never succeeds |
| **422** | Underpayment | **Wait.** The request was fine, the customer just owes more. The invoice is still open |

Three genuinely different behaviours. Collapse all three to `400` and the
merchant's code cannot tell "give up" from "start over" from "wait", so it will
guess, and it will guess wrong.

### Why 410 rather than 404

The invoice exists. 404 would say it never did. 410 Gone means: this existed,
it is finished, stop asking. That is precisely the situation.

### Why 422 rather than 400

400 means the request was malformed. An underpayment is a perfectly well-formed
request that the server understood completely. It just does not satisfy the
invoice. 422 Unprocessable Entity is exactly that distinction.

### The bug this hid

Until day 6 the system returned 409 for payments against expired invoices
instead of 410, because the 410 branch only fired in the narrow window before
the expiry sweeper ran. Since the sweeper runs every thirty seconds, the
specified 410 was effectively unreachable.

Every endpoint test passed. It surfaced only from auditing whether every
security event in the catalogue was actually being emitted.

That is worth saying out loud: it is an argument for treating observability as a
correctness tool, not decoration.

### Say this

> "The status codes are chosen so the difference is actionable. 409 means stop,
> 410 means this invoice is gone so request a new one, 422 means the payment was
> short and the customer still owes money. A merchant's integration behaves
> differently in each case, which is the only reason differentiated codes are
> worth the effort."

---

## 7 · The transaction hash is unique in the database, not checked in code

**Chosen.** A unique index on `payments.tx_hash`.

### One line of DDL doing two jobs

**Idempotency.** A blockchain watcher confirming a payment will retry if it does
not get a clean response. The same transaction arriving twice must not credit
the merchant twice.

**Replay detection.** An attacker resubmitting a payment they captured earlier
hits the same constraint.

### Why the database and not the application

The obvious approach is to check first:

```
SELECT * FROM payments WHERE tx_hash = 'abc'
if not found: INSERT
```

That has a race. Two requests arriving at the same moment:

```
Request A   SELECT tx_hash='abc'   → not found
Request B   SELECT tx_hash='abc'   → not found
Request A   INSERT                 → succeeds
Request B   INSERT                 → succeeds
                                     merchant credited twice
```

The window is small. It is not zero, and payment systems are exactly where small
windows get hit, because retries arrive in bursts.

A unique index closes it completely. The second INSERT fails atomically inside
the database. There is no window because the check and the write are the same
operation.

### The code still checks first

Not for correctness, for ergonomics: it lets the service return a clean 409
rather than surfacing a constraint violation. The index is the guarantee; the
check is the good error message.

### Say this

> "The unique index on transaction hash is doing two jobs: it makes payment
> submission idempotent so a retried confirmation cannot double-credit, and it
> is the replay detector. It is in the database rather than in application code
> because an application-level check races with itself when two requests arrive
> at once, and a unique index cannot."

---

## 8 · Three service calls, three different resilience strategies

**This is the strongest thing in the project.** Not that three patterns are
used, but that each was derived rather than picked.

### The two questions that decide it

For any call between services:

1. **What does failure cost?**
2. **What does waiting cost?**

Answer those and the strategy falls out. "Use retries" is not an answer to
anything on its own.

---

### Invoice → Rate: circuit breaker with a cached fallback

**Cost of failure:** we cannot price an invoice.
**Cost of waiting:** a customer is staring at a checkout page that will not load.

Waiting is expensive here, because someone is watching. But failure is cheap,
because **we saw a price a few seconds ago** and prices do not move much in
seconds.

So: fail fast, and use the last price we saw.

The breaker stops Invoice queuing requests against a dependency that is already
failing. Without it, every invoice creation waits out the full timeout, threads
pile up behind a service that cannot answer, and a Rate outage degrades Invoice
too. That is how one service failing takes down three.

**The staleness ceiling.** The cached price is only used for thirty minutes.
Past that a stale rate stops being a degradation and becomes a liability: the
platform would be locking a merchant to a price that no longer reflects the
market and absorbing the difference at settlement. Beyond the ceiling, refusing
is the cheaper mistake.

**The cold-start case.** If Rate has been down since startup, there is no cached
price at all. The service returns 503 rather than inventing a number. There is no
honest answer, so it does not give one.

---

### Invoice → Settlement: retry, against an idempotent endpoint

**Cost of failure:** a merchant was paid and never credited. **This is the worst
failure in the system.**
**Cost of waiting:** nothing. Nobody is watching. The customer has already been
told their payment succeeded.

Failure is catastrophic, waiting is free. So: retry, and keep retrying.

**Which forces the endpoint to be idempotent.** If Settlement is going to be
called repeatedly for the same invoice, a second call must not credit twice.
That is enforced by the unique index on `(invoice_id, entry_type)` — the same
technique as the transaction hash, for the same reason.

The retry and the idempotency are not two decisions. The first requires the
second.

**Plus a sweeper**, because three inline retries over a couple of seconds do not
survive Settlement being redeployed. Anything left in `Paid` gets picked up
later. The invoice sitting in `Paid` rather than `Settled` is the honest state:
the customer's money arrived, the merchant is not yet credited.

---

### Settlement → the merchant's own server: durable queue, backoff, dead letter

**Cost of failure:** the merchant does not know they were paid.
**Cost of waiting:** none, but **we cannot fix their server** and retrying hard
makes their outage worse.

This is the only dependency genuinely outside our control.

**Durable, not in memory.** The queue is a database table, so a restart resumes
rather than losing every pending notification.

**Exponential backoff:** 3s, 6s, 12s, 25s, 45s. The gap doubles so a struggling
server is not hammered. Retrying hard against something that is already
overloaded is indistinguishable from attacking it.

**With jitter**, which matters more than it looks. Without random spread, a
hundred deliveries that failed together retry together forever, arriving as
synchronised bursts that are themselves a small denial of service.

**Bounded, with a dead letter.** After six attempts the row is marked Failed and
**kept**. Retrying forever ties up resources on a server that may never return.
Keeping the row means somebody can see what was never delivered and requeue it.

---

### Say this

> "The three service-to-service calls get three different strategies, and the
> difference is the point. Invoice to Rate is on the critical path, so it fails
> fast and falls back to a cached price: a slightly stale rate beats a checkout
> that hangs. Invoice to Settlement is money, so it retries instead, and the
> endpoint is idempotent specifically to make retrying safe. Settlement to the
> merchant's own server is outside our control, so it gets a durable queue with
> backoff and a dead letter. Each one came from asking what failure costs and
> what waiting costs."

---

## 9 · The checkout page has no login

**Chosen.** The invoice id in the URL is the credential.

### The two alternatives, and why both fail

**Require the merchant's API key.** The key would have to be in a web page the
customer can read. View source and you have a credential that can create
invoices billed to that shop. Catastrophic.

**Build customer accounts.** Nobody registers an account to buy a coffee. It
would destroy the conversion rate the merchant is paying us for.

### What was chosen instead

The invoice id is a version 4 GUID: **122 bits of randomness**. Not sequential,
not derived from anything. Holding the link is the authorisation.

This is a known pattern, sometimes called a capability URL. BitPay and Coinbase
Commerce both use it for checkout. So do Google Docs share links and Calendly.

### What it forces

If the link is the credential, then **the response must contain only what a payer
needs**. `/api/checkout/{id}` deliberately excludes the merchant id and
everything about the merchant's account.

So if a link leaks, what leaks is one payment. Not an account.

That is why checkout is a separate endpoint from `/api/invoices/{id}` rather
than the same one without auth. Different audience, different projection.

### Say this

> "The checkout page needs no key, because the person paying is not the merchant
> and holds no credential. The invoice id is a version 4 GUID, so it is
> unguessable, and holding the link is the authorisation. That is why the
> checkout response deliberately excludes the merchant id: if the link leaks,
> one payment is exposed, not an account."

---

## 10 · Migrations run at startup, and production would not do this

**Chosen.** Each service creates its own tables when it starts, with a retry
loop.

### Why

The deployment criterion rewards a system that comes up from a clean clone with
one command. Automatic migration is the difference between `docker compose up`
working and a marker having to install .NET tooling and run database commands by
hand.

### Why it is wrong in production

Two copies of a service starting at the same time both try to migrate the same
database. They race. Depending on what the migration does, that ranges from one
failing harmlessly to a corrupted schema.

In production, migration is a deliberate step in the deployment pipeline, run
once, before the new version starts.

### Why it is safe here

One replica of each service. Compose starts them one at a time. And the retry
loop exists because a container reported healthy is not always immediately
accepting connections.

### Say this out loud rather than being caught by it

> "Migrations run at startup, which a production system would not do, because
> two replicas starting at once would race each other. It is here because the
> system has to come up from a clean clone with one command, and there is one
> replica of each service. In production this would be a deliberate step in the
> deployment pipeline."

---

# The shape of every answer

If she asks about a decision you have not rehearsed, use this shape:

1. **What it does.** One sentence.
2. **What the alternative was.** Name it specifically.
3. **What breaks if you do it the other way.** Concrete, not abstract.
4. **What it cost.** Every choice costs something. Name it.

The fourth step is the one people skip, and it is the one that reads as
understanding rather than recitation. A decision presented as free is a decision
you have not thought about.
