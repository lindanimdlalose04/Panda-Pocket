# Panda Pocket runbook

How to start the system, use it, and show it working. Written for someone who
has not used Docker, Consul, Seq or Ocelot before.

---

## Part 1: what the pieces are

You do not need to install .NET, PostgreSQL, MongoDB or anything else. Docker
runs all of it. You need **Docker Desktop** and nothing more.

| Piece | What it is | Why it is here |
|---|---|---|
| **Docker** | Runs each part of the system in its own isolated box, called a container | Nine containers start with one command, and none of them can interfere with software already on your machine |
| **Docker Compose** | A single file listing all nine containers and how they connect | `docker compose up` starts everything in the right order |
| **Ocelot** | The API gateway. A .NET library | One public front door. Everything the browser calls goes through it |
| **Consul** | The service registry | Services announce themselves to it; the gateway asks it where to send traffic |
| **Seq** | A log viewer with a web page | Every service sends its logs here, so one screen shows all of them |
| **Swagger** | Auto-generated API documentation with a "try it" button | Proves the endpoints exist and lets you call them without writing code |
| **PostgreSQL** | A relational database | Holds merchants, invoices and the money ledger |
| **MongoDB** | A document database | Holds the price tick history |

---

## Part 2: starting it

### First time, or after a reboot

**1. Start Docker Desktop.** Wait until its whale icon stops animating. Nothing
below works until Docker is actually running.

**2. Open a terminal in the project folder:**

```bash
cd "C:\HONS\Databases\Panda Pocket"
```

**3. Start everything:**

```bash
docker compose up -d
```

`-d` means detached, so it runs in the background and gives your prompt back.

First run takes two to five minutes because Docker downloads the base images.
Later runs take about thirty seconds.

**4. Check all nine came up:**

```bash
docker compose ps
```

You want nine rows, all saying `running`, and `(healthy)` on the eight that have
health checks. If any say `starting`, wait twenty seconds and run it again.

### If Docker Desktop refuses to start

This has happened on most build days on this machine. Docker leaves broken
socket files behind when it does not shut down cleanly, and then will not start,
naming a different file each attempt.

```powershell
.\infra\fix-docker-sockets.ps1
```

Takes about thirty seconds. **Run this before recording anything.**

### Filling it with demo data

A fresh database has one merchant and no invoices, which looks empty on camera.

```powershell
.\infra\seed-demo.ps1
```

On Linux or macOS, `bash infra/seed-demo.sh` does the same thing.

Creates three merchants, each with its own API key, and invoices in every state:
settled, underpaid, pending and cancelled. It does this through the public API,
not by writing to the database, so it also proves the API works.

### Starting completely fresh

```powershell
.\infra\reset-demo.ps1
```

Destroys all data, rebuilds, restarts and reseeds. Takes about a minute. Use it
before a final recording so nothing from testing is on screen.

### Stopping

```bash
docker compose stop      # stop, keep data
docker compose down      # stop and remove containers, keep data
docker compose down -v   # stop and delete data as well
```

---

## Part 3: the addresses

| What | Address | Use it for |
|---|---|---|
| **Merchant dashboard** | http://localhost:5000 | **Start here.** The main screen |
| Customer checkout | http://localhost:5000/checkout.html?id=... | What a shopper sees |
| **Consul** | http://localhost:8500 | The service registry |
| **Seq** | http://localhost:5341 | All logs from all services |
| Invoice API docs | http://localhost:5002/swagger | Endpoint documentation |
| Merchant API docs | http://localhost:5001/swagger | |
| Rate API docs | http://localhost:5003/swagger | |
| Settlement API docs | http://localhost:5004/swagger | |

The four service ports are published for development and demonstration. In a
real deployment only the gateway on 5000 would be reachable.

### The demo account

| | |
|---|---|
| Dashboard login | `owner@democoffee.co.za` / `demo-password-123` |
| API key | `pk_live_demo0000000000000000000000000000000000` |

Already filled in on the page. You do not need to type it.

---

## Part 4: using the merchant dashboard

Open **http://localhost:5000**. Six panels.

### Live rates (top left)

Three prices in rand, refreshing every five seconds. They come from a simulator
inside the Rate service, not a real exchange, so the demo cannot be broken by
somebody else's website being down.

Watch USDTZAR. It barely moves while BTCZAR wanders, because it is configured as
a stablecoin. That is a deliberate detail, not an accident.

### API key (left)

Pre-filled with the demo key. Every call the page makes sends it.

**"Clear key (to see the 401)"** empties the field. Create an invoice after that
and the gateway rejects it with 401 Unauthorized before it reaches any service.
Put the key back by refreshing the page.

### Create invoice (left)

Amount in rand, your own reference, and which crypto. Press **Create invoice**.

What happens in the second that follows:

1. Browser sends it to the gateway with the API key.
2. Gateway checks the key with the Merchant service, and stamps a tracking id.
3. Gateway asks Consul where the Invoice service is.
4. Invoice asks Rate for a price and **locks it**.
5. Invoice works out the crypto amount, saves it, starts a fifteen minute timer.

The green line underneath shows the crypto amount, the locked rate, and a
correlation id you can paste into Seq.

**The locked rate is the whole product.** The merchant is quoted R250 and gets
R250 whatever Bitcoin does in those fifteen minutes.

### Invoices (right)

Every invoice, newest first, with a live countdown. Status colours: blue
Pending, amber Underpaid, green Paid or Settled, red Expired or Cancelled.

Buttons on a live invoice:

- **Pay** pays the full amount. Status goes to Settled.
- **Underpay** pays half. Status goes to Underpaid, and the invoice stays open.
  Press **Pay** afterwards to top it up.
- **Cancel** cancels it.
- **Checkout** opens the customer-facing page.
- **Replay** appears on finished invoices. It resends a payment that was already
  recorded, and is rejected with 409. That is replay protection working.

### ZAR ledger (right)

Three figures, then the statement.

Every settled invoice writes **two** lines, not one:

```
Credit  +250.00   balance 250.00
Fee       -2.50   balance 247.50
```

"You were paid R250 and we took R2.50" is checkable. A single R247.50 line would
hide where the difference went.

Underneath: *"Reconciled: stored R2 048.31 equals the sum of the ledger."* The
balance is a cached figure, and that line proves it still matches the entries it
came from.

### Webhook deliveries (right)

When an invoice settles, the system notifies the merchant's own server.

The demo merchant's address is deliberately broken, so deliveries fail and retry
with a growing gap: about 3s, 6s, 12s, 25s, 45s. After six attempts the row goes
red and stays, so nothing is silently lost. **Retry** puts it back in the queue.

---

## Part 5: the customer checkout

Press **Checkout** on a pending invoice, or open
`http://localhost:5000/checkout.html?id=<invoice id>`.

This is what a shopper sees: the rand amount, the crypto amount, the address to
pay, and a countdown. **Copy** copies the address.

It needs no API key. The customer is not the merchant and has no credential, so
the unguessable invoice id in the URL is the authorisation, which is how BitPay
and Coinbase Commerce work too.

**Simulate wallet payment** stands in for a real wallet plus a service watching
the blockchain. Press it and the page turns green.

Let the countdown reach zero instead and the page turns red. Paying then gives
**410 Gone**, which tells the shop to issue a fresh invoice rather than retry.

---

## Part 6: looking behind the scenes

### Consul, the service registry (http://localhost:8500)

Click **Services**. Four services, each with a green tick.

Click one, then the instance. It shows the address the gateway routes to
(`invoice-service:8080`), a unique instance id, and a health check Consul runs
every ten seconds.

**The thing to demonstrate:**

```bash
docker compose stop settlement-service
```

Refresh Consul. It has gone. The dashboard's ledger panel stops loading while
every other panel keeps working.

```bash
docker compose start settlement-service
```

Refresh again. Back, with a **new instance id**. No restart of anything else, no
configuration changed.

That is the difference between a registry and a list of addresses in a file.

### Seq, the logs (http://localhost:5341)

Every service sends its logs here. Paste a correlation id from the dashboard into
the search box:

```
CorrelationId = 'the-id-you-copied'
```

One payment appears crossing Gateway, Invoice, Rate and Settlement in order.
That is one request traced through four separate programs.

Other useful searches:

```
Service = 'Invoice'
EventType = 'CIRCUIT_OPENED'
EventType = 'PAYMENT_REPLAY_ATTEMPT'
@Level = 'Error'
```

There are eleven security event types. They exist for the SOC and knowledge
graph work in the next deliverable.

### Swagger, the API docs (http://localhost:5002/swagger)

Every endpoint, its parameters and its responses, generated from the running
code. Expand one and press **Try it out** to call it.

---

## Part 7: the three failure demonstrations

These carry more marks than the happy path, because they show the patterns
actually working rather than merely being configured.

### 1. Circuit breaker

```bash
# create one invoice first, so a price is cached
docker compose stop rate-service
```

Create another invoice. **It still works.** The Rate service is gone, so Invoice
uses the last price it saw. Open the invoice's history and it says so:

```
Invoice created on a cached rate, 53s old (rate-service unavailable)
```

In Seq, search `EventType = 'CIRCUIT_OPENED'`. The `reason` field distinguishes
the breaker being closed and the call failing from the breaker being open and
the call being refused outright.

```bash
docker compose start rate-service
```

Logs show `OPENED` then `HALF-OPEN` then `CLOSED` as it recovers by itself.

### 2. Webhook retry

Already running. Look at the Webhook deliveries panel, or:

```bash
docker logs pp-settlement | grep "attempt"
```

Attempt counts climbing with a widening gap, then dead-lettered after six.

### 3. Database isolation

```bash
bash infra/verify-isolation.sh
```

Three database users tried against three databases. Each reaches exactly its own
and is refused the other two:

```
FATAL:  permission denied for database "merchant_db"
DETAIL:  User does not have CONNECT privilege.
```

The refusals are the evidence.

---

## Part 8: when something is wrong

**Nothing loads at localhost:5000**

```bash
docker compose ps
```

If `pp-gateway` is missing or unhealthy:

```bash
docker logs pp-gateway --tail 50
docker compose restart gateway
```

**Docker Desktop will not start**

```powershell
.\infra\fix-docker-sockets.ps1
```

**Everything is 401**

The API key field is empty. Refresh the page to restore it.

**Everything is 429**

You hit the rate limit, thirty invoice requests a minute. Wait a minute. This is
the system working.

**Ledger panel empty**

Nothing has settled yet. Create an invoice and press Pay.

**Ports already in use**

Something else on your machine is using 5000, 5001, 5432 or 8500. Find it:

```powershell
Get-NetTCPConnection -LocalPort 5000 -State Listen
```

Note that MongoDB is published on **27018**, not the usual 27017, because this
machine already runs its own MongoDB.

**Start over completely**

```powershell
.\infra\reset-demo.ps1
```

---

## Part 9: a demo order that works

Roughly eight minutes, matching the order the mark schedule asks for.

1. **Camera on**, say who you are, camera off.
2. **The dashboard.** Create an invoice, press Pay, show it reach Settled, show
   the two ledger lines and the reconciliation.
3. **The gateway.** Show `ocelot.json`: routes name a service, not a host. Show
   in Seq that the request went through the Gateway before reaching Invoice.
4. **The registry.** Consul UI, four services with addresses and health. Stop
   settlement, refresh, start it, refresh, new instance id.
5. **Docker.** `docker compose ps`, nine containers. Show `docker-compose.yml`.
6. **The two patterns.** Circuit breaker: stop Rate, create an invoice, show the
   cached rate in the history and `CIRCUIT_OPENED` in Seq. Webhook retry: show
   the climbing attempt counts and the dead letter.
7. **Security events.** Seq filtered by `EventType`, the eleven types, and the
   audit trail that becomes graph edges.

Practise once before recording. The mark schedule says: *"If you do not show an
aspect, I cannot give you the marks."*
