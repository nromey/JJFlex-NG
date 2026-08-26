# Publish the Debug zip + NOTES to the Dropbox tester folder, for
# build-debug.bat --publish.
#
# Called as: publish-debug-to-dropbox.ps1 -ZipPath   <abs path to .zip>
#                                         -NotesPath <abs path to NOTES .txt>
#                                         -DestDir   <Dropbox ...\JJFlexRadio\debug>
#
# WHY THIS EXISTS (task #230, found 2026-08-25). The batch file used to do this
# inline, and it had three faults that compounded into a silent lie:
#
#   1. DESTRUCTIVE STEP FIRST. The purge of prior debug files ran
#      unconditionally, BEFORE anything was known about whether a replacement
#      could land. The build testers were actually using was deleted before the
#      new one was attempted.
#
#   2. NEITHER OPERATION WAS CHECKED. No errorlevel test, no verification that
#      the destination file existed afterwards. Copy-Item failing -- source
#      missing, Dropbox mid-sync holding a handle, file locked, path wrong --
#      left the folder EMPTY and the script carried on.
#
#   3. THE FILENAMES WERE ECHOED UNCONDITIONALLY. The console reported a
#      successful publish of a file that was not there.
#
# Any one of those is survivable. Together they mean the one observable signal
# -- "it printed the filename" -- is emitted identically whether the publish
# worked or destroyed the tester copy. That is the silent-success pattern this
# project exists to expose, sitting in the distribution path.
#
# THE ORDER IS THE FIX, and it is the whole fix:
#
#   copy  ->  verify at the destination  ->  only then purge  ->  only then
#   print what was actually seen there.
#
# A tester holding yesterday's build is in a far better position than a tester
# holding nothing, so every failure path here leaves the previous build in
# place and says plainly what did not happen. Nothing is printed as published
# until it has been read back from the destination at the right length.
#
# LATEST.txt. The "Dropbox = latest only" invariant existed so testers never
# have to guess which zip is current. Inferring that from the folder holding
# exactly one file makes the answer depend on a deletion succeeding. The file
# says it outright, is written after the copies are verified, and costs
# nothing.

param(
    [Parameter(Mandatory=$true)] [string] $ZipPath,
    [Parameter(Mandatory=$true)] [string] $NotesPath,
    [Parameter(Mandatory=$true)] [string] $DestDir
)

$ErrorActionPreference = 'Stop'

function Fail([string] $what) {
    Write-Host "ERROR: $what"
    Write-Host "  NOTHING was deleted from the Dropbox folder. Testers keep the"
    Write-Host "  build they already had."
    exit 1
}

# --- the sources, before anything at the destination is touched --------------

foreach ($p in @($ZipPath, $NotesPath)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
        Fail "source file does not exist: $p"
    }
    if ((Get-Item -LiteralPath $p).Length -eq 0) {
        Fail "source file is empty: $p"
    }
}

$zipSrc   = Get-Item -LiteralPath $ZipPath
$notesSrc = Get-Item -LiteralPath $NotesPath

# --- the destination folder --------------------------------------------------

if (-not (Test-Path -LiteralPath $DestDir -PathType Container)) {
    try {
        New-Item -Path $DestDir -ItemType Directory -Force | Out-Null
    } catch {
        Fail ("could not create the Dropbox folder $DestDir -- " + $_.Exception.Message)
    }
    if (-not (Test-Path -LiteralPath $DestDir -PathType Container)) {
        Fail "New-Item reported success but $DestDir does not exist"
    }
}

# --- copy, then read back ----------------------------------------------------

function Copy-AndVerify([System.IO.FileInfo] $src, [string] $destPath) {
    try {
        Copy-Item -LiteralPath $src.FullName -Destination $destPath -Force
    } catch {
        Fail ("copying " + $src.Name + " to Dropbox failed -- " +
              $_.Exception.GetType().Name + ": " + $_.Exception.Message +
              "`n  A Dropbox sync holding the file open is the usual cause.")
    }

    if (-not (Test-Path -LiteralPath $destPath -PathType Leaf)) {
        Fail ("Copy-Item reported success but nothing is at $destPath")
    }

    $landed = Get-Item -LiteralPath $destPath
    if ($landed.Length -ne $src.Length) {
        Fail ("$destPath is " + $landed.Length + " bytes and the source is " +
              $src.Length + " bytes. A partial copy is worse than none -- it " +
              "looks like a build.")
    }
    return $landed
}

$zipDest   = Join-Path $DestDir $zipSrc.Name
$notesDest = Join-Path $DestDir $notesSrc.Name

$zipLanded   = Copy-AndVerify $zipSrc   $zipDest
$notesLanded = Copy-AndVerify $notesSrc $notesDest

# --- ONLY NOW is it safe to remove the older ones ----------------------------
#
# A purge failure is a warning, not an error: the current build is already at
# the destination and verified, which is the invariant that actually matters.
# Leftover older zips are untidy; a missing current one is a broken tester.

$keep = @($zipLanded.Name, $notesLanded.Name)
$purged = @()
$purgeFailed = @()

foreach ($filter in @('JJFlex_*_debug*.zip', 'NOTES-*-debug*.txt')) {
    $stale = @(Get-ChildItem -LiteralPath $DestDir -Filter $filter -File -ErrorAction SilentlyContinue |
        Where-Object { $keep -notcontains $_.Name })
    foreach ($f in $stale) {
        try {
            Remove-Item -LiteralPath $f.FullName -Force
            $purged += $f.Name
        } catch {
            $purgeFailed += $f.Name
        }
    }
}

# --- say which one is current, in a file rather than by implication ----------

$latest = @(
    "The current JJ Flexible debug build in this folder:",
    "",
    "  $($zipLanded.Name)",
    "  $($notesLanded.Name)",
    "",
    "Published $(Get-Date -Format 'yyyy-MM-dd HH:mm')."
) -join "`r`n"

$latestWritten = $true
try {
    Set-Content -LiteralPath (Join-Path $DestDir 'LATEST.txt') -Value $latest -Encoding UTF8
} catch {
    $latestWritten = $false
}

# --- report what is actually there, nothing else -----------------------------

$mb = [math]::Round($zipLanded.Length / 1MB, 1)
Write-Host ("  published: " + $zipLanded.Name + " (" + $mb + " MB, verified at the destination)")
Write-Host ("  published: " + $notesLanded.Name + " (verified at the destination)")

if ($purged.Count -gt 0) {
    Write-Host ("  removed " + $purged.Count + " older file(s): " + ($purged -join ', '))
} else {
    Write-Host "  no older debug files to remove."
}

if ($purgeFailed.Count -gt 0) {
    Write-Host ("  WARNING: could not remove " + ($purgeFailed -join ', ') +
                " -- the current build is published; these are just leftovers.")
}

if (-not $latestWritten) {
    Write-Host "  WARNING: could not write LATEST.txt. The build is published; testers"
    Write-Host "           just have no note in the folder saying which file is current."
}

exit 0
