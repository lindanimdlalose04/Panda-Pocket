# Panda Pocket: populate the system with realistic demo data.
#
# PowerShell equivalent of seed-demo.sh, so the whole demo can be driven from
# one shell. This machine has a broken WSL bash on PATH and Git Bash somewhere
# else, which makes "bash infra/seed-demo.sh" a live-on-camera failure waiting
# to happen.
#
# Everything here goes through the PUBLIC API via the gateway, not by writing to
# the databases. Seeding through the same endpoints a merchant would use means
# this script doubles as proof the API works end to end, and it cannot drift
# from reality the way direct SQL inserts silently do.
#
# Creates two extra merchants alongside the seeded demo one, and invoices
# covering every state the machine can reach: settled, underpaid, cancelled and
# pending.
#
# Usage:  .\infra\seed-demo.ps1

$ErrorActionPreference = 'Continue'

$GW    = if ($env:GW) { $env:GW } else { "http://localhost:5000" }
$KEY   = if ($env:DEMO_KEY) { $env:DEMO_KEY } else { "pk_live_demo0000000000000000000000000000000000" }
$STAMP = Get-Date -Format "HHmmss"
$ORDER = Get-Date -Format "HHmm"

function Ok($t)   { Write-Host "  $t" -ForegroundColor Green }
function Dim($t)  { Write-Host "  $t" -ForegroundColor DarkGray }
function Head($t) { Write-Host $t -ForegroundColor White }

function Post($path, $body, $withKey = $true) {
    $headers = @{ "Content-Type" = "application/json" }
    if ($withKey) { $headers["X-API-Key"] = $KEY }
    try {
        return Invoke-RestMethod -Uri "$GW$path" -Method Post -Headers $headers `
                                 -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 15
    } catch { return $null }
}

# ---------------------------------------------------------------------------
# Wait for the gateway, so this can run straight after docker compose up.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host -NoNewline "Waiting for the gateway"
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        Invoke-WebRequest -Uri "$GW/health" -TimeoutSec 2 -UseBasicParsing | Out-Null
        $ready = $true; break
    } catch { Write-Host -NoNewline "."; Start-Sleep -Seconds 2 }
}
if (-not $ready) {
    Write-Host ""
    Write-Host "  The gateway never answered. Is the system running?" -ForegroundColor Red
    Write-Host "  Try:  docker compose ps" -ForegroundColor Red
    exit 1
}
Write-Host " ready"
Write-Host ""

# ---------------------------------------------------------------------------
# Merchants
# ---------------------------------------------------------------------------
Head "Creating merchants"
foreach ($m in @(
    @{ name = "Rosebank Roastery"; email = "owner@rosebankroast.co.za"; fee = 1.0 },
    @{ name = "Kalk Bay Books";    email = "owner@kalkbaybooks.co.za";  fee = 1.5 }
)) {
    Post "/api/merchants" @{
        businessName = $m.name; email = $m.email
        password = "demo-password-123"; feePercent = $m.fee
    } $false | Out-Null
    Dim "$($m.name) ($($m.email)) at $($m.fee)%"
}
Write-Host ""

# ---------------------------------------------------------------------------
# Settled invoices
# ---------------------------------------------------------------------------
$n = 0
Head "Creating invoices"

foreach ($s in @(
    @{ amount = 250;  label = "Flat white and a croissant"; asset = "BTC" },
    @{ amount = 480;  label = "Two lunches";                asset = "BTC" },
    @{ amount = 1250; label = "Coffee beans, 5kg";          asset = "ETH" },
    @{ amount = 89;   label = "Single espresso";            asset = "USDT" }
)) {
    $n++
    $inv = Post "/api/invoices" @{ amountZar = $s.amount; reference = "ORDER-$ORDER$n"; asset = $s.asset }
    if (-not $inv) { continue }

    Post "/api/invoices/$($inv.id)/payments" @{
        txHash = "tx-seed-$STAMP-$(Get-Random)$(Get-Random)"
        amountCrypto = $inv.cryptoAmount
    } | Out-Null

    Ok ("settled    R{0,-6} {1}" -f $s.amount, $s.label)
}

# Underpaid: pays 40 percent, so the invoice stays open and can be topped up.
$n++
$inv = Post "/api/invoices" @{ amountZar = 600; reference = "ORDER-$ORDER$n"; asset = "BTC" }
if ($inv) {
    $part = [math]::Round($inv.cryptoAmount * 0.4, 8)
    Post "/api/invoices/$($inv.id)/payments" @{
        txHash = "tx-seed-$STAMP-under"; amountCrypto = $part
    } | Out-Null
    Write-Host "  underpaid  R600   Catering deposit" -ForegroundColor Yellow
}

# Cancelled
$n++
$inv = Post "/api/invoices" @{ amountZar = 175; reference = "ORDER-$ORDER$n"; asset = "BTC" }
if ($inv) {
    Post "/api/invoices/$($inv.id)/cancel" @{ reason = "Customer changed their mind" } | Out-Null
    Write-Host "  cancelled  R175   Cancelled order" -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# Pending, left open on purpose so the checkout page has something live
# ---------------------------------------------------------------------------
Write-Host ""
Head "Leaving these pending, for the checkout demo"

foreach ($s in @(
    @{ amount = 250;  label = "Table 4 bill";         asset = "BTC" },
    @{ amount = 1500; label = "Monthly subscription"; asset = "ETH" }
)) {
    $n++
    $inv = Post "/api/invoices" @{ amountZar = $s.amount; reference = "ORDER-$ORDER$n"; asset = $s.asset }
    if (-not $inv) { continue }
    Write-Host ("  pending    R{0,-6} {1}" -f $s.amount, $s.label) -ForegroundColor Cyan
    Write-Host "             $GW/checkout.html?id=$($inv.id)" -ForegroundColor DarkGray
}

Write-Host ""
Head "Done."
Dim "Merchant view : $GW"
Dim "Logs          : http://localhost:5341"
Dim "Registry      : http://localhost:8500"
Write-Host ""
Dim "Webhook deliveries for the demo merchant fail against http://localhost:9999"
Dim "by design, which is what makes the retry and backoff visible."
Write-Host ""
