#!/usr/bin/env bash
# Panda Pocket: proof of database-per-service isolation.
#
# Runs psql inside the Postgres container as each service role and shows that
# every role reaches exactly one database and is refused the other two.
#
# Usage:  bash infra/verify-isolation.sh
# Demo:   screenshot the FAIL lines. They are the evidence, not the errors.

set -u

PASS_merchant_svc=merchant_pw_dev
PASS_invoice_svc=invoice_pw_dev
PASS_settlement_svc=settlement_pw_dev

DBS="merchant_db invoice_db settlement_db"
ROLES="merchant_svc invoice_svc settlement_svc"

green() { printf '\033[32m%s\033[0m\n' "$1"; }
red()   { printf '\033[31m%s\033[0m\n' "$1"; }

for role in $ROLES; do
  eval "pw=\$PASS_${role}"
  echo
  echo "=== $role ==="
  for db in $DBS; do
    out=$(docker exec -e PGPASSWORD="$pw" pp-postgres \
            psql -U "$role" -d "$db" -tAc "select current_user || '@' || current_database()" 2>&1)
    if [ $? -eq 0 ]; then
      green "  CONNECTED  $db   -> $out"
    else
      red   "  REFUSED    $db   -> $(echo "$out" | grep -i 'permission denied\|FATAL' | head -1)"
    fi
  done
done

echo
echo "Expected: one CONNECTED and two REFUSED per role."
