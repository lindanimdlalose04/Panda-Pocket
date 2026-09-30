# Panda Pocket: start everything and open it in a browser.
#
# Intended to be run by double-clicking START-HERE.bat in the project root.
# Does the whole cold start: checks Docker, recovers it if it is stuck, brings
# the nine containers up, waits until they are actually answering, and opens the
# dashboard.

$ErrorActionPreference = 'Continue'
# $PSScriptRoot is empty if this is ever run other than as a file, so fall back
# to the current directory rather than failing with an unhelpful binding error.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$root = if (Test-Path (Join-Path $here "..\docker-compose.yml")) { Split-Path -Parent $here } else { $here }
$composeFile = Join-Path $root "docker-compose.yml"

if (-not (Test-Path $composeFile)) {
    Write-Host ""
    Write-Host "  Could not find docker-compose.yml." -ForegroundColor Red
    Write-Host "  Expected it at: $composeFile" -ForegroundColor Red
    Write-Host "  Run START-HERE.bat from inside the Panda Pocket folder." -ForegroundColor Red
    Write-Host ""
    Read-Host "  Press Enter to close"
    exit 1
}

Set-Location $root

# Every compose call names the file and project directory explicitly, so the
# script works no matter what directory it is invoked from.
$compose = @("compose", "-f", $composeFile, "--project-directory", $root)

$docker = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
if (-not (Test-Path $docker)) { $docker = "docker" }

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "  Panda Pocket" Cyan
Say "  ============" Cyan
Say ""

# ---------------------------------------------------------------------------
# 1. Is Docker running?
# ---------------------------------------------------------------------------
Say "  [1/4] Checking Docker..." White

& $docker info --format '{{.ServerVersion}}' 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {

    Say "        Docker is not responding. Starting Docker Desktop..." Yellow

    $desktop = "C:\Program Files\Docker\Docker\Docker Desktop.exe"
    if (Test-Path $desktop) { Start-Process -FilePath $desktop }

    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 5
        & $docker info --format '{{.ServerVersion}}' 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $up = $true; break }

        # Docker Desktop on this machine sometimes fails to start because of
        # orphaned socket files left by an unclean shutdown. Detect it and fix it
        # rather than leaving the user staring at a spinner.
        $errFile = "$env:LOCALAPPDATA\Docker\backend.error.json"
        if (Test-Path $errFile) {
            Say "        Docker failed to start. Clearing stale sockets..." Yellow
            & "$PSScriptRoot\fix-docker-sockets.ps1"
            if ($LASTEXITCODE -eq 0) { $up = $true }
            break
        }
    }

    if (-not $up) {
        Say ""
        Say "  Docker would not start." Red
        Say "  Open Docker Desktop yourself, wait for the whale to stop moving," Red
        Say "  then run this again." Red
        Say ""
        Read-Host "  Press Enter to close"
        exit 1
    }
}

Say "        Docker is running." Green

# ---------------------------------------------------------------------------
# 2. Start the containers
# ---------------------------------------------------------------------------
Say "  [2/4] Starting the system (first run downloads images, be patient)..." White

& $docker @compose up -d 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) {
    Say "        Something went wrong starting the containers." Red
    Say "        Run this to see why:  docker compose logs" Red
    Read-Host "  Press Enter to close"
    exit 1
}

# ---------------------------------------------------------------------------
# 3. Wait until the gateway actually answers
# ---------------------------------------------------------------------------
Say "  [3/4] Waiting for the system to be ready..." White

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:5000/health" -TimeoutSec 3 -UseBasicParsing
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
}

if (-not $ready) {
    Say "        Still not answering after three minutes." Red
    Say "        Check with:  docker compose ps" Red
    Read-Host "  Press Enter to close"
    exit 1
}

Say "        Ready." Green

# ---------------------------------------------------------------------------
# 4. Report and open
# ---------------------------------------------------------------------------
Say "  [4/4] Containers:" White
& $docker @compose ps --format "table {{.Name}}\t{{.Status}}" | ForEach-Object { Say "        $_" }

Say ""
Say "  Open these in your browser:" Cyan
Say ""
Say "    Dashboard   http://localhost:5000    <- start here" White
Say "    Registry    http://localhost:8500    (Consul: which services are alive)" Gray
Say "    Logs        http://localhost:5341    (Seq: every service's logs)" Gray
Say "    API docs    http://localhost:5002/swagger" Gray
Say ""
Say "  To add demo data:   .\infra\seed-demo.ps1" Gray
Say "  To stop:            double-click STOP.bat" Gray
Say ""

Start-Process "http://localhost:5000"

Say "  Dashboard opened in your browser." Green
Say ""
Read-Host "  Press Enter to close this window"
