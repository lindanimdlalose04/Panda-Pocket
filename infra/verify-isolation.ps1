# Panda Pocket: proof of database-per-service isolation.
#
# Every service role is tried against every database. Each role must reach
# exactly one and be refused the other two.
#
# Usage:  ./infra/verify-isolation.ps1
# Demo:   screenshot the REFUSED lines. They are the evidence, not the errors.
#
# Prefers a real TCP connection from the host, because the refusal message is
# more explicit that way:
#
#   FATAL:  permission denied for database "merchant_db"
#   DETAIL:  User does not have CONNECT privilege.
#
# and because a client crossing the network boundary is a more honest
# demonstration than one already inside the container. Falls back to
# `docker exec` if no psql is on the host.

$ErrorActionPreference = 'Continue'

# Locate a psql: PATH first, then the copy bundled with pgAdmin 4.
$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $psql) {
  $bundled = "$env:LOCALAPPDATA\Programs\pgAdmin 4\runtime\psql.exe"
  if (Test-Path $bundled) { $psql = $bundled }
}
$mode = if ($psql) { "host psql over TCP localhost:5432" } else { "docker exec (no host psql found)" }
Write-Host "Mode: $mode" -ForegroundColor DarkGray

$creds = [ordered]@{
  merchant_svc   = 'merchant_pw_dev'
  invoice_svc    = 'invoice_pw_dev'
  settlement_svc = 'settlement_pw_dev'
}
$dbs = @('merchant_db', 'invoice_db', 'settlement_db')

$fail = 0
foreach ($role in $creds.Keys) {
  Write-Host ""
  Write-Host "=== $role ===" -ForegroundColor Cyan
  foreach ($db in $dbs) {
    $env:PGPASSWORD = $creds[$role]
    $q = "select current_user || '@' || current_database()"

    if ($psql) {
      $out = & $psql -h localhost -p 5432 -U $role -d $db -tAc $q 2>&1
    } else {
      $out = docker exec -e "PGPASSWORD=$($creds[$role])" pp-postgres psql -U $role -d $db -tAc $q 2>&1
    }
    $ok = ($LASTEXITCODE -eq 0)

    $expected = ($db -eq ($role -replace '_svc$', '_db'))
    if ($ok) {
      Write-Host ("  CONNECTED  {0,-15} -> {1}" -f $db, (($out -join ' ').Trim())) -ForegroundColor Green
      if (-not $expected) { $fail++ }
    } else {
      $detail = ($out -join ' ') -replace '.*(permission denied for database "[^"]+").*', '$1'
      Write-Host ("  REFUSED    {0,-15} -> {1}" -f $db, $detail.Trim()) -ForegroundColor Red
      if ($expected) { $fail++ }
    }
  }
}
Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue

Write-Host ""
if ($fail -eq 0) {
  Write-Host "PASS: each role reached exactly its own database." -ForegroundColor Green
} else {
  Write-Host "FAIL: $fail unexpected result(s). Isolation is not what init.sql intends." -ForegroundColor Red
  exit 1
}
