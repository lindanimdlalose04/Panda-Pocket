# Panda Pocket: publish every service on the host, ready for the fast image build.
#
# Step one of two on a memory-constrained machine:
#
#   .\infra\publish-local.ps1
#   .\infra\build-local.ps1
#
# Compiling inside the container needs the .NET SDK image and roughly 2 GB per
# service, which this machine does not have spare. Publishing here and copying
# the result into a runtime-only image turns each container build into a file
# copy. See infra/docker/Dockerfile.prebuilt for the full reasoning.
#
# The multi-stage Dockerfiles are untouched, so a clean clone still builds the
# ordinary way with a plain "docker compose build".

# Continue rather than Stop: dotnet writes ordinary output to stderr, which
# Windows PowerShell would otherwise treat as a terminating error.
$ErrorActionPreference = 'Continue'

$root = Split-Path $PSScriptRoot -Parent

$projects = [ordered]@{
    soc        = 'src\Services\Soc.Api\PandaPocket.Services.Soc.csproj'
    gateway    = 'src\Gateway\PandaPocket.Gateway.csproj'
    merchant   = 'src\Services\Merchant.Api\PandaPocket.Services.Merchant.csproj'
    invoice    = 'src\Services\Invoice.Api\PandaPocket.Services.Invoice.csproj'
    rate       = 'src\Services\Rate.Api\PandaPocket.Services.Rate.csproj'
    settlement = 'src\Services\Settlement.Api\PandaPocket.Services.Settlement.csproj'
}

Push-Location $root
try {
    Write-Host ""
    Write-Host "Publishing six services to .artifacts" -ForegroundColor Cyan
    Write-Host ""

    $failed = @()

    foreach ($name in $projects.Keys) {
        $out = Join-Path '.artifacts' $name

        # Cleared first, so a renamed or deleted assembly cannot linger in the
        # output and end up in the image alongside its replacement.
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }

        # -m:1 forces MSBuild to build one project at a time. In parallel it
        # spawns a compiler process per project, and on a memory-constrained
        # machine those fail with "Failed to create CoreCLR" and "GC heap
        # initialization failed", which read like a broken SDK rather than a
        # machine with no RAM left.
        dotnet publish $projects[$name] -c Release -o $out --nologo -v q -m:1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            $dll = (Get-ChildItem (Join-Path $out 'PandaPocket*.dll') | Select-Object -First 1).Name
            Write-Host ("  ok    {0,-12} {1}" -f $name, $dll) -ForegroundColor Green
        }
        else {
            Write-Host ("  FAIL  {0,-12} exit {1}" -f $name, $LASTEXITCODE) -ForegroundColor Red
            $failed += $name
        }
    }

    Write-Host ""
    if ($failed.Count -gt 0) {
        Write-Host "  $($failed.Count) project(s) failed: $($failed -join ', ')" -ForegroundColor Red
        Write-Host "  Run 'dotnet build' to see the errors in full." -ForegroundColor Red
        exit 1
    }

    Write-Host "  Done. Next:  .\infra\build-local.ps1" -ForegroundColor Green
    Write-Host ""
}
finally {
    Pop-Location
}
