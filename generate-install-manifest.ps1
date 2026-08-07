#requires -Version 5.1
<#
QB Track M — generate install-manifest.json into the build output.

Walks the build output tree (the same tree the NSIS installer packages with
File /r) and writes a known-good manifest listing every file: relative path
(forward slashes), size in bytes, SHA-256 (lowercase hex), and FileVersion
where the file carries one. The manifest itself is excluded from its own list.

The debug bundle (DebugInfo.vb) reads this at collection time, builds a live
manifest of the actual install directory with the same schema, and diffs the
two — so a stale, corrupt, or mixed install names itself in the bundle instead
of shipping 190 MB of binaries for support to diff by hand.

Cleanup at uninstall is automatic: generate-deletelist.ps1 enumerates the
output tree recursively AFTER this script has run (install.bat runs from the
PostBuildEvent, which MSBuild schedules after this target), so the manifest
gets its own Delete line like any other packaged file.

Failures here are build failures on purpose — a build tree with an unreadable
file is a broken build, and a silently absent manifest would defeat the
self-verification. (At RUNTIME a missing manifest is tolerated; see
DebugInfo.vb.)

Output is UTF-8 without a BOM, written to a temp name and moved into place so
a cancelled build never leaves a half-written manifest behind.

Usage:
  generate-install-manifest.ps1 -OutputDir <build-output-root> [-Configuration Debug] [-Platform x64]
#>

param(
  [Parameter(Mandatory = $true)] [string] $OutputDir,
  [string] $Configuration = '',
  [string] $Platform = ''
)

$ErrorActionPreference = 'Stop'
$manifestName = 'install-manifest.json'

$timer = [System.Diagnostics.Stopwatch]::StartNew()
$root = (Resolve-Path -LiteralPath $OutputDir).Path.TrimEnd('\')
$rootLen = $root.Length + 1

$sha = [System.Security.Cryptography.SHA256]::Create()
$files = New-Object System.Collections.Generic.List[object]
$totalBytes = [long]0

Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
  $rel = $_.FullName.Substring($rootLen).Replace('\', '/')
  if ($rel -ieq $manifestName) { return }

  $stream = [System.IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite, Delete')
  try {
    $hashBytes = $sha.ComputeHash($stream)
  }
  finally {
    $stream.Dispose()
  }
  $hex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })

  $entry = [ordered]@{
    path   = $rel
    size   = [long]$_.Length
    sha256 = $hex
  }
  $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).FileVersion
  if (-not [string]::IsNullOrWhiteSpace($fv)) { $entry['fileVersion'] = $fv.Trim() }

  $files.Add([pscustomobject]$entry)
  $totalBytes += $_.Length
}

# Version stamp: the built exe's FileVersion (always the clean 4-part number;
# ProductVersion can carry a +hash suffix — same reasoning as install.bat).
$appVersion = $null
$exePath = Join-Path $root 'jjflexible.exe'
if (Test-Path -LiteralPath $exePath) {
  $appVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
}

$manifest = [ordered]@{
  schema    = 'jjflex-install-manifest/1'
  source    = 'build'
  product   = 'JJ Flexible Radio Access'
  generated = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
  fileCount = $files.Count
  totalBytes = $totalBytes
  files     = $files
}
if ($appVersion) { $manifest.Insert(3, 'version', $appVersion) }
if ($Configuration) { $manifest['configuration'] = $Configuration }
if ($Platform) { $manifest['platform'] = $Platform }

$json = ConvertTo-Json -InputObject $manifest -Depth 4

$outPath = Join-Path $root $manifestName
$tmpPath = $outPath + '.tmp'
[System.IO.File]::WriteAllText($tmpPath, $json, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tmpPath -Destination $outPath -Force

$timer.Stop()
Write-Host ('Wrote {0}: {1} files, {2:N0} bytes hashed, {3} ms' -f `
  $manifestName, $files.Count, $totalBytes, $timer.ElapsedMilliseconds)
