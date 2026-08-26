# Archive the Debug build to the NAS history tree, for build-debug.bat.
#
# Called as: archive-debug-to-nas.ps1 -ZipPath   <abs path to .zip>
#                                     -NotesPath <abs path to NOTES .txt>
#                                     -ExePath   <abs path to jjflexible.exe>
#                                     -PdbPath   <abs path to jjflexible.pdb>
#                                     -DestDir   <...\historical\<ver>\x64-debug>
#                                     -Stamp     yyyyMMdd-HHmm
#                                     -Version   4.1.16.NNNN
#
# WHY THIS EXISTS (task #230). The batch file fired four Copy-Item calls at the
# NAS and then echoed three filenames, checking nothing in between -- the same
# defect as the Dropbox publish next door, in the layer that is supposed to be
# the durable one. A Tailscale drop mid-copy, a full volume, or a path typo
# produced the identical console output as a good archive.
#
# That is worse here than at the Dropbox end, not better. Dropbox is the CURRENT
# layer and a bad publish is noticed within a day, by a tester. The NAS is the
# HISTORY layer: nobody looks until they need to bisect, which is months later
# and precisely when the missing build cannot be reconstructed.
#
# EXIT CODES, because the caller has to tell three outcomes apart:
#   0   everything archived and read back at the right length
#   10  NAS not reachable -- expected offline, nothing attempted, not a failure
#   1   NAS reachable and something did not land -- a real failure, say so
#
# The distinction between 10 and 1 is the point. Collapsing them would either
# make every offline build look broken or make every broken archive look
# offline, and the second is how history goes quietly missing.

param(
    [Parameter(Mandatory=$true)] [string] $ZipPath,
    [Parameter(Mandatory=$true)] [string] $NotesPath,
    [Parameter(Mandatory=$true)] [string] $ExePath,
    [Parameter(Mandatory=$true)] [string] $PdbPath,
    [Parameter(Mandatory=$true)] [string] $DestDir,
    [Parameter(Mandatory=$true)] [string] $Stamp,
    [Parameter(Mandatory=$true)] [string] $Version
)

$ErrorActionPreference = 'Stop'

# The historical root is the parent of <ver>\x64-debug. Reachability is a
# question about the share, not about a folder that this run may be creating.
$historicalRoot = Split-Path -Parent (Split-Path -Parent $DestDir)

if (-not (Test-Path -LiteralPath $historicalRoot)) {
    Write-Host "  NAS not reachable at $historicalRoot"
    exit 10
}

$problems = @()

function Copy-AndVerify([string] $srcPath, [string] $destPath, [string] $label) {
    if (-not (Test-Path -LiteralPath $srcPath -PathType Leaf)) {
        $script:problems += "$label -- source missing at $srcPath"
        return $null
    }
    $src = Get-Item -LiteralPath $srcPath

    try {
        Copy-Item -LiteralPath $srcPath -Destination $destPath -Force
    } catch {
        $script:problems += ("$label -- " + $_.Exception.GetType().Name + ": " + $_.Exception.Message)
        return $null
    }

    if (-not (Test-Path -LiteralPath $destPath -PathType Leaf)) {
        $script:problems += "$label -- the copy reported success and nothing is at $destPath"
        return $null
    }

    $landed = Get-Item -LiteralPath $destPath
    if ($landed.Length -ne $src.Length) {
        $script:problems += ("$label -- landed at " + $landed.Length + " bytes, source is " +
                             $src.Length + " bytes (truncated copy)")
        return $null
    }
    return $landed
}

try {
    New-Item -Path $DestDir -ItemType Directory -Force | Out-Null
} catch {
    Write-Host ("ERROR: could not create $DestDir -- " + $_.Exception.Message)
    exit 1
}
if (-not (Test-Path -LiteralPath $DestDir -PathType Container)) {
    Write-Host "ERROR: New-Item reported success but $DestDir does not exist."
    exit 1
}

# Timestamped, never overwritten -- the bisect material.
$zipName   = "JJFlex_${Version}_x64_debug_${Stamp}.zip"
$notesName = "NOTES-${Version}-debug_${Stamp}.txt"

$zip   = Copy-AndVerify $ZipPath   (Join-Path $DestDir $zipName)   $zipName
$notes = Copy-AndVerify $NotesPath (Join-Path $DestDir $notesName) $notesName

# Refreshed per version -- the symbolication target.
$exe = Copy-AndVerify $ExePath (Join-Path $DestDir 'jjflexible.exe') 'jjflexible.exe'
$pdb = Copy-AndVerify $PdbPath (Join-Path $DestDir 'jjflexible.pdb') 'jjflexible.pdb'

# Only things read back from the NAS get named.
if ($zip)   { Write-Host ("  archived: " + $zip.Name + " (" + [math]::Round($zip.Length / 1MB, 1) + " MB, verified)") }
if ($notes) { Write-Host ("  archived: " + $notes.Name + " (verified)") }
if ($exe -and $pdb) { Write-Host "  archived: jjflexible.exe + .pdb (refreshed, verified)" }
elseif ($exe)       { Write-Host "  archived: jjflexible.exe (verified)" }
elseif ($pdb)       { Write-Host "  archived: jjflexible.pdb (verified)" }

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "ERROR: the NAS archive is INCOMPLETE. What did not land:"
    foreach ($p in $problems) { Write-Host "  - $p" }
    Write-Host ""
    Write-Host "  The zip is still in %TEMP% -- nothing has been lost yet, but this"
    Write-Host "  version has no bisectable copy on the NAS until it is archived."
    exit 1
}

exit 0
