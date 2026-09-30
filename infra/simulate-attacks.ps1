# Panda Pocket: drive every detection rule on demand.
#
# A demonstration cannot wait for an attack to happen. This produces the exact
# conditions each rule looks for, so the alerts on screen are the real engine
# reacting to real events rather than rows somebody inserted.
#
# Nothing here writes to soc_db. Every scenario goes through the public API or
# stops a container, exactly as a real caller or a real outage would, so the
# events are genuine observations and the rules earn their alerts.
#
# Usage:
#   .\infra\simulate-attacks.ps1                      all scenarios, in order
#   .\infra\simulate-attacks.ps1 -Scenario R1         just credential probing
#   .\infra\simulate-attacks.ps1 -Clean               wipe events and alerts first
#
# Scenarios:
#   R1  credential probing        invalid API keys from one address
#   R2  quota abuse               one merchant far past its rate limit
#   R3  dependency failure        the price feed taken down
#   R5  payout redirection        failed logins, then the payout URL moved
#   R6  checkout enumeration      guessing invoice ids
#   R7  rate manipulation         invoices priced on a stale rate

[CmdletBinding()]
param(
    [ValidateSet('all', 'R1', 'R2', 'R3', 'R5', 'R6', 'R7')]
    [string]$Scenario = 'all',

    [switch]$Clean,

    [string]$Gateway = 'http://localhost:5000',
    [string]$Soc     = 'http://localhost:5005'
)

# Continue, not Stop. Every scenario here deliberately provokes HTTP errors, and
# docker writes ordinary progress to stderr, both of which Windows PowerShell
# would otherwise treat as terminating.
$ErrorActionPreference = 'Continue'

$docker     = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
$root       = Split-Path $PSScriptRoot -Parent
$apiKey     = 'pk_live_demo0000000000000000000000000000000000'
$merchantId = '11111111-1111-1111-1111-111111111111'
$email      = 'owner@democoffee.co.za'
$password   = 'demo-password-123'
$stamp      = Get-Date -Format 'HHmmss'

function Say($text, $colour = 'Gray') { Write-Host "  $text" -ForegroundColor $colour }

function Head($text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Cyan
}

# Every call is expected to fail in most scenarios, so failures are swallowed
# rather than reported. What matters is the event the attempt produced.
function Try-Call($method, $uri, $headers, $body) {
    try {
        if ($body) {
            Invoke-RestMethod -Uri $uri -Method $method -Headers $headers `
                -ContentType 'application/json' -Body $body -TimeoutSec 20 | Out-Null
        }
        else {
            Invoke-RestMethod -Uri $uri -Method $method -Headers $headers -TimeoutSec 20 | Out-Null
        }
        return $true
    }
    catch { return $false }
}

# The circuit breaker stays open for fifteen seconds after it trips, and holds
# it open until a probe succeeds. Invoices created before it closes are still
# priced from the cache, so a scenario that starts too soon after the outage has
# its invoices counted as part of the outage. R7 read 29 invoices instead of 4
# for exactly that reason: the arithmetic was right and the evidence was mixed.
function Wait-ForRateService($timeoutSeconds = 90) {
    Say "waiting for the rate service and the circuit to recover..." DarkGray

    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            # The collection, not a named pair. An earlier version asked for
            # BTC-ZAR, which is not what the service publishes (BTCZAR), so the
            # check 404'd forever and the wait only worked by running out its
            # timeout. Asking for the collection does not depend on which pairs
            # happen to be configured.
            Invoke-RestMethod "$Gateway/api/rates" -TimeoutSec 5 | Out-Null

            # Answering is not the same as the breaker having closed. One more
            # pause covers the half-open probe.
            Start-Sleep -Seconds 20
            Say "rate service is answering again and the circuit has closed." DarkGray
            return
        }
        catch { Start-Sleep -Seconds 5 }
    }
    Say "rate service did not recover within ${timeoutSeconds}s; continuing anyway." Yellow
}

function Wait-ForRules($seconds = 30) {
    Write-Host ""
    Say "waiting ${seconds}s for the publisher flush and one rule cycle..." DarkGray
    Start-Sleep -Seconds $seconds
}

# ---------------------------------------------------------------------------
# R1: credential probing
# ---------------------------------------------------------------------------
function Invoke-R1 {
    Head "R1  Credential probing"
    Say "Presenting eight API keys that do not exist, from this address."
    Say "The gateway rejects each one and records who tried, which is the only"
    Say "thing tying the attempts together: a caller guessing keys has no"
    Say "merchant identity yet." DarkGray

    for ($i = 1; $i -le 8; $i++) {
        Try-Call 'GET' "$Gateway/api/invoices" @{ 'X-API-Key' = "pk_live_probe_${stamp}_$i" } $null | Out-Null
    }
    Say "8 rejected keys sent. Rule fires at 5 in 5 minutes." Green
}

# ---------------------------------------------------------------------------
# R2: quota abuse
# ---------------------------------------------------------------------------
function Invoke-R2 {
    Head "R2  Quota abuse"
    Say "Creating invoices as fast as possible with a valid key."
    Say "The gateway allows 30 writes a minute per merchant. Past that it answers"
    Say "429 and records it. A runaway retry loop looks exactly like this, which" DarkGray
    Say "is why the rule is MEDIUM and the recommended action is to ask." DarkGray

    $throttled = 0
    for ($i = 1; $i -le 45; $i++) {
        $body = "{`"amountZar`":50,`"reference`":`"FLOOD-$stamp-$i`",`"asset`":`"BTC`"}"
        try {
            Invoke-RestMethod "$Gateway/api/invoices" -Method Post -Headers @{ 'X-API-Key' = $apiKey } `
                -ContentType 'application/json' -Body $body -TimeoutSec 20 | Out-Null
        }
        catch {
            if ($_.Exception.Response.StatusCode.value__ -eq 429) { $throttled++ }
        }
    }
    Say "$throttled requests throttled. Rule fires at 10 in 2 minutes." Green
}

# ---------------------------------------------------------------------------
# R3 and R7: the price feed goes down
# ---------------------------------------------------------------------------
function Invoke-RateOutage($alsoCreateInvoices) {
    Head "R3 / R7  The price feed goes down"

    Say "First one invoice while the rate service is up, so the Invoice service"
    Say "has a cached quote to fall back to. Without it there is no honest number" DarkGray
    Say "to price from and the system refuses rather than inventing one." DarkGray

    $body = "{`"amountZar`":100,`"reference`":`"WARM-$stamp`",`"asset`":`"BTC`"}"
    Try-Call 'POST' "$Gateway/api/invoices" @{ 'X-API-Key' = $apiKey } $body | Out-Null

    Say ""
    Say "Stopping rate-service."
    Push-Location $root
    try { & $docker compose stop rate-service | Out-Null } finally { Pop-Location }
    Start-Sleep -Seconds 5

    if ($alsoCreateInvoices) {
        Say "Creating four invoices with no live price."
        for ($i = 1; $i -le 4; $i++) {
            $b = "{`"amountZar`":$($i * 200),`"reference`":`"STALE-$stamp-$i`",`"asset`":`"BTC`"}"
            Try-Call 'POST' "$Gateway/api/invoices" @{ 'X-API-Key' = $apiKey } $b | Out-Null
        }
        Say "Each was priced from the cached rate and said so. That is the" DarkGray
        Say "exposure R7 reports, with the rand figure attached." DarkGray
    }

    # Held down deliberately. R3 needs two independent signals: the breaker
    # opening, which is what a caller concluded, and the registry marking the
    # instance critical, which is what an outside observer saw. Consul polls
    # every ten seconds, so an outage shorter than that can end before it is
    # ever noticed, and R3 then fires on one run and not the next. Waiting makes
    # the demonstration reproducible rather than lucky.
    Say ""
    Say "Holding the outage for 25s so the registry observes it too." DarkGray
    Start-Sleep -Seconds 25

    Say ""
    Say "Restarting rate-service."
    Push-Location $root
    try { & $docker compose start rate-service | Out-Null } finally { Pop-Location }

    Say "Done. R3 fires on the circuit opening plus the failing health check." Green
    if ($alsoCreateInvoices) { Say "R7 fires on 3 or more invoices priced on a fallback." Green }
}

# ---------------------------------------------------------------------------
# R5: payout redirection
# ---------------------------------------------------------------------------
function Invoke-R5 {
    Head "R5  Payout redirection"
    Say "Three failed logins against a real account, then a successful one, then"
    Say "the payout webhook is moved."
    Say ""
    Say "Each step on its own is unremarkable. People mistype passwords and" DarkGray
    Say "merchants change their endpoints. The sequence against one account is" DarkGray
    Say "an account takeover with the money about to follow it." DarkGray

    for ($i = 1; $i -le 3; $i++) {
        $bad = "{`"email`":`"$email`",`"password`":`"wrong-guess-$i`"}"
        Try-Call 'POST' "$Gateway/api/auth/login" @{} $bad | Out-Null
    }
    Say "3 failed logins recorded against the account." DarkGray

    $token = $null
    try {
        $login = Invoke-RestMethod "$Gateway/api/auth/login" -Method Post -ContentType 'application/json' `
            -Body "{`"email`":`"$email`",`"password`":`"$password`"}" -TimeoutSec 20
        $token = $login.token
    }
    catch { Say "could not log in: $($_.Exception.Message)" Red }

    if (-not $token) { Say "Skipping the webhook change; no token." Red; return }
    Say "Logged in. The attacker now holds a session." DarkGray

    # A different destination every run, because the service only raises the
    # event when the URL actually changes.
    $newUrl = "http://attacker-$stamp.example.net/collect"
    $ok = Try-Call 'PUT' "$Gateway/api/merchants/$merchantId" `
        @{ 'Authorization' = "Bearer $token" } "{`"webhookUrl`":`"$newUrl`"}"

    if ($ok) { Say "Payout webhook moved to $newUrl" Yellow }
    else { Say "webhook change refused" Red }

    Say "R5 fires on a webhook change with 3+ credential events behind it." Green
}

# ---------------------------------------------------------------------------
# R6: checkout enumeration
# ---------------------------------------------------------------------------
function Invoke-R6 {
    Head "R6  Checkout enumeration"
    Say "Requesting fourteen checkout pages for invoice ids that do not exist."
    Say "The checkout id is the bearer token for a payment page, so a miss means" DarkGray
    Say "somebody holding a link that was never issued. One is a stale link." DarkGray
    Say "Fourteen in a minute is somebody working through the space." DarkGray

    for ($i = 1; $i -le 14; $i++) {
        Try-Call 'GET' "$Gateway/api/checkout/$([guid]::NewGuid())" @{} $null | Out-Null
    }
    Say "14 misses sent. Rule fires at 10 in 5 minutes." Green
}

# ---------------------------------------------------------------------------
# Clean slate
# ---------------------------------------------------------------------------
function Clear-SocData {
    Head "Clearing collected events and alerts"
    $sql = 'TRUNCATE alert_events, alerts, soc_events;'
    & $docker exec -e PGPASSWORD=soc_pw_dev pp-postgres psql -U soc_svc -d soc_db -c $sql | Out-Null
    if ($LASTEXITCODE -eq 0) { Say "soc_db cleared. The rule catalogue is kept." Green }
    else { Say "could not clear soc_db (is the stack up?)" Red }
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
try { Invoke-RestMethod "$Soc/api/soc/summary" -TimeoutSec 10 | Out-Null }
catch {
    Write-Host ""
    Write-Host "  The SOC service is not answering on $Soc." -ForegroundColor Red
    Write-Host "  Start the stack first:  docker compose up -d" -ForegroundColor Red
    Write-Host ""
    exit 1
}

if ($Clean) { Clear-SocData }

switch ($Scenario) {
    'R1' { Invoke-R1 }
    'R2' { Invoke-R2 }
    'R3' { Invoke-RateOutage $false }
    'R5' { Invoke-R5 }
    'R6' { Invoke-R6 }
    'R7' { Invoke-RateOutage $true }
    'all' {
        # Order matters. The rate outage needs quota to create invoices, and R2
        # deliberately exhausts that quota, so R2 runs last.
        Invoke-R1
        Invoke-R6
        Invoke-R5
        Invoke-RateOutage $true

        # Before R2, because its flood creates invoices too. Run while the
        # breaker is still open and those invoices are priced from the cache as
        # well, which folds R2's traffic into R7's exposure figure and makes
        # neither alert attributable to its own scenario.
        Wait-ForRateService
        Invoke-R2
    }
}

Wait-ForRules 35

Head "Alerts raised"
try {
    $alerts = Invoke-RestMethod "$Soc/api/soc/alerts" -TimeoutSec 20
    if ($alerts.totalCount -eq 0) {
        Say "none yet. Rules evaluate every 20s; try again shortly." Yellow
    }
    foreach ($a in $alerts.items) {
        $colour = switch ($a.severity) { 'CRITICAL' { 'Red' } 'HIGH' { 'Yellow' } default { 'Gray' } }
        Write-Host ("  [{0}] {1,-8} {2}" -f $a.ruleId, $a.severity, $a.ruleName) -ForegroundColor $colour
        Write-Host ("        {0}" -f $a.description) -ForegroundColor DarkGray
        Write-Host ("        {0} events" -f $a.eventCount) -ForegroundColor DarkGray
    }
}
catch { Say "could not read alerts: $($_.Exception.Message)" Red }

Write-Host ""
Say "Alert detail, with every event behind it:  $Soc/api/soc/alerts/{id}" DarkGray
Write-Host ""
