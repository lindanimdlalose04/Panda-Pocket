# Panda Pocket: build the code zip for submission.
#
# The brief asks for the source code and the deployment files, and only the code
# that was written rather than anything the platform generated. So this takes
# src, the gateway configuration, the Compose file and the infra scripts, and
# leaves out bin, obj, .vs, .git, node_modules and the docs folder.
#
# The document and the video are submitted separately; they are not in here.
#
# Usage:  .\infra\make-submission.ps1

$ErrorActionPreference = 'Stop'

$root  = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $env:TEMP "pandapocket-submission"
$zip   = Join-Path (Split-Path $root -Parent) "Panda-Pocket-Deliverable-1-code.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Directories copied whole, minus build output.
$excludeDirs = @('bin', 'obj', 'node_modules', '.vs', '.git')

function Copy-Tree($relative) {
    $from = Join-Path $root $relative
    if (-not (Test-Path $from)) { return }

    Get-ChildItem $from -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\')
        $parts = $rel -split '\\'
        if ($parts | Where-Object { $excludeDirs -contains $_ }) { return }

        $target = Join-Path $stage $rel
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item $_.FullName $target
    }
}

Write-Host ""
Write-Host "Staging the code" -ForegroundColor Cyan

Copy-Tree 'src'        # four services, the gateway and its ocelot.json, the shared library
Copy-Tree 'infra'      # database init, reset, seed and verification scripts
Copy-Tree 'requests'   # .http files exercising every endpoint

foreach ($f in @('docker-compose.yml', 'global.json', 'PandaPocket.sln',
                 '.dockerignore', '.editorconfig', 'README.md',
                 'START-HERE.bat', 'STOP.bat')) {
    $p = Join-Path $root $f
    if (Test-Path $p) { Copy-Item $p (Join-Path $stage $f) }
}

$files = (Get-ChildItem $stage -Recurse -File)
$kb    = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1KB)
Write-Host "  $($files.Count) files, $kb KB" -ForegroundColor DarkGray

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Written:" -ForegroundColor Green
Write-Host "  $zip"
Write-Host "  $([math]::Round((Get-Item $zip).Length / 1KB)) KB" -ForegroundColor DarkGray
Write-Host ""
