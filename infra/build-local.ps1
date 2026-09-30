# Panda Pocket: build the six service images from the published output.
#
# Step two of two. Run .\infra\publish-local.ps1 first.
#
# Serial on purpose. Building these in parallel took the build daemon down twice
# on this machine, with "rpc error: code = Unavailable" and then the engine
# itself returning 500s. One at a time is slower on paper and far faster in
# practice, because nothing has to be retried.

# Continue, not Stop. Windows PowerShell wraps a native command's stderr in an
# ErrorRecord, so "docker compose build" writing its ordinary progress to stderr
# reads as a terminating error even when it exits 0. Exit codes are checked
# explicitly below instead.
$ErrorActionPreference = 'Continue'

$root   = Split-Path $PSScriptRoot -Parent
$docker = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"

$services = @(
    'soc-service',
    'gateway',
    'merchant-service',
    'rate-service',
    'settlement-service',
    'invoice-service'
)

Push-Location $root
try {
    foreach ($name in @('soc','gateway','merchant','invoice','rate','settlement')) {
        if (-not (Test-Path (Join-Path '.artifacts' $name))) {
            Write-Host ""
            Write-Host "  .artifacts\$name is missing. Run .\infra\publish-local.ps1 first." -ForegroundColor Red
            exit 1
        }
    }

    Write-Host ""
    Write-Host "Building six images, one at a time" -ForegroundColor Cyan
    Write-Host ""

    $failed = @()

    foreach ($service in $services) {
        $sw = [Diagnostics.Stopwatch]::StartNew()

        # No 2>&1 here, for the same reason: redirecting native stderr inside
        # PowerShell turns progress output into errors.
        & $docker compose -f docker-compose.yml -f docker-compose.build.yml build $service | Out-Null
        $code = $LASTEXITCODE

        $sw.Stop()

        if ($code -eq 0) {
            Write-Host ("  ok    {0,-20} {1,5:N0}s" -f $service, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
        }
        else {
            Write-Host ("  FAIL  {0,-20} exit {1}" -f $service, $code) -ForegroundColor Red
            $failed += $service
        }
    }

    Write-Host ""
    if ($failed.Count -gt 0) {
        Write-Host "  $($failed.Count) image(s) failed: $($failed -join ', ')" -ForegroundColor Red
        Write-Host "  If the error mentions 'rpc error' or the engine stops answering," -ForegroundColor Yellow
        Write-Host "  the build daemon ran out of memory. Close a browser and retry." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "  Done. Next:  docker compose up -d" -ForegroundColor Green
    Write-Host ""
}
finally {
    Pop-Location
}
