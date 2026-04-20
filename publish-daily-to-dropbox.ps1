# publish-daily-to-dropbox.ps1
# End-of-day promotion: publish a debug daily zip to Dropbox top level.
#
# Intelligent flow:
#   1. Compute the expected version from HEAD
#         base     = <Version> in JJFlexRadio.vbproj
#         Y        = git rev-list --count HEAD + BUILDNUM_OFFSET
#         expected = "<base>.<Y>"
#   2. Look for a matching debug zip on NAS
#         \\nas...\jjflex\historical\<expected>\x64-debug\JJFlex_<expected>_x64_debug_*.zip
#   3. If found, copy it to Dropbox top level as the daily.
#      If not found and the tree is clean, invoke build-debug.bat, then copy.
#      If not found and the tree is dirty, refuse (Y won't reproduce from any commit).
#   4. Purge any prior daily artifacts in Dropbox top level first so exactly one
#      daily zip + NOTES lives there at any time. Also sweeps stray
#      "Setup JJFlex_*-daily.exe" files left behind by the old release-oriented
#      version of this script.
#
# Daily = debug (per memory feedback_daily_is_debug.md). Release installers go
# to the stable/ tier and GitHub Releases, not to the top-level daily slot.
#
# Triggered manually (or by Claude when you say "done developing"). Each copy
# into Dropbox JJFlexRadio/ fires a notification to shared users -- do NOT run
# this automatically per build.

param(
    [string] $DropboxRoot    = 'C:\Users\nrome\Dropbox\JJFlexRadio',
    [string] $NasRoot        = '\\nas.macaw-jazz.ts.net\jjflex',
    [string] $RepoRoot       = $PSScriptRoot,
    [int]    $BuildnumOffset = -468,
    [switch] $NoBuild,
    # Opt-in escape hatch: when the working tree is dirty, stage everything
    # and commit with the supplied message before building. Requires BOTH
    # flags -- we don't auto-generate messages (degrades git log quality and
    # conflates unrelated edits). Intended for small, single-purpose dirty
    # states like "I just edited this script; commit it and ship."
    [switch] $AutoCommit,
    [string] $CommitMessage
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "============================================"
Write-Host "Publish daily debug build to Dropbox"
Write-Host "  Repo    : $RepoRoot"
Write-Host "  NAS     : $NasRoot"
Write-Host "  Dropbox : $DropboxRoot"
Write-Host "============================================"

# --- Compute expected version from HEAD -----------------------------------
$vbproj = Join-Path $RepoRoot 'JJFlexRadio.vbproj'
if (-not (Test-Path $vbproj)) {
    Write-Error "JJFlexRadio.vbproj not found at $vbproj"
    exit 2
}

Push-Location $RepoRoot
try {
    $baseMatch = Select-String -Path $vbproj -Pattern '<Version>([0-9][0-9.]*)</Version>' | Select-Object -First 1
    if (-not $baseMatch) {
        Write-Error "Could not parse <Version> from $vbproj"
        exit 2
    }
    $baseVer = $baseMatch.Matches.Groups[1].Value

    $gitCountRaw = & git rev-list --count HEAD
    if ($LASTEXITCODE -ne 0) {
        Write-Error "git rev-list --count HEAD failed (not a git repo?)"
        exit 2
    }
    $gitCount = [int] ($gitCountRaw.Trim())
    $buildNum = $gitCount + $BuildnumOffset
    $expected = "$baseVer.$buildNum"

    $gitSha = (& git rev-parse --short HEAD).Trim()

    $dirtyLines = @(& git status --porcelain)
    $dirty      = $dirtyLines.Count
} finally {
    Pop-Location
}

function Write-HeadAnalysis {
    param($BaseVer, $GitCount, $BuildNum, $Expected, $GitSha, $DirtyCount, $DirtyLines)
    Write-Host ""
    Write-Host "HEAD analysis:"
    Write-Host "  base version : $BaseVer"
    Write-Host "  commit count : $GitCount"
    Write-Host "  build Y      : $BuildNum"
    Write-Host "  expected ver : $Expected"
    Write-Host "  git SHA      : $GitSha"
    if ($DirtyCount -gt 0) {
        Write-Warning "  working tree : DIRTY ($DirtyCount uncommitted change(s))"
        foreach ($line in $DirtyLines) {
            Write-Host "    $line"
        }
    } else {
        Write-Host "  working tree : clean"
    }
}

Write-HeadAnalysis $baseVer $gitCount $buildNum $expected $gitSha $dirty $dirtyLines

# --- Auto-commit escape hatch (opt-in) ------------------------------------
if ($dirty -gt 0 -and $AutoCommit) {
    if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
        Write-Error "-AutoCommit requires -CommitMessage '<real human message>'. We do not auto-generate commit messages."
        exit 9
    }
    Write-Host ""
    Write-Host "Auto-committing dirty tree with message:"
    Write-Host "  $CommitMessage"

    Push-Location $RepoRoot
    try {
        & git add -A
        if ($LASTEXITCODE -ne 0) {
            Write-Error "git add -A failed"
            exit 10
        }
        & git commit -m $CommitMessage
        if ($LASTEXITCODE -ne 0) {
            Write-Error "git commit failed (pre-commit hook? nothing to commit?)"
            exit 11
        }

        # Recompute expected version -- Y advanced by 1.
        $gitCountRaw = & git rev-list --count HEAD
        $gitCount    = [int] ($gitCountRaw.Trim())
        $buildNum    = $gitCount + $BuildnumOffset
        $expected    = "$baseVer.$buildNum"
        $gitSha      = (& git rev-parse --short HEAD).Trim()
        $dirtyLines  = @(& git status --porcelain)
        $dirty       = $dirtyLines.Count
    } finally {
        Pop-Location
    }

    Write-Host ""
    Write-Host "After auto-commit:"
    Write-HeadAnalysis $baseVer $gitCount $buildNum $expected $gitSha $dirty $dirtyLines
}

# --- Look for existing debug build on NAS matching HEAD -------------------
$nasDir      = Join-Path (Join-Path $NasRoot 'historical') "$expected\x64-debug"
$sourceZip   = $null
$sourceNotes = $null

if (Test-Path $nasDir) {
    $zips = Get-ChildItem -Path $nasDir -Filter "JJFlex_${expected}_x64_debug_*.zip" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending
    if ($zips) {
        $sourceZip = $zips[0]
        Write-Host ""
        Write-Host "Matching NAS build found: $($sourceZip.Name)"
        Write-Host "  built $($sourceZip.LastWriteTime)"

        $notesCandidates = Get-ChildItem -Path $nasDir -Filter "NOTES-${expected}-debug_*.txt" -File -ErrorAction SilentlyContinue |
                           Sort-Object LastWriteTime -Descending
        if ($notesCandidates) { $sourceNotes = $notesCandidates[0] }
    }
}

# --- If no match, build it (or bail) --------------------------------------
if (-not $sourceZip) {
    Write-Host ""
    Write-Host "No NAS debug build for $expected."

    if ($NoBuild) {
        Write-Error "-NoBuild set. Run build-debug.bat manually, then re-run this script."
        exit 4
    }
    if ($dirty -gt 0) {
        Write-Error "Working tree is dirty AND no matching build exists. Commit first (so Y is reproducible), then re-run. Or pass -NoBuild to just promote an older zip (won't find one for this HEAD)."
        exit 5
    }

    Write-Host "Building a fresh debug zip via build-debug.bat..."
    $buildBat = Join-Path $RepoRoot 'build-debug.bat'
    if (-not (Test-Path $buildBat)) {
        Write-Error "build-debug.bat not found at $buildBat"
        exit 6
    }
    & cmd.exe /c "`"$buildBat`""
    if ($LASTEXITCODE -ne 0) {
        Write-Error "build-debug.bat exited with $LASTEXITCODE"
        exit 6
    }

    # Re-scan after build
    if (-not (Test-Path $nasDir)) {
        Write-Error "After build, NAS dir still missing: $nasDir (NAS offline?)"
        exit 7
    }
    $zips = Get-ChildItem -Path $nasDir -Filter "JJFlex_${expected}_x64_debug_*.zip" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending
    if (-not $zips) {
        Write-Error "Build finished but no zip appeared in $nasDir"
        exit 8
    }
    $sourceZip = $zips[0]
    $notesCandidates = Get-ChildItem -Path $nasDir -Filter "NOTES-${expected}-debug_*.txt" -File -ErrorAction SilentlyContinue |
                       Sort-Object LastWriteTime -Descending
    if ($notesCandidates) { $sourceNotes = $notesCandidates[0] }
}

# --- Promote to Dropbox top level ----------------------------------------
if (-not (Test-Path $DropboxRoot)) {
    Write-Error "Dropbox folder not found: $DropboxRoot"
    exit 3
}

Write-Host ""
Write-Host "Cleaning prior daily artifacts in $DropboxRoot..."

$staleZips = Get-ChildItem -Path $DropboxRoot -Filter 'JJFlex_*_x64_daily.zip' -File -ErrorAction SilentlyContinue
foreach ($z in $staleZips) {
    Remove-Item -LiteralPath $z.FullName -Force
    Write-Host "  removed: $($z.Name)"
}
$staleNotes = Get-ChildItem -Path $DropboxRoot -Filter 'NOTES-*daily*.txt' -File -ErrorAction SilentlyContinue
foreach ($n in $staleNotes) {
    Remove-Item -LiteralPath $n.FullName -Force
    Write-Host "  removed: $($n.Name)"
}
# Sweep stray release installers left behind by the old version of this script.
$staleExes = Get-ChildItem -Path $DropboxRoot -Filter 'Setup JJFlex_*-daily.exe' -File -ErrorAction SilentlyContinue
foreach ($e in $staleExes) {
    Remove-Item -LiteralPath $e.FullName -Force
    Write-Host "  removed stale release installer: $($e.Name)"
}

$destZipName = "JJFlex_${expected}_x64_daily.zip"
$destZipPath = Join-Path $DropboxRoot $destZipName
Copy-Item -LiteralPath $sourceZip.FullName -Destination $destZipPath -Force
Write-Host ""
Write-Host "Copied zip:   $destZipName"

if ($sourceNotes) {
    $destNotesPath = Join-Path $DropboxRoot 'NOTES-daily.txt'
    Copy-Item -LiteralPath $sourceNotes.FullName -Destination $destNotesPath -Force
    Write-Host "Copied NOTES: NOTES-daily.txt  (from $($sourceNotes.Name))"
} else {
    Write-Warning "No matching NOTES file found on NAS -- Dropbox daily has no notes."
}

Write-Host ""
Write-Host "============================================"
Write-Host "Daily published: $expected"
Write-Host "============================================"

# --- Seal: also snapshot Claude's memory folder ---------------------------
# Rides this end-of-day trigger so every "done developing" ceremony also
# captures a matching memory snapshot to NAS historical\memory\. Memory
# backup is non-fatal -- if it fails, the daily publish still counts.
$memoryScript = Join-Path $RepoRoot 'backup-memory-to-nas.ps1'
if (Test-Path $memoryScript) {
    try {
        & $memoryScript -NasRoot $NasRoot
    } catch {
        Write-Warning "Memory snapshot step failed: $_"
        Write-Warning "(Daily publish itself succeeded; re-run backup-memory-to-nas.ps1 manually if needed.)"
    }
} else {
    Write-Warning "backup-memory-to-nas.ps1 not found at repo root; skipping memory snapshot."
}

# --- Seal: also snapshot Noel's private JJFlex docs -----------------------
# Same non-fatal pattern as memory backup. Captures the human-curated
# private docs (TODO-detailed, easter-egg-manifest) to NAS
# historical\private\. Private docs change on a human cadence but losing
# them would lose plaintext unlock words, strategic plans, and other
# material we deliberately don't put in the public repo.
$privateScript = Join-Path $RepoRoot 'backup-private-to-nas.ps1'
if (Test-Path $privateScript) {
    try {
        & $privateScript -NasRoot $NasRoot
    } catch {
        Write-Warning "Private docs snapshot step failed: $_"
        Write-Warning "(Daily publish itself succeeded; re-run backup-private-to-nas.ps1 manually if needed.)"
    }
} else {
    Write-Warning "backup-private-to-nas.ps1 not found at repo root; skipping private docs snapshot."
}
