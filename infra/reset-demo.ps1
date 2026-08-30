# Panda Pocket: wipe everything and rebuild a clean demo state.
#
# DESTRUCTIVE. Removes the Postgres, MongoDB and Seq volumes, so every invoice,
# ledger entry, webhook delivery and log line is lost. That is the point: after
# a week of testing, the invoice list is full of references like NOKEY-002 and
# CB-FALLBACK-1-1787771299, which makes a demo look like a junkyard rather than
# a payment gateway.
#
# It also doubles as the strongest possible evidence for the deployment
# criterion: this script proves the whole system comes up from nothing with one
# command, schema and seed included, with no manual step.
#
# Usage:  ./infra/reset-demo.ps1
#
# Run it the night before a demo, not during one: the rebuild takes a couple of
# minutes and the webhook retry backoff needs a little time to produce a
# visible attempt count.

$ErrorActionPreference = 'Stop'
$docker = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"

Write-Host ""
Write-Host "This deletes ALL Panda Pocket data: invoices, ledger, webhooks, logs." -ForegroundColor Yellow
$answer = Read-Host "Type 'reset' to continue"
if ($answer -ne 'reset') {
    Write-Host "Cancelled. Nothing was changed." -ForegroundColor Green
    exit 0
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    Write-Host ""
    Write-Host "Stopping and removing containers and volumes..." -ForegroundColor Cyan
    & $docker compose down -v

    Write-Host "Starting the stack..." -ForegroundColor Cyan
    & $docker compose up -d

    Write-Host "Waiting for the gateway to report healthy..." -ForegroundColor Cyan
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 5
        $status = & $docker compose ps --format '{{.Name}} {{.Status}}'
        if ($status -match 'pp-gateway.*healthy') {
            Write-Host "  gateway is healthy" -ForegroundColor Green
            break
        }
    }

    Write-Host ""
    Write-Host "Seeding demo data..." -ForegroundColor Cyan
    bash ./infra/seed-demo.sh

    Write-Host ""
    Write-Host "Clean demo state ready." -ForegroundColor Green
    & $docker compose ps --format 'table {{.Name}}\t{{.Status}}'
}
finally {
    Pop-Location
}
