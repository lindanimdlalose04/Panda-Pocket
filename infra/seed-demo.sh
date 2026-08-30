#!/usr/bin/env bash
# Panda Pocket: populate the system with realistic demo data.
#
# Everything here goes through the PUBLIC API via the gateway, not by writing to
# the databases. That is deliberate: seeding through the same endpoints a
# merchant would use means the script doubles as proof the API works end to end,
# and it cannot drift from reality the way direct SQL inserts silently do.
#
# Creates three merchants, each with its own API key, and invoices covering
# every state the machine can reach: settled, underpaid, pending, cancelled.
#
# Usage:  bash infra/seed-demo.sh
#
# Idempotent-ish: references carry a run stamp, so it can be run repeatedly
# without colliding with the unique (merchant_id, reference) index.

set -u

GW="${GW:-http://localhost:5000}"
STAMP=$(date +%H%M%S)
ORDER=$(date +%H%M)   # order numbers look like a real till, and stay unique per run

green() { printf '\033[32m%s\033[0m\n' "$1"; }
dim()   { printf '\033[2m%s\033[0m\n' "$1"; }
bold()  { printf '\033[1m%s\033[0m\n' "$1"; }

api() { curl -s -H "Content-Type: application/json" "$@"; }

# ---------------------------------------------------------------------------
# Wait for the gateway, so this can be run immediately after docker compose up.
# ---------------------------------------------------------------------------
printf "Waiting for the gateway"
for _ in $(seq 1 60); do
  if curl -s -o /dev/null --max-time 2 "$GW/health"; then break; fi
  printf "."
  sleep 2
done
echo " ready"
echo

# ---------------------------------------------------------------------------
# Merchants. The demo merchant already exists from the Merchant service seeder,
# so only the extra two are created here. A second and third merchant matter for
# the demo: they make the 403 checks meaningful, and give the future knowledge
# graph more than one node to draw edges between.
# ---------------------------------------------------------------------------
bold "Creating merchants"

create_merchant() {
  local name="$1" email="$2" fee="$3"
  api -X POST "$GW/api/merchants" -d "{
    \"businessName\": \"$name\",
    \"email\": \"$email\",
    \"password\": \"demo-password-123\",
    \"feePercent\": $fee
  }" > /dev/null 2>&1
  dim "  $name ($email) at ${fee}%"
}

create_merchant "Rosebank Roastery"  "owner@rosebankroast.co.za" 1.0
create_merchant "Kalk Bay Books"     "owner@kalkbaybooks.co.za"  1.5
echo

# ---------------------------------------------------------------------------
# Invoices for the demo merchant, covering the whole state machine.
# ---------------------------------------------------------------------------
KEY="${DEMO_KEY:-pk_live_demo0000000000000000000000000000000000}"

new_invoice() {
  local amount="$1" ref="$2" asset="$3"
  api -X POST "$GW/api/invoices" -H "X-API-Key: $KEY" \
    -d "{\"amountZar\": $amount, \"reference\": \"$ref\", \"asset\": \"$asset\"}"
}

field() { python -c "import sys,json;print(json.load(sys.stdin).get('$1',''))" 2>/dev/null; }

bold "Creating invoices"
N=0

# --- settled: paid in full, ledger written, webhook queued ---
for spec in "250:Flat white and a croissant:BTC" \
            "480:Two lunches:BTC" \
            "1250:Coffee beans, 5kg:ETH" \
            "89:Single espresso:USDT"; do
  amount="${spec%%:*}"; rest="${spec#*:}"; label="${rest%%:*}"; asset="${rest##*:}"
  N=$((N + 1))
  resp=$(new_invoice "$amount" "ORDER-$ORDER$N" "$asset")
  id=$(echo "$resp" | field id)
  crypto=$(echo "$resp" | field cryptoAmount)
  [ -z "$id" ] && continue

  api -X POST "$GW/api/invoices/$id/payments" -H "X-API-Key: $KEY" \
    -d "{\"txHash\": \"tx-seed-$STAMP-$RANDOM$RANDOM\", \"amountCrypto\": $crypto}" > /dev/null
  green "  settled    R$amount  $label"
done

# --- underpaid: a real payment that does not cover the invoice ---
N=$((N + 1)); resp=$(new_invoice 600 "ORDER-$ORDER$N" "BTC")
id=$(echo "$resp" | field id); crypto=$(echo "$resp" | field cryptoAmount)
if [ -n "$id" ]; then
  half=$(python -c "print(round($crypto * 0.4, 8))")
  api -X POST "$GW/api/invoices/$id/payments" -H "X-API-Key: $KEY" \
    -d "{\"txHash\": \"tx-seed-$STAMP-under\", \"amountCrypto\": $half}" > /dev/null
  printf '\033[33m  underpaid  R600  Catering deposit\033[0m\n'
fi

# --- cancelled: the merchant changed their mind ---
N=$((N + 1)); resp=$(new_invoice 175 "ORDER-$ORDER$N" "BTC")
id=$(echo "$resp" | field id)
if [ -n "$id" ]; then
  api -X POST "$GW/api/invoices/$id/cancel" -H "X-API-Key: $KEY" \
    -d '{"reason": "Customer changed their mind"}' > /dev/null
  printf '\033[31m  cancelled  R175  Cancelled order\033[0m\n'
fi

# --- pending: left open, so the demo has a live countdown and a checkout link ---
bold ""
bold "Leaving these pending, for the checkout demo"
for spec in "250:Table 4 bill:BTC" "1500:Monthly subscription:ETH"; do
  amount="${spec%%:*}"; rest="${spec#*:}"; label="${rest%%:*}"; asset="${rest##*:}"
  N=$((N + 1))
  resp=$(new_invoice "$amount" "ORDER-$ORDER$N" "$asset")
  id=$(echo "$resp" | field id)
  [ -z "$id" ] && continue
  printf '\033[34m  pending    R%-6s %s\033[0m\n' "$amount" "$label"
  echo "             $GW/checkout.html?id=$id"
done

echo
bold "Done."
dim "  Merchant view : $GW"
dim "  Logs          : http://localhost:5341"
dim "  Webhook sink  : $GW/demo/webhook-sink"
echo
dim "Webhook deliveries for the demo merchant will be failing against"
dim "http://localhost:9999 by design, which is what makes the retry and"
dim "backoff visible. Repoint the webhook at $GW/demo/webhook-sink"
dim "to watch one succeed and have its signature verified."
