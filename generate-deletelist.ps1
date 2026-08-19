#requires -Version 5.1
<#
Sprint 29 Track J — generate deleteList.txt for the NSIS uninstaller.

Walks the publish output directory and emits:
  - one `Delete "$INSTDIR\<rel-path>"` line per file (recursive)
  - one `RMDir /r "$INSTDIR\<top-level-subdir>"` line per immediate subdirectory

Self-contained .NET 10 publishes 13 satellite-resource subdirs (cs/, de/, ...),
plus runtimes/, help/, Resources/, etc. The recursive Delete + per-subdir
RMDir /r mirror what the installer's `File /r` actually drops into the install
root, so the uninstaller cleans up any subfolder the publish happened to add
without us having to hardcode the list per architecture.

Output is ASCII without a BOM — NSIS !include is sensitive to BOM bytes.

Sprint 30 Track D — the two scripts now agree about what shipped. This walk used
to emit Delete lines for .pdb, .xml and runPgm.bat, which `install template.nsi`
excludes from `File /r` and therefore never installs. NSIS no-ops on a missing
file so nothing broke, but a delete list naming files the installer never wrote
cannot be read as evidence of what an install contains — and this list is the
only machine-readable answer to "what did we ship".

The exclusions are a parameter, defaulted to match the .nsi's
`File /r /x "*.pdb" /x "*.xml" /x "runPgm.bat"`. If that line changes, change
this default with it. Note which direction of mismatch actually costs something:
this list skipping a file the installer DOES ship leaves that file behind at
uninstall. The reverse — the state we are leaving — is only untidy.

Files earlier releases shipped and this one does not are swept separately at the
bottom, so upgraded machines still get cleaned. Excluding a pattern from the walk
must not silently stop removing yesterday's copy of it.

Usage:
  generate-deletelist.ps1 -OutputDir <publish-root> -OutFile <deletelist.txt>
#>

param(
  [Parameter(Mandatory = $true)] [string] $OutputDir,
  [Parameter(Mandatory = $true)] [string] $OutFile,
  # Mirrors install template.nsi's File /r exclusions. Keep in step with it.
  [string[]] $ExcludePatterns = @('*.pdb', '*.xml', 'runPgm.bat')
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $OutputDir).Path
$rootLen = $root.Length + 1

$lines = New-Object System.Collections.Generic.List[string]
$skipped = 0

Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
  $name = $_.Name
  foreach ($pattern in $ExcludePatterns) {
    if ($name -like $pattern) { $script:skipped++; return }
  }
  $rel = $_.FullName.Substring($rootLen)
  $lines.Add('Delete "$INSTDIR\' + $rel + '"')
}

Get-ChildItem -LiteralPath $root -Directory | ForEach-Object {
  $lines.Add('RMDir /r "$INSTDIR\' + $_.Name + '"')
}

# --- Legacy sweep -----------------------------------------------------------
# Litter shipped by EARLIER installers and no longer present in the publish
# output, so the walk above can never name it. Without these lines an upgraded
# machine keeps them forever, and after uninstall the install root is left
# non-empty, which makes the deliberate `RMDir "$INSTDIR"` (no /r, so it only
# succeeds when empty) fail and leave the folder behind.
#
# .pdb is wildcarded because there were a dozen of them and their names track
# project names; a .pdb under Program Files\JJFlexRadio can only have come from
# this installer. The API-doc XMLs are named explicitly rather than wildcarded,
# because *.xml in an install root is the kind of pattern that eventually eats
# something it should not.
$lines.Add('')
$lines.Add('; Litter from earlier releases (see generate-deletelist.ps1).')
$lines.Add('Delete "$INSTDIR\*.pdb"')
$lines.Add('Delete "$INSTDIR\JJLogIO.xml"')
$lines.Add('Delete "$INSTDIR\JJTrace.xml"')
$lines.Add('Delete "$INSTDIR\runPgm.bat"')

# Write ASCII without BOM — NSIS chokes on BOMs inside !included files.
[System.IO.File]::WriteAllLines($OutFile, $lines, [System.Text.UTF8Encoding]::new($false))

Write-Host ('Wrote ' + $lines.Count + ' lines to ' + $OutFile +
            ' (' + $skipped + ' files skipped as not-installed)')
