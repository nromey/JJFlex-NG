# Emit the identity of a debug build, in the two places a tester can reach it.
#
# Called as: build-debug-notes.ps1 -Version <X.Y.Z.Y> -GitSha <short>
#                                  -OutPath <abs path to NOTES file>
#                                  [-Built <"yyyy-MM-dd HH:mm">]
#                                  [-BodyPath <abs path to user-supplied body>]
#                                  [-BuildInfoPath <abs path inside the build tree>]
#
# TWO OUTPUTS, ONE SOURCE OF TRUTH:
#   -OutPath        NOTES-<version>-debug.txt, which travels BESIDE the zip.
#   -BuildInfoPath  BUILD-INFO.txt, written INTO the build tree so the zip
#                   carries it and it survives extraction, re-sharing, and
#                   being renamed. Optional; when omitted only the NOTES is
#                   written.
# Both are rendered from the same identity block below, so they cannot disagree
# about what was built.
#
# WHY BUILD-INFO.txt EXISTS AT ALL (task #268, 2026-08-26). Dropbox stamps a
# delivered file with the time IT finished syncing on the recipient's machine,
# not the time it was published. Don read his copy's timestamp as a 2 AM
# publish; nobody had been awake. The reflex answer to "which build do you
# have?" is the file date, and that answer is wrong on every tester machine and
# differently wrong on each one.
#
# The fix is not to fight Dropbox -- there is no setting we control -- but to
# stop depending on a signal we do not own. The 4-part version is stamped into
# the exe at build time and the commit is known at build time, so the artifact
# can simply SAY what it is. A file's metadata belongs to whoever last moved
# it; a file's contents belong to whoever wrote them.
#
# WHY NOT JUST LATEST.txt. LATEST.txt answers "which of these is current?" for
# one folder, which is a different question and stops being true the moment a
# tester keeps two builds to compare -- exactly what they do when bisecting a
# regression. It also does not travel: forward the zip, or pull it off the NAS,
# and LATEST.txt is not there. BUILD-INFO.txt is inside the artifact, so it
# goes wherever the artifact goes. Both now carry Built and Commit; they answer
# different questions and neither replaces the other.
#
# -Built IS THE EXE'S OWN LAST-WRITE TIME, passed in by build-debug.bat rather
# than taken from the clock here. Two files generated seconds apart could
# otherwise straddle a minute boundary and disagree, and "when was it built"
# must mean the build, not the packaging step that followed it.
#
# UTF-8 WITH BOM output (2026-04-20 fix): Windows Notepad and Explorer's
# file-preview pane default to Windows-1252 for files without a BOM, which
# renders em-dashes and other Unicode as mojibake. The BOM costs 3 bytes and
# makes every Windows text-reading tool treat the file as UTF-8 unambiguously.

param(
    [Parameter(Mandatory=$true)] [string] $Version,
    [Parameter(Mandatory=$true)] [string] $GitSha,
    [Parameter(Mandatory=$true)] [string] $OutPath,
    [string] $BodyPath      = "",
    [string] $Built         = "",
    [string] $BuildInfoPath = ""
)

$ErrorActionPreference = 'Stop'

# A fallback that nobody is told about is how a wrong date becomes a confident
# one. If the caller could not supply the exe's timestamp, say so out loud and
# label the value for what it is.
if (-not $Built) {
    $Built = Get-Date -Format 'yyyy-MM-dd HH:mm'
    Write-Host "  NOTE: no -Built supplied; using the current clock, which is the"
    Write-Host "        packaging time rather than the build time."
}

# The one line that has to survive being skim-read by someone who is about to
# quote a date down a phone. First, and plain.
#
# It says "in this file" rather than "below" ON PURPOSE: the same block is
# rendered into two documents that order things differently -- the NOTES puts
# the warning above the identity, BUILD-INFO.txt puts it after -- and the first
# draft said "below" in both, which was false in one of them. Reading the
# assembled output caught it; reading the source line would not have.
$timestampWarning = @(
    'The DATE ON THIS FILE MEANS NOTHING. Dropbox re-stamps a file with the',
    'moment it finished delivering it to your machine, so Explorer will show',
    'you whenever your copy arrived. It is not when this build was made, and',
    'it is different on every tester''s machine. The Version and Built lines',
    'in this file are the real answer.'
)

$identity = @(
    "Version: $Version (Debug x64)",
    "Built:   $Built",
    "Commit:  $GitSha"
)

# --- NOTES-<version>-debug.txt ----------------------------------------------

$lines = @('JJ Flexible Radio Access -- Debug Build', '') + $timestampWarning + @('') + $identity + @('')

if ($BodyPath -and (Test-Path -LiteralPath $BodyPath)) {
    # Explicit -Encoding UTF8 so PowerShell 5.1 decodes the body correctly
    # even when the body file has no BOM (Claude Code's default Write is
    # UTF-8 without BOM). Without this, PS5.1 falls back to the system's
    # default codepage which mangles Unicode on the read side.
    $body = Get-Content -Raw -LiteralPath $BodyPath -Encoding UTF8
    $text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine + $body
} else {
    $recent = @('Recent commits:') + (git log --oneline -n 10 HEAD)
    $text = (($lines + $recent) -join [Environment]::NewLine) + [Environment]::NewLine
}

# UTF-8 WITH BOM -- see header comment. $true = emit BOM.
$utf8WithBom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($OutPath, $text, $utf8WithBom)
Write-Host "  wrote $OutPath"

# --- BUILD-INFO.txt, inside the build tree ----------------------------------

if ($BuildInfoPath) {
    $infoDir = Split-Path -Parent $BuildInfoPath
    if (-not (Test-Path -LiteralPath $infoDir -PathType Container)) {
        Write-Host "ERROR: cannot write BUILD-INFO.txt -- no folder at $infoDir"
        exit 1
    }

    $info = @('JJ Flexible Radio Access -- which build is this?', '') +
            $identity +
            @('', 'This file was written by the build, and it travels inside the zip, so',
                  'it still tells the truth after the zip has been delivered, extracted,',
                  'copied or forwarded.',
              '') + $timestampWarning + @('',
              'When you report a problem, quote the Version line exactly -- all four',
              'numbers. That is what identifies this build and nothing else does.')

    $infoText = ($info -join [Environment]::NewLine) + [Environment]::NewLine
    [System.IO.File]::WriteAllText($BuildInfoPath, $infoText, $utf8WithBom)

    if (-not (Test-Path -LiteralPath $BuildInfoPath -PathType Leaf)) {
        Write-Host "ERROR: BUILD-INFO.txt was written and is not at $BuildInfoPath"
        exit 1
    }
    Write-Host "  wrote $BuildInfoPath (ships inside the zip)"
}

exit 0
