# backup-private-to-nas.ps1
# Snapshot Noel's private JJFlex docs folder to the NAS historical tree.
#
# Contents: the human-curated private docs that live outside the repo
# (detailed TODO, easter-egg manifest with unlock plaintexts, any future
# private planning). These files contain information we deliberately keep
# out of the public repo -- unlock words, internal plans, personal notes.
#
# Sibling folder to `historical\memory\`:
#   \\nas...\jjflex\historical\private\private-YYYYMMDD-HHMMSS.zip
#
# Private docs change on a human cadence (when Noel edits them), NOT per
# build. So like memory backups, they get their own sibling tree under
# historical\ rather than being nested inside per-version folders.
#
# Runs manually any time, or auto-invoked at the tail of
# publish-daily-to-dropbox.ps1 alongside the memory backup. Each
# "done developing" end-of-day ceremony seals a matching private-docs
# snapshot.

param(
    [string] $PrivateRoot = 'C:\Users\nrome\JJFlex-private',
    [string] $NasRoot     = '\\nas.macaw-jazz.ts.net\jjflex'
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "============================================"
Write-Host "Backup JJFlex private docs to NAS"
Write-Host "  Private : $PrivateRoot"
Write-Host "  NAS     : $NasRoot"
Write-Host "============================================"

if (-not (Test-Path $PrivateRoot)) {
    Write-Error "Private docs folder not found: $PrivateRoot"
    exit 2
}

$destDir = Join-Path (Join-Path $NasRoot 'historical') 'private'
if (-not (Test-Path $destDir)) {
    try {
        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
        Write-Host "Created NAS folder: $destDir"
    } catch {
        Write-Error "Could not create NAS folder $destDir. NAS offline? $_"
        exit 3
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zipName   = "private-$timestamp.zip"
$zipPath   = Join-Path $destDir $zipName

# Build the zip in local temp, then copy to NAS -- avoids partial files
# on the network and sidesteps Compress-Archive UNC-path quirks.
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) $zipName

try {
    if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
    Compress-Archive -Path (Join-Path $PrivateRoot '*') -DestinationPath $tempZip -Force
    Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
    Remove-Item -LiteralPath $tempZip -Force

    $size      = (Get-Item $zipPath).Length
    $fileCount = (Get-ChildItem -Path $PrivateRoot -File -Recurse).Count

    Write-Host ""
    Write-Host "Snapshot saved: $zipName"
    Write-Host "  files : $fileCount"
    Write-Host "  size  : $size bytes"
    Write-Host "  path  : $zipPath"
} catch {
    Write-Error "Backup failed: $_"
    exit 4
}

Write-Host ""
Write-Host "============================================"
Write-Host "Private docs snapshot complete"
Write-Host "============================================"
