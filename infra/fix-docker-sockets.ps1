# Panda Pocket: recover Docker Desktop from orphaned socket files.
#
# Symptom: Docker Desktop refuses to start, reporting something like
#
#   starting services: initializing Secrets Engine:
#   listening on unix://.../docker-secrets-engine/engine.sock:
#   remove .../engine.sock: The file cannot be accessed by the system.
#
# Cause: Docker keeps AF_UNIX sockets as reparse points on disk. When Docker
# Desktop does not shut down cleanly, for example the machine is powered off
# while it is running, those reparse points survive with their backing kernel
# objects gone. Windows then refuses to open or delete them, and the Docker
# backend aborts at startup because it clears each socket before binding it.
#
# The socket named in the error varies between restarts, because the backend
# fails at whichever orphaned socket it reaches first. dockerInference,
# sailor-ingest.sock, userAnalyticsOtlpHttp.sock and
# docker-secrets-engine/engine.sock have all appeared on this machine. Fixing
# them one at a time turns into whack-a-mole, so this script finds every
# orphaned socket across all of Docker's runtime directories and clears them in
# one pass.
#
# Fix: the files themselves cannot be deleted, even through extended \?\
# paths, but the directory containing them can be renamed, because renaming a
# directory does not touch its children. Docker recreates the directories on
# the next start.
#
# Usage:  ./infra/fix-docker-sockets.ps1
#
# Worth running before the demo if Docker has not been started in a while.

$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

Write-Host "Stopping Docker Desktop if it is running..." -ForegroundColor Cyan
Get-Process -Name "Docker Desktop", "com.docker.backend" -ErrorAction SilentlyContinue |
  Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# Discover every directory holding an orphaned socket reparse point, skipping
# directories this script has already set aside on a previous run.
Write-Host "Scanning for orphaned socket reparse points..." -ForegroundColor Cyan

$dockerRoots = Get-ChildItem "$env:LOCALAPPDATA" -Directory -Force -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match '^docker' }

$affected = @()
foreach ($root in $dockerRoots) {
  Get-ChildItem $root.FullName -Recurse -Force -Depth 2 -ErrorAction SilentlyContinue |
    Where-Object { $_.Attributes -match 'ReparsePoint' -and $_.FullName -notmatch '\.stale-' } |
    ForEach-Object { $affected += $_.DirectoryName }
}

$affected = $affected | Sort-Object -Unique

if ($affected.Count -eq 0) {
  Write-Host "  No orphaned sockets found." -ForegroundColor Green
} else {
  foreach ($dir in $affected) {
    # Never rename a top-level Docker folder; those hold settings and image
    # data. Only leaf runtime directories are safe to set aside.
    if ($dir -eq "$env:LOCALAPPDATA\Docker") {
      Write-Host "  Skipping $dir (top-level, not a runtime directory)" -ForegroundColor Yellow
      continue
    }

    $leaf   = Split-Path $dir -Leaf
    $parent = Split-Path $dir -Parent
    $new    = "$leaf.stale-$stamp"

    try {
      Rename-Item -LiteralPath $dir -NewName $new -ErrorAction Stop
      Write-Host "  Renamed $leaf -> $new" -ForegroundColor Green
    } catch {
      Write-Host "  Could not rename ${dir}: $($_.Exception.Message)" -ForegroundColor Red
      Write-Host "  A reboot clears these reparse points if renaming fails." -ForegroundColor Yellow
      exit 1
    }
  }
}

# Best effort tidy-up of directories set aside on earlier runs. These usually
# cannot be removed while their reparse points are still orphaned; after a
# reboot they can. Failure here is not a problem, only clutter.
foreach ($root in $dockerRoots) {
  Get-ChildItem (Split-Path $root.FullName -Parent) -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '\.stale-' } |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
  Get-ChildItem $root.FullName -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '\.stale-' } |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

Remove-Item "$env:LOCALAPPDATA\Docker\backend.error.json" -Force -ErrorAction SilentlyContinue

Write-Host "Starting Docker Desktop..." -ForegroundColor Cyan
Start-Process -FilePath "C:\Program Files\Docker\Docker\Docker Desktop.exe"

Write-Host "Waiting for the engine (up to 5 minutes)..." -ForegroundColor Cyan
$docker = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Seconds 5
  & $docker info --format '{{.ServerVersion}}' 2>&1 | Out-Null
  if ($LASTEXITCODE -eq 0) {
    Write-Host "Engine up: $(& $docker info --format '{{.ServerVersion}}')" -ForegroundColor Green
    exit 0
  }
  if (Test-Path "$env:LOCALAPPDATA\Docker\backend.error.json") {
    $err = (Get-Content "$env:LOCALAPPDATA\Docker\backend.error.json" -Raw |
            ConvertFrom-Json).error.error.error.originalError.description
    if ($err) {
      Write-Host "Backend failed again: $err" -ForegroundColor Red
      Write-Host "Re-run this script; a new orphaned socket has surfaced." -ForegroundColor Yellow
      exit 1
    }
  }
}

Write-Host "Engine did not come up in time. Check the Docker Desktop window." -ForegroundColor Red
exit 1
