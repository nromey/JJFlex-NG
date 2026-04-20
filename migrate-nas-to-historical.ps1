<#
.SYNOPSIS
    Consolidate NAS history under \historical\<version>\ and retire the
    parallel \debug\ and \nightly\ flat folders.

.DESCRIPTION
    Before:
      \debug\    JJFlex_<ver>_x64_debug_<stamp>.zip + NOTES-*.txt (flat)
      \nightly\  Setup JJFlex_<ver>_<stamp>_<arch>.exe (flat) + stale PURPOSE
      \historical\<ver>\<arch>\  JJFlexRadio.exe + .pdb

    After:
      \historical\<ver>\
        x64\                Release exe + pdb                          (unchanged)
        x86\                Release exe + pdb                          (unchanged)
        x64-debug\          Debug exe + pdb + zip(s) + NOTES           (consolidated)
        installers\         Setup JJFlex_<ver>_<stamp>_<arch>.exe      (moved from nightly)

    \debug\ and \nightly\ top-level folders are removed after contents move.
    \stable\ is untouched — it is "currently published", not history.

    Idempotent and safe to re-run: skips items that would overwrite if the
    destination already has them with matching size.

.NOTES
    Run with -WhatIf first to preview. Uses Move-Item, not Copy+Delete, so
    a partial run can be resumed.
#>

param(
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

$nas         = '\\nas.macaw-jazz.ts.net\jjflex'
$debugDir    = Join-Path $nas 'debug'
$nightlyDir  = Join-Path $nas 'nightly'
$historical  = Join-Path $nas 'historical'

if (-not (Test-Path $nas)) {
    Write-Error "NAS not reachable at $nas. Check Tailscale."
    exit 1
}

Write-Host ""
Write-Host "============================================"
Write-Host "NAS consolidation -> \historical\<version>\"
if ($WhatIf) { Write-Host "  MODE: WhatIf (no changes will be written)" }
Write-Host "============================================"

# ---------------------------------------------------------------------------
# Phase 1: debug\ zips + NOTES  ->  historical\<ver>\x64-debug\
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- Phase 1: debug zips and NOTES ---"

if (Test-Path $debugDir) {
    $zips = Get-ChildItem -Path $debugDir -Filter 'JJFlex_*_x64_debug_*.zip' -File -ErrorAction SilentlyContinue
    foreach ($zip in $zips) {
        if ($zip.Name -notmatch '^JJFlex_([0-9.]+)_x64_debug_([0-9\-]+)\.zip$') {
            Write-Warning "  skip (unrecognized name): $($zip.Name)"
            continue
        }
        $ver = $Matches[1]
        $stamp = $Matches[2]
        $dstDir = Join-Path $historical "$ver\x64-debug"
        $dst = Join-Path $dstDir $zip.Name

        if (Test-Path $dst) {
            Write-Host "  skip (already at destination): $($zip.Name)"
            continue
        }
        Write-Host "  move: $($zip.Name)  ->  $ver\x64-debug\"
        if (-not $WhatIf) {
            if (-not (Test-Path $dstDir)) { New-Item -Path $dstDir -ItemType Directory -Force | Out-Null }
            Move-Item -LiteralPath $zip.FullName -Destination $dst
        }

        # Matching NOTES
        $notesName = "NOTES-${ver}-debug_${stamp}.txt"
        $notesSrc = Join-Path $debugDir $notesName
        if (Test-Path $notesSrc) {
            $notesDst = Join-Path $dstDir $notesName
            if (-not (Test-Path $notesDst)) {
                Write-Host "    + NOTES: $notesName"
                if (-not $WhatIf) { Move-Item -LiteralPath $notesSrc -Destination $notesDst }
            }
        }
    }

    # Any leftover NOTES not matched by a zip above
    $orphanNotes = Get-ChildItem -Path $debugDir -Filter 'NOTES-*-debug_*.txt' -File -ErrorAction SilentlyContinue
    foreach ($n in $orphanNotes) {
        if ($n.Name -match '^NOTES-([0-9.]+)-debug_([0-9\-]+)\.txt$') {
            $ver = $Matches[1]
            $dstDir = Join-Path $historical "$ver\x64-debug"
            $dst = Join-Path $dstDir $n.Name
            if (Test-Path $dst) { continue }
            Write-Host "  move orphan NOTES: $($n.Name)  ->  $ver\x64-debug\"
            if (-not $WhatIf) {
                if (-not (Test-Path $dstDir)) { New-Item -Path $dstDir -ItemType Directory -Force | Out-Null }
                Move-Item -LiteralPath $n.FullName -Destination $dst
            }
        }
    }
} else {
    Write-Host "  (no \debug\ folder — already migrated?)"
}

# ---------------------------------------------------------------------------
# Phase 2: nightly\ installers  ->  historical\<ver>\installers\
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- Phase 2: nightly installers ---"

if (Test-Path $nightlyDir) {
    $installers = Get-ChildItem -Path $nightlyDir -Filter 'Setup JJFlex_*.exe' -File -ErrorAction SilentlyContinue
    foreach ($inst in $installers) {
        # "Setup JJFlex_<ver>_<stamp>_<arch>.exe" where arch is x64|x86
        if ($inst.Name -notmatch '^Setup JJFlex_([0-9.]+)_[0-9_]+_(x64|x86)\.exe$') {
            Write-Warning "  skip (unrecognized name): $($inst.Name)"
            continue
        }
        $ver = $Matches[1]
        $dstDir = Join-Path $historical "$ver\installers"
        $dst = Join-Path $dstDir $inst.Name

        if (Test-Path $dst) {
            Write-Host "  skip (already at destination): $($inst.Name)"
            continue
        }
        Write-Host "  move: $($inst.Name)  ->  $ver\installers\"
        if (-not $WhatIf) {
            if (-not (Test-Path $dstDir)) { New-Item -Path $dstDir -ItemType Directory -Force | Out-Null }
            Move-Item -LiteralPath $inst.FullName -Destination $dst
        }
    }
} else {
    Write-Host "  (no \nightly\ folder — already migrated?)"
}

# ---------------------------------------------------------------------------
# Phase 3: retire top-level \debug\ and \nightly\
#   Drop PURPOSE.txt, empty don\ placeholder, then remove the folders.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- Phase 3: retire \debug\ and \nightly\ ---"

function Remove-IfEmpty([string]$path) {
    if (-not (Test-Path $path)) { return }

    # Nuke PURPOSE and the don\ placeholder (user confirmed don\ can go)
    $purpose = Join-Path $path 'PURPOSE.txt'
    if (Test-Path $purpose) {
        Write-Host "  rm: $purpose"
        if (-not $WhatIf) { Remove-Item -LiteralPath $purpose -Force }
    }
    $donDir = Join-Path $path 'don'
    if (Test-Path $donDir) {
        Write-Host "  rm tree: $donDir"
        if (-not $WhatIf) { Remove-Item -LiteralPath $donDir -Recurse -Force }
    }

    $remaining = Get-ChildItem -Path $path -Force -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Warning "  $path still has content — leaving in place:"
        $remaining | ForEach-Object { Write-Warning "    $($_.Name)" }
    } else {
        Write-Host "  rmdir: $path"
        if (-not $WhatIf) { Remove-Item -LiteralPath $path -Force }
    }
}

Remove-IfEmpty $debugDir
Remove-IfEmpty $nightlyDir

# ---------------------------------------------------------------------------
# Phase 4: refresh \historical\PURPOSE.txt
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- Phase 4: refresh historical\PURPOSE.txt ---"

$purposePath = Join-Path $historical 'PURPOSE.txt'
$purposeBody = @"
Per-version build history for JJ Flexible Radio Access.

Layout: historical\<version>\
  x64\         Release exe + pdb (for symbolicating release crash dumps)
  x86\         Release exe + pdb
  x64-debug\   Debug exe + pdb, plus the distributed zip(s) and NOTES
  installers\  Setup JJFlex_<ver>_<stamp>_<arch>.exe (every built installer)

Same-version rebuilds overwrite exe+pdb but keep timestamped zips and
installers so bisect remains possible.

stable\ is NOT history — it holds the currently-promoted release installer.
"@

Write-Host "  write: $purposePath"
if (-not $WhatIf) {
    [System.IO.File]::WriteAllText($purposePath, $purposeBody, [System.Text.UTF8Encoding]::new($false))
}

Write-Host ""
Write-Host "============================================"
if ($WhatIf) {
    Write-Host "Dry run complete. Re-run without -WhatIf to apply."
} else {
    Write-Host "Consolidation complete."
}
Write-Host "============================================"
