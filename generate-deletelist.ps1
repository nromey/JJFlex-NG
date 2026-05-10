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

Usage:
  generate-deletelist.ps1 -OutputDir <publish-root> -OutFile <deletelist.txt>
#>

param(
  [Parameter(Mandatory = $true)] [string] $OutputDir,
  [Parameter(Mandatory = $true)] [string] $OutFile
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $OutputDir).Path
$rootLen = $root.Length + 1

$lines = New-Object System.Collections.Generic.List[string]

Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
  $rel = $_.FullName.Substring($rootLen)
  $lines.Add('Delete "$INSTDIR\' + $rel + '"')
}

Get-ChildItem -LiteralPath $root -Directory | ForEach-Object {
  $lines.Add('RMDir /r "$INSTDIR\' + $_.Name + '"')
}

# Write ASCII without BOM — NSIS chokes on BOMs inside !included files.
[System.IO.File]::WriteAllLines($OutFile, $lines, [System.Text.UTF8Encoding]::new($false))

Write-Host ('Wrote ' + $lines.Count + ' lines to ' + $OutFile)
