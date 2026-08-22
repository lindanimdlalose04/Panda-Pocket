# Panda Pocket: proof of database-per-service isolation (PowerShell version).
#
# Usage:  ./infra/verify-isolation.ps1
# Demo:   screenshot the REFUSED lines. They are the evidence, not the errors.

$creds = @{
  merchant_svc   = 'merchant_pw_dev'
  invoice_svc    = 'invoice_pw_dev'
  settlement_svc = 'settlement_pw_dev'
}
$dbs = @('merchant_db', 'invoice_db', 'settlement_db')

foreach ($role in $creds.Keys) {
  Write-Host ""
  Write-Host "=== $role ===" -ForegroundColor Cyan
  foreach ($db in $dbs) {
    $out = docker exec -e "PGPASSWORD=$($creds[$role])" pp-postgres `
             psql -U $role -d $db -tAc "select current_user || '@' || current_database()"
    if ($LASTEXITCODE -eq 0) {
      Write-Host ("  CONNECTED  {0,-15} -> {1}" -f $db, ($out -join '').Trim()) -ForegroundColor Green
    } else {
      Write-Host ("  REFUSED    {0,-15}" -f $db) -ForegroundColor Red
    }
  }
}

Write-Host ""
Write-Host "Expected: one CONNECTED and two REFUSED per role."
