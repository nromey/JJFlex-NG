# backup-memory-to-nas.ps1
# Snapshot Claude's memory folder to the NAS historical tree.
#
# Memory is Claude's evolving picture of the project across sessions
# (product vision, user preferences, decisions, tester relationships). It
# lives outside the repo and outside normal backup paths. This script
# zips the full memory folder to:
#   \\nas...\jjflex\historical\memory\memory-YYYYMMDD-HHMMSS.zip
#
# Memory gets its own sibling folder under historical\ (not under any
# per-version \historical\<ver>\ folder) because memory changes on a
# different cadence than builds -- it is its own history.
#
# Runs manually any time, or auto-invoked at the tail of
# publish-daily-to-dropbox.ps1 so the "done developing" ceremony seals a
# matching memory snapshot every end-of-day.

param(
    [string] $MemoryRoot = 'C:\Users\nrome\.claude\projects\c--dev-JJFlex-NG\memory',
    [string] $NasRoot    = '\\nas.macaw-jazz.ts.net\jjflex'
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "============================================"
Write-Host "Backup Claude memory to NAS"
Write-Host "  Memory : $MemoryRoot"
Write-Host "  NAS    : $NasRoot"
Write-Host "============================================"

if (-not (Test-Path $MemoryRoot)) {
    Write-Error "Memory folder not found: $MemoryRoot"
    exit 2
}

$destDir = Join-Path (Join-Path $NasRoot 'historical') 'memory'
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
$zipName   = "memory-$timestamp.zip"
$zipPath   = Join-Path $destDir $zipName

# Build the zip in the local temp dir, then copy to NAS. Two reasons:
#   1. Compress-Archive writing straight to a UNC path has had bugs historically.
#   2. A network hiccup mid-compress would leave a partial zip on the NAS.
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) $zipName

try {
    if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
    Compress-Archive -Path (Join-Path $MemoryRoot '*') -DestinationPath $tempZip -Force
    Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
    Remove-Item -LiteralPath $tempZip -Force

    $size      = (Get-Item $zipPath).Length
    $fileCount = (Get-ChildItem -Path $MemoryRoot -File -Recurse).Count

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
Write-Host "Memory snapshot complete"
Write-Host "============================================"
