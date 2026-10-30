# publish-nightly-to-dropbox.ps1
# End-of-day promotion: publish a debug nightly zip to Dropbox top level.
#
# Intelligent flow:
#   1. Compute the expected version from HEAD
#         base     = <Version> in JJFlexRadio.vbproj
#         Y        = git rev-list --count HEAD + BUILDNUM_OFFSET
#         expected = "<base>.<Y>"
#   2. Look for a matching debug zip on NAS
#         \\nas...\jjflex\historical\<expected>\x64-debug\JJFlex_<expected>_x64_debug_*.zip
#   3. If found, copy it to Dropbox top level as the nightly.
#      If not found and the tree is clean, invoke build-debug.bat, then copy.
#      If not found and the tree is dirty, refuse (Y won't reproduce from any commit).
#   4. Stage and verify the zip + NOTES locally, copy and verify them at their
#      final Dropbox names, then purge older nightly artifacts
#      so exactly one nightly zip + NOTES lives there. Also sweeps stray
#      "Setup JJFlex_*-nightly.exe" files left behind by the old release-oriented
#      version of this script.
#
# Nightly = debug (per memory feedback_nightly_is_debug.md). Release installers go
# to the stable/ tier and GitHub Releases, not to the top-level nightly slot.
#
# Triggered manually (or by Claude when you say "done developing"). Each copy
# into Dropbox JJFlexRadio/ fires a notification to shared users -- do NOT run
# this automatically per build.

param(
    # Machine-dependent (D:\Dropbox on the ms-02; old default was
    # C:\Users\nrome\Dropbox) — resolved from Dropbox's own info.json,
    # hardcoded fallback retained.
    [string] $DropboxRoot    = $(try { (Get-Content (Join-Path $env:LOCALAPPDATA 'Dropbox\info.json') -Raw | ConvertFrom-Json).personal.path + '\JJFlexRadio' } catch { 'C:\Users\nrome\Dropbox\JJFlexRadio' }),
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
    [string] $CommitMessage,
    # Bypass the build and backup flow for isolated promotion tests.
    [switch] $PromoteOnly,
    [string] $SourceZipPath,
    [string] $SourceNotesPath,
    [string] $PromotionVersion
)

$ErrorActionPreference = 'Stop'

function Invoke-NightlyPromotion {
    param(
        [string] $ZipPath,
        [string] $NotesPath,
        [string] $Destination,
        [string] $Version
    )

    $stageDir = $null
    $stageZip = $null
    $stageNotes = $null
    $backupZip = $null
    $backupNotes = $null
    $zipAttempted = $false
    $notesAttempted = $false
    $restorationFailed = $false
    try {
        $zip = Get-Item -LiteralPath $ZipPath -ErrorAction Stop
        $notes = Get-Item -LiteralPath $NotesPath -ErrorAction Stop
        if ($zip.PSIsContainer -or $notes.PSIsContainer) {
            throw 'Both nightly sources must be files.'
        }
        if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
            throw "Dropbox folder not found: $Destination"
        }

        $destZip = Join-Path $Destination "JJFlex_${Version}_x64_nightly.zip"
        $destNotes = Join-Path $Destination 'NOTES-nightly.txt'
        $stageDir = (New-Item -ItemType Directory -Path (Join-Path $env:TEMP ("jjflex-nightly-" + [guid]::NewGuid().ToString('N'))) -ErrorAction Stop).FullName
        $stageZip = Join-Path $stageDir "JJFlex_${Version}_x64_nightly.zip"
        $stageNotes = Join-Path $stageDir 'NOTES-nightly.txt'
        $backupZip = Join-Path $stageDir 'prior-nightly.zip'
        $backupNotes = Join-Path $stageDir 'prior-notes.txt'
        $zipExisted = Test-Path -LiteralPath $destZip
        $notesExisted = Test-Path -LiteralPath $destNotes

        Copy-Item -LiteralPath $zip.FullName -Destination $stageZip -ErrorAction Stop
        Assert-MatchingFile $zip.FullName $stageZip 'Local zip stage'
        Copy-Item -LiteralPath $notes.FullName -Destination $stageNotes -ErrorAction Stop
        Assert-MatchingFile $notes.FullName $stageNotes 'Local NOTES stage'

        if ($zipExisted) {
            Copy-Item -LiteralPath $destZip -Destination $backupZip -ErrorAction Stop
            Assert-MatchingFile $destZip $backupZip 'Prior zip backup'
        }
        if ($notesExisted) {
            Copy-Item -LiteralPath $destNotes -Destination $backupNotes -ErrorAction Stop
            Assert-MatchingFile $destNotes $backupNotes 'Prior NOTES backup'
        }

        $zipAttempted = $true
        Copy-Item -LiteralPath $stageZip -Destination $destZip -Force -ErrorAction Stop
        Assert-MatchingFile $stageZip $destZip 'Dropbox zip'
        $notesAttempted = $true
        Copy-Item -LiteralPath $stageNotes -Destination $destNotes -Force -ErrorAction Stop
        Assert-MatchingFile $stageNotes $destNotes 'Dropbox NOTES'
        Write-Host "Copied and verified zip: $destZip"
        Write-Host "Copied and verified NOTES: $destNotes"
    } catch {
        Write-Host "ERROR: Nightly copy or verification failed: $_"
        if ($notesAttempted) {
            try {
                if ($notesExisted) {
                    if (-not (Test-MatchingFile $backupNotes $destNotes)) {
                        Copy-Item -LiteralPath $backupNotes -Destination $destNotes -Force -ErrorAction Stop
                    }
                    Assert-MatchingFile $backupNotes $destNotes 'Restored NOTES'
                } elseif (Test-Path -LiteralPath $destNotes) {
                    Remove-Item -LiteralPath $destNotes -Force -ErrorAction Stop
                }
            } catch {
                $restorationFailed = $true
                Write-Host "WARNING: Could not restore prior NOTES; backup remains at ${backupNotes}: $_"
            }
        }
        if ($zipAttempted) {
            try {
                if ($zipExisted) {
                    if (-not (Test-MatchingFile $backupZip $destZip)) {
                        Copy-Item -LiteralPath $backupZip -Destination $destZip -Force -ErrorAction Stop
                    }
                    Assert-MatchingFile $backupZip $destZip 'Restored zip'
                } elseif (Test-Path -LiteralPath $destZip) {
                    Remove-Item -LiteralPath $destZip -Force -ErrorAction Stop
                }
            } catch {
                $restorationFailed = $true
                Write-Host "WARNING: Could not restore prior zip; backup remains at ${backupZip}: $_"
            }
        }
        Write-Host 'Prior nightly artifacts were not purged.'
        return 10
    } finally {
        if ($stageDir -and -not $restorationFailed) {
            try {
                foreach ($path in @($stageZip, $stageNotes, $backupZip, $backupNotes)) {
                    if (Test-Path -LiteralPath $path) {
                        Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                    }
                }
                Remove-Item -LiteralPath $stageDir -ErrorAction Stop
            } catch {
                Write-Host "WARNING: Could not clean local nightly staging folder ${stageDir}: $_"
            }
        }
    }

    try {
        foreach ($oldZip in @(Get-ChildItem -LiteralPath $Destination -Filter 'JJFlex_*_x64_nightly.zip' -File -ErrorAction Stop)) {
            if ($oldZip.FullName -ne $destZip) {
                Remove-Item -LiteralPath $oldZip.FullName -Force -ErrorAction Stop
                Write-Host "Removed old zip: $($oldZip.Name)"
            }
        }
        foreach ($oldNotes in @(Get-ChildItem -LiteralPath $Destination -Filter 'NOTES-*nightly*.txt' -File -ErrorAction Stop)) {
            if ($oldNotes.FullName -ne $destNotes) {
                Remove-Item -LiteralPath $oldNotes.FullName -Force -ErrorAction Stop
                Write-Host "Removed old NOTES: $($oldNotes.Name)"
            }
        }
        foreach ($oldExe in @(Get-ChildItem -LiteralPath $Destination -Filter 'Setup JJFlex_*-nightly.exe' -File -ErrorAction Stop)) {
            Remove-Item -LiteralPath $oldExe.FullName -Force -ErrorAction Stop
            Write-Host "Removed stale release installer: $($oldExe.Name)"
        }
    } catch {
        Write-Host "WARNING: Nightly copied and verified, but purge failed: $_"
        return 11
    }
    return 0
}

function Test-MatchingFile {
    param([string] $Source, [string] $Copy)
    try {
        $sourceFile = Get-Item -LiteralPath $Source -ErrorAction Stop
        $copyFile = Get-Item -LiteralPath $Copy -ErrorAction Stop
        if ($sourceFile.PSIsContainer -or $copyFile.PSIsContainer -or $sourceFile.Length -ne $copyFile.Length) {
            return $false
        }
        return ((Get-FileHash -LiteralPath $Source -Algorithm SHA256 -ErrorAction Stop).Hash -eq
                (Get-FileHash -LiteralPath $Copy -Algorithm SHA256 -ErrorAction Stop).Hash)
    } catch {
        return $false
    }
}

function Assert-MatchingFile {
    param([string] $Source, [string] $Copy, [string] $Label)
    if (-not (Test-MatchingFile $Source $Copy)) {
        throw "${Label} failed size or SHA-256 verification: $Copy"
    }
}

if ($PromoteOnly) {
    if (-not $PSBoundParameters.ContainsKey('DropboxRoot') -or
        -not $SourceZipPath -or -not $SourceNotesPath -or -not $PromotionVersion) {
        Write-Host 'ERROR: -PromoteOnly requires -DropboxRoot, -SourceZipPath, -SourceNotesPath, and -PromotionVersion.'
        exit 2
    }
    exit (Invoke-NightlyPromotion -ZipPath $SourceZipPath -NotesPath $SourceNotesPath -Destination $DropboxRoot -Version $PromotionVersion)
}

Write-Host ""
Write-Host "============================================"
Write-Host "Publish nightly debug build to Dropbox"
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
Write-Host ""
if (-not $sourceNotes) {
    Write-Host "ERROR: No matching NOTES file found in $nasDir. Prior nightly was not touched."
    exit 10
}
$promotionStatus = Invoke-NightlyPromotion -ZipPath $sourceZip.FullName -NotesPath $sourceNotes.FullName -Destination $DropboxRoot -Version $expected
if ($promotionStatus -ne 0) { exit $promotionStatus }

Write-Host ""
Write-Host "============================================"
Write-Host "Nightly published: $expected"
Write-Host "============================================"

# --- Seal: also snapshot Claude's memory folder ---------------------------
# Rides this end-of-day trigger so every "done developing" ceremony also
# captures a matching memory snapshot to NAS historical\memory\. Memory
# backup is non-fatal -- if it fails, the nightly publish still counts.
$memoryScript = Join-Path $RepoRoot 'backup-memory-to-nas.ps1'
if (Test-Path $memoryScript) {
    try {
        & $memoryScript -NasRoot $NasRoot
    } catch {
        Write-Warning "Memory snapshot step failed: $_"
        Write-Warning "(Nightly publish itself succeeded; re-run backup-memory-to-nas.ps1 manually if needed.)"
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
        Write-Warning "(Nightly publish itself succeeded; re-run backup-private-to-nas.ps1 manually if needed.)"
    }
} else {
    Write-Warning "backup-private-to-nas.ps1 not found at repo root; skipping private docs snapshot."
}
