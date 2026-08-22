#!/usr/bin/env bash
# Panda Pocket: proof of database-per-service isolation.
#
# Every service role is tried against every database. Each role must reach
# exactly one and be refused the other two.
#
# Usage:  bash infra/verify-isolation.sh
# Demo:   screenshot the REFUSED lines. They are the evidence, not the errors.
#
# Prefers a real TCP connection from the host, because the refusal message is
# more explicit that way ("DETAIL: User does not have CONNECT privilege") and
# because a client crossing the network boundary is a more honest demonstration
# than one already inside the container. Falls back to docker exec.

set -u

PSQL=""
if command -v psql >/dev/null 2>&1; then
  PSQL="psql"
elif [ -x "${LOCALAPPDATA:-}/Programs/pgAdmin 4/runtime/psql.exe" ]; then
  PSQL="${LOCALAPPDATA:-}/Programs/pgAdmin 4/runtime/psql.exe"
elif [ -x "/c/Users/${USERNAME:-${USER:-}}/AppData/Local/Programs/pgAdmin 4/runtime/psql.exe" ]; then
  PSQL="/c/Users/${USERNAME:-${USER:-}}/AppData/Local/Programs/pgAdmin 4/runtime/psql.exe"
fi

if [ -n "$PSQL" ]; then
  echo "Mode: host psql over TCP localhost:5432"
else
  echo "Mode: docker exec (no host psql found)"
fi

DBS="merchant_db invoice_db settlement_db"
ROLES="merchant_svc invoice_svc settlement_svc"
QUERY="select current_user || '@' || current_database()"

green() { printf '\033[32m%s\033[0m\n' "$1"; }
red()   { printf '\033[31m%s\033[0m\n' "$1"; }

fail=0
for role in $ROLES; do
  case "$role" in
    merchant_svc)   pw=merchant_pw_dev ;;
    invoice_svc)    pw=invoice_pw_dev ;;
    settlement_svc) pw=settlement_pw_dev ;;
  esac
  own="${role%_svc}_db"

  echo
  echo "=== $role ==="
  for db in $DBS; do
    if [ -n "$PSQL" ]; then
      out=$(PGPASSWORD="$pw" "$PSQL" -h localhost -p 5432 -U "$role" -d "$db" -tAc "$QUERY" 2>&1)
    else
      out=$(docker exec -e PGPASSWORD="$pw" pp-postgres psql -U "$role" -d "$db" -tAc "$QUERY" 2>&1)
    fi
    rc=$?

    if [ $rc -eq 0 ]; then
      green "  CONNECTED  $(printf '%-15s' "$db") -> $(echo "$out" | tr -d '\r' | head -1)"
      [ "$db" = "$own" ] || fail=$((fail+1))
    else
      detail=$(echo "$out" | grep -o 'permission denied for database "[^"]*"' | head -1)
      [ -n "$detail" ] || detail=$(echo "$out" | grep -i 'FATAL' | head -1)
      red   "  REFUSED    $(printf '%-15s' "$db") -> $detail"
      [ "$db" = "$own" ] && fail=$((fail+1))
    fi
  done
done

echo
if [ $fail -eq 0 ]; then
  green "PASS: each role reached exactly its own database."
else
  red "FAIL: $fail unexpected result(s). Isolation is not what init.sql intends."
  exit 1
fi
