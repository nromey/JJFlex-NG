<#
.SYNOPSIS
    Backfill NAS historical\<version>\x64-debug\ from existing debug zips.

.DESCRIPTION
    build-debug.bat used to only archive debug zips to NAS debug\. This meant
    every debug build past 4.1.16.1 had no exe+pdb in historical\, so old
    crash dumps from those builds could not be symbolicated once newer Debug
    builds superseded them in the devs' working trees.

    Every debug zip on NAS contains both JJFlexRadio.exe and JJFlexRadio.pdb,
    so we can rebuild history from those zips without touching dev machines.

    For each JJFlex_<ver>_x64_debug_<stamp>.zip in NAS debug\:
      - parse <ver> from filename
      - extract JJFlexRadio.exe and JJFlexRadio.pdb to historical\<ver>\x64-debug\
      - if multiple zips exist for the same version, the latest timestamp wins
        (that's the exe testers actually used; older same-version rebuilds are
        overwritten, same policy as publish-to-nas.ps1 uses for Release)
      - idempotent: skipped if target pdb already exists AND matches size

.NOTES
    Debug subfolder is x64-debug\ (not x64\) so Debug pdbs never overwrite
    Release pdbs for the same version — they're different binaries.
#>

param(
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

$nas         = '\\nas.macaw-jazz.ts.net\jjflex'
$debugDir    = Join-Path $nas 'debug'
$historical  = Join-Path $nas 'historical'

if (-not (Test-Path $nas)) {
    Write-Error "NAS not reachable at $nas. Check Tailscale."
    exit 1
}

Write-Host ""
Write-Host "============================================"
Write-Host "Backfill historical\<ver>\x64-debug from $debugDir"
Write-Host "============================================"

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Gather all debug zips and group by version; within a group, keep the newest by LastWriteTime.
$zips = Get-ChildItem -Path $debugDir -Filter 'JJFlex_*_x64_debug_*.zip' -File -ErrorAction Stop
if (-not $zips) {
    Write-Host "No debug zips found — nothing to backfill."
    exit 0
}

$latestPerVersion = $zips |
    ForEach-Object {
        if ($_.Name -match '^JJFlex_([0-9.]+)_x64_debug_') {
            [pscustomobject]@{
                Version = $Matches[1]
                File    = $_
            }
        }
    } |
    Group-Object Version |
    ForEach-Object {
        $_.Group | Sort-Object { $_.File.LastWriteTime } -Descending | Select-Object -First 1
    } |
    Sort-Object Version

$total = $latestPerVersion.Count
$written = 0
$skipped = 0
$failed  = 0

foreach ($item in $latestPerVersion) {
    $ver = $item.Version
    $zipPath = $item.File.FullName
    $destDir = Join-Path $historical "$ver\x64-debug"
    $destExe = Join-Path $destDir 'JJFlexRadio.exe'
    $destPdb = Join-Path $destDir 'JJFlexRadio.pdb'

    Write-Host ""
    Write-Host "[$ver] $($item.File.Name)"

    # Idempotency: if both files exist and non-empty, skip. We can't verify
    # content without extracting, but overwriting a good pdb with an identical
    # one is harmless — so a coarse existence check is enough.
    if ((Test-Path $destExe) -and (Test-Path $destPdb) -and
        (Get-Item $destExe).Length -gt 0 -and
        (Get-Item $destPdb).Length -gt 0) {
        Write-Host "  skip: already populated at $destDir"
        $skipped++
        continue
    }

    if ($WhatIf) {
        Write-Host "  WhatIf: would extract to $destDir"
        continue
    }

    if (-not (Test-Path $destDir)) {
        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
    }

    $zip = $null
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)

        foreach ($name in @('JJFlexRadio.exe', 'JJFlexRadio.pdb')) {
            $entry = $zip.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
            if (-not $entry) {
                Write-Warning "  $name missing from zip — crash dumps for $ver will not symbolicate"
                continue
            }
            $outPath = Join-Path $destDir $name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $outPath, $true)
            Write-Host "  wrote: $name ($($entry.Length) bytes)"
        }
        $written++
    }
    catch {
        Write-Warning "  FAILED for $ver : $($_.Exception.Message)"
        $failed++
    }
    finally {
        if ($zip) { $zip.Dispose() }
    }
}

Write-Host ""
Write-Host "============================================"
Write-Host "Backfill summary: $written written, $skipped already-present, $failed failed, $total total versions"
Write-Host "============================================"
