#requires -Version 5.1
<#
Sprint 46 Track E (#556, and the reason it matters is #543).

Answers one question that nothing in this repo could answer before:
**is JJFlexRadio.chm current with the help markdown it was built from?**

## Why a stamp, and not the obvious checks

Three obvious approaches were tried first and all three are unusable:

1. **Search the CHM for a distinctive help string.** CHM content is
   LZX-compressed, so a plain string search cannot see inside it. Every absence
   it reports is meaningless - including the positive control. This cost a real
   measurement in #543.

2. **Compare mtimes.** Git sets mtime on checkout, so after any branch switch
   the CHM and every .md file share a timestamp and the comparison measures
   nothing.

3. **Hash the CHM itself.** Measured 2026-09-06: two consecutive
   `build-help.bat` runs over an *unchanged* source tree produce CHMs with
   different SHA-256 hashes and different byte lengths. **hhc.exe output is not
   reproducible**, so no hash, size or diff of the CHM can ever mean "up to
   date". This is also, on its own, why the CHM could never stop showing as
   modified while it was tracked.

So the stamp hashes the **sources**, not the artifact, and records that hash
beside the CHM at the moment the CHM is compiled. A later Check recomputes the
source hash and compares. Deterministic, immune to mtime, immune to hhc's
nondeterminism.

## What is hashed

Every file the compiled help is built FROM:

  - docs\help\md\*.md, except whats-new.md
  - docs\CHANGELOG.md, which IS whats-new.md (build-help.bat copies it in, so
    hashing the copy would report "fresh" for a changelog edit that has not been
    imported yet)
  - jjflex-help.hhp, toc.hhc, index.hhk, style.css

Deliberately NOT hashed: build-help.bat and convert-md.ps1. A comment-only edit
to a script would demand a help rebuild, and a guard that cries wolf is a guard
people route around.

## Modes

  -Mode Write   compile-time. Writes JJFlexRadio.chm.stamp next to the CHM.
                Called by build-help.bat after a successful compile.

  -Mode Check   package-time. Verifies, in this order:
                  a. the built CHM exists in docs\help\
                  b. the stamp exists and matches the current source hash
                  c. -PackagedChm, if given, exists and is byte-identical to
                     the built CHM - i.e. the build actually copied it into the
                     publish output that is about to be packaged
                Exits non-zero, loudly, on any of them.

Check (c) is the half that pays for #556. Once the CHM stopped being committed,
"it is in the tree" and "it reached the publish output" became different
statements, and the .vbproj copies it under Condition="Exists(...)" - which
means a missing CHM produces no error at all, just an installer with no help in
it. That is a silent accessibility regression, so it is now a loud one.

Usage:
  help-stamp.ps1 -Mode Write
  help-stamp.ps1 -Mode Check
  help-stamp.ps1 -Mode Check -PackagedChm "<publish-dir>\JJFlexRadio.chm"
#>

param(
  [ValidateSet('Write', 'Check')] [string] $Mode = 'Check',
  [string] $HelpDir = $PSScriptRoot,
  [string] $PackagedChm = ''
)

$ErrorActionPreference = 'Stop'

$helpDir   = (Resolve-Path -LiteralPath $HelpDir).Path

# Normalised for the message, not for the lookup. install.bat builds this path
# by concatenating %~dp0 - which ends in a backslash - with the output subpath,
# so the raw string reads "C:\dev\JJFlex-NG\\bin\x64\...". Test-Path does not
# care; a person reading the error aloud does. GetFullPath is a string
# operation and does not require the file to exist, which matters because the
# whole point here is to report the ones that do not.
if ($PackagedChm -ne '') {
  $PackagedChm = [System.IO.Path]::GetFullPath($PackagedChm)
}
$chmPath   = Join-Path $helpDir 'JJFlexRadio.chm'
$stampPath = Join-Path $helpDir 'JJFlexRadio.chm.stamp'
$mdDir     = Join-Path $helpDir 'md'
$changelog = Join-Path (Split-Path -Parent $helpDir) 'CHANGELOG.md'

# Hashing goes through System.Security.Cryptography directly rather than
# Get-FileHash, matching generate-install-manifest.ps1. This is not style:
# measured on this machine 2026-09-06, `powershell -NoProfile` (5.1) has NO
# Get-FileHash at all. PSModulePath lists PowerShell 7's module directory ahead
# of Windows PowerShell's, so 5.1 binds Microsoft.PowerShell.Utility 7.0.0.0,
# which it cannot load, and the cmdlet simply is not there - CommandNotFound,
# from a cmdlet that has shipped in the box since 4.0. Every caller of this
# script invokes it as `powershell -NoProfile -File`, so a guard built on
# Get-FileHash would fail on the machine that cuts the release.
function Get-Sha256File([string] $Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $stream = [System.IO.File]::OpenRead($Path)
    try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
  } finally { $sha.Dispose() }
  return ([System.BitConverter]::ToString($bytes)).Replace('-', '')
}

function Get-HelpSourceFiles {
  $files = New-Object System.Collections.Generic.List[object]

  if (Test-Path -LiteralPath $mdDir) {
    Get-ChildItem -LiteralPath $mdDir -Filter '*.md' -File |
      Where-Object { $_.Name -ne 'whats-new.md' } |
      Sort-Object Name |
      ForEach-Object { $files.Add([pscustomobject]@{ Key = 'md/' + $_.Name; Path = $_.FullName }) }
  }

  if (Test-Path -LiteralPath $changelog) {
    $files.Add([pscustomobject]@{ Key = 'CHANGELOG.md'; Path = $changelog })
  }

  foreach ($n in @('jjflex-help.hhp', 'toc.hhc', 'index.hhk', 'style.css')) {
    $p = Join-Path $helpDir $n
    if (Test-Path -LiteralPath $p) {
      $files.Add([pscustomobject]@{ Key = $n; Path = $p })
    }
  }

  return $files
}

function Get-HelpSourceHash {
  $files = Get-HelpSourceFiles
  if ($files.Count -eq 0) {
    throw "No help source files found under $helpDir. This is not a JJ Flexible help directory."
  }

  # One "key:sha256" line per file, ordered by key, hashed as a whole. The key
  # is included so that renaming a file changes the result even when the bytes
  # do not, and so that a deleted file is not silently equivalent to an added one.
  $sb = New-Object System.Text.StringBuilder
  foreach ($f in ($files | Sort-Object -Property Key -CaseSensitive)) {
    $h = Get-Sha256File $f.Path
    [void]$sb.AppendLine($f.Key + ':' + $h)
  }

  $bytes  = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
  $sha    = [System.Security.Cryptography.SHA256]::Create()
  try   { $digest = $sha.ComputeHash($bytes) }
  finally { $sha.Dispose() }

  return [pscustomobject]@{
    Hash  = ([System.BitConverter]::ToString($digest)).Replace('-', '')
    Count = $files.Count
  }
}

function Read-StampHash {
  if (-not (Test-Path -LiteralPath $stampPath)) { return $null }
  foreach ($line in (Get-Content -LiteralPath $stampPath)) {
    if ($line -match '^\s*sources-sha256\s*=\s*([0-9A-Fa-f]{64})\s*$') {
      return $Matches[1].ToUpperInvariant()
    }
  }
  return $null
}

function Write-Loud([string[]] $lines) {
  Write-Host ''
  foreach ($l in $lines) { Write-Host $l }
  Write-Host ''
}

# ---------------------------------------------------------------------------
if ($Mode -eq 'Write') {
  if (-not (Test-Path -LiteralPath $chmPath)) {
    Write-Loud @(
      'ERROR: asked to stamp the help build, but there is no CHM at',
      "  $chmPath",
      'Nothing was written. A stamp with no artifact beside it would report a',
      'help build that never happened.'
    )
    exit 1
  }

  $src = Get-HelpSourceHash
  $out = @(
    '# JJ Flexible compiled-help build stamp.',
    '# Written by docs\help\help-stamp.ps1 when the CHM is compiled; read by',
    '# install.bat and build-debug.bat before the CHM is packaged. Generated,',
    '# untracked, and safe to delete - the next help build writes it again.',
    ('sources-sha256=' + $src.Hash),
    ('source-files=' + $src.Count),
    ('chm-bytes=' + (Get-Item -LiteralPath $chmPath).Length),
    ('stamped=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
  )
  [System.IO.File]::WriteAllLines($stampPath, $out, [System.Text.UTF8Encoding]::new($false))
  Write-Host ("Help stamp written: " + $src.Count + " source files, sources-sha256 " + $src.Hash.Substring(0, 12) + "...")
  exit 0
}

# ---------------------------------------------------------------------------
# Check
$rebuild = @(
  'Rebuild the help and try again:',
  '  docs\help\build-help.bat',
  'It needs pandoc and HTML Help Workshop. If either is missing, build-help.bat',
  'says which and where to get it.'
)

if (-not (Test-Path -LiteralPath $chmPath)) {
  Write-Loud (@(
    'ERROR: the compiled help file does not exist.',
    "  expected: $chmPath",
    '',
    'The CHM is the in-app help. It is a build artifact and is no longer',
    'committed (#556), so a tree that has never built help does not have one.',
    'Packaging now would produce an installer whose Help menu opens nothing -',
    'a silent accessibility regression, which is exactly what this check exists',
    'to make loud.',
    ''
  ) + $rebuild)
  exit 1
}

$src     = Get-HelpSourceHash
$stamped = Read-StampHash

if ($null -eq $stamped) {
  Write-Loud (@(
    'ERROR: the compiled help file has no build stamp, so there is no way to',
    'tell whether it matches the help markdown.',
    "  chm:   $chmPath",
    "  stamp: $stampPath (missing or unreadable)",
    '',
    'Do not reason from the timestamps instead. Git sets mtime on checkout, so',
    'after a branch switch the CHM and the .md files share one and the',
    'comparison measures nothing.',
    ''
  ) + $rebuild)
  exit 1
}

if ($stamped -ne $src.Hash) {
  Write-Loud (@(
    'ERROR: the compiled help file is STALE. The help markdown has changed',
    'since this CHM was built, so packaging it would ship help that does not',
    'describe the build it ships with.',
    "  chm:            $chmPath",
    ('  stamped hash:   ' + $stamped.Substring(0, 16) + '...'),
    ('  current hash:   ' + $src.Hash.Substring(0, 16) + '...'),
    ('  sources hashed: ' + $src.Count + ' files'),
    '',
    'This is #543: the CHM was 21 pages behind in released builds from',
    '2026-08-30 to 2026-09-05 and nobody noticed, because nothing complained.',
    'This is the thing that complains.',
    ''
  ) + $rebuild)
  exit 1
}

if ($PackagedChm -ne '') {
  if (-not (Test-Path -LiteralPath $PackagedChm)) {
    Write-Loud @(
      'ERROR: the help file is current in docs\help, but it never reached the',
      'build output that is about to be packaged.',
      "  expected: $PackagedChm",
      '',
      'JJFlexRadio.vbproj copies it with Condition="Exists(...)", so a CHM that',
      'was absent when the compiler ran is skipped without any error. Build the',
      'help FIRST, then build the project, then package.'
    )
    exit 1
  }

  $a = Get-Sha256File $chmPath
  $b = Get-Sha256File $PackagedChm
  if ($a -ne $b) {
    Write-Loud @(
      'ERROR: the help file in the build output is not the one in docs\help.',
      "  built:    $chmPath",
      "  packaged: $PackagedChm",
      '',
      'The copy is PreserveNewest, so this is what a help rebuild AFTER the',
      'project build looks like: the output still holds the previous CHM.',
      'Rebuild the project so the current help is copied out, then package.'
    )
    exit 1
  }

  Write-Host ("Help check: CHM current with " + $src.Count + " source files, and the packaged copy matches.")
  exit 0
}

Write-Host ("Help check: CHM current with " + $src.Count + " source files.")
exit 0
