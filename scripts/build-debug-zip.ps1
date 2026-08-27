# Zip the Debug build output for build-debug.bat.
# Called as: build-debug-zip.ps1 -SourceDir <abs path to bin dir>
#                                -DestPath  <abs path to .zip>
#
# WHY THIS EXISTS (2026-08-20). build-debug.bat used to zip with
# Compress-Archive, and on failure printed "is jjflexible.exe locked by a
# running instance?". That message was a guess, and it was wrong:
#
#   "The 'Compress-Archive' command was found in the module
#    'Microsoft.PowerShell.Archive', but the module could not be loaded."
#
# The lock explanation was not merely unverified, it was impossible.
# build-debug.bat already exits with code 6 BEFORE the build if any
# jjflexible/JJFlexRadio process is running, so by the time control reaches
# the zip step a running instance has been ruled out by the script itself.
#
# THE MECHANISM, MEASURED 2026-08-27 (task #133). This header used to name two
# independent faults. It is one fault with two necessary halves, and saying it
# the other way is wrong in a way that matters -- somebody fixing "the
# execution policy" alone would have changed nothing:
#
#   1. $env:PSModulePath IS INHERITED. Launched with a PowerShell 7 environment
#      in scope, the 5.1 child sees PS7's module directory first and resolves
#      Microsoft.PowerShell.Archive to PS7's v1.2.6 under
#      C:\program files\windowsapps\microsoft.powershell_*, ahead of its own
#      v1.0.1.0 in System32. Autoload does NOT fall back once the first
#      candidate fails.
#
#   2. THAT COPY IS A .psm1 OUTSIDE $PSHOME. Windows PowerShell exempts script
#      modules under its own System32 module directory from the execution
#      policy; it does not exempt one living under WindowsApps. Under the
#      default Restricted policy the import raises a PSSecurityException:
#      "running scripts is disabled on this system."
#
# NEITHER HALF ALONE DOES IT, and both were checked rather than reasoned:
#   - poisoned PSModulePath + -ExecutionPolicy Bypass  -> Compress-Archive works
#   - clean 5.1-only PSModulePath + Restricted policy  -> Compress-Archive works
#   - poisoned PSModulePath + Restricted policy        -> the failure above
# So 5.1's own bundled Archive module is also a script module and loads fine
# under Restricted, and PS7's 1.2.6 runs fine on 5.1 when it is allowed to
# load. The header's old claim that 1.2.6 "will not run on 5.1" was a version
# story for what is actually a file-location story.
#
# The same shadowing hits any module the 5.1 child needs, not just this one:
# under the poisoned path Get-ExecutionPolicy itself fails, because
# Microsoft.PowerShell.Security resolves the same way.
#
# THE FIX: use System.IO.Compression.ZipFile directly. It is a .NET
# Framework type in a binary assembly, not a script module, so it is immune
# to execution policy and immune to PSModulePath poisoning. It is also
# substantially faster and lower-memory than Compress-Archive, which matters
# for a self-contained publish tree (~185 MB, ~445 files).
#
# BEHAVIOUR IS DELIBERATELY UNCHANGED: the tree's CONTENTS go at the zip
# root, exactly as Compress-Archive -Path 'dir\*' did. Testers extract the
# same shape they always have. The one difference is that this includes
# hidden files, which a wildcard glob skipped -- the publish tree has none,
# and including more is the safe direction.
#
# Entries are added one at a time rather than via CreateFromDirectory, for
# one specific reason: .NET Framework's CreateFromDirectory writes entry
# names with BACKSLASH separators, which violates the ZIP spec (APPNOTE
# 4.4.17.1 requires forward slashes). Explorer and 7-Zip tolerate it; other
# tools create files with literal backslashes in the name instead of
# subdirectories. Building entries by hand lets us normalise to '/'.

param(
    [Parameter(Mandatory=$true)] [string] $SourceDir,
    [Parameter(Mandatory=$true)] [string] $DestPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
    Write-Host "ERROR: source directory not found: $SourceDir"
    exit 1
}

$srcFiles = @(Get-ChildItem -LiteralPath $SourceDir -Recurse -Force -File)
if ($srcFiles.Count -eq 0) {
    Write-Host "ERROR: source directory is empty: $SourceDir"
    exit 1
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
} catch {
    Write-Host "ERROR: could not load the .NET compression assemblies."
    Write-Host ("  " + $_.Exception.GetType().FullName + ": " + $_.Exception.Message)
    exit 1
}

# CreateFromDirectory refuses to overwrite, so clear the destination first.
# This is what Compress-Archive -Force did.
if (Test-Path -LiteralPath $DestPath) {
    try {
        Remove-Item -LiteralPath $DestPath -Force
    } catch {
        Write-Host "ERROR: could not replace the existing zip at $DestPath"
        Write-Host ("  " + $_.Exception.GetType().FullName + ": " + $_.Exception.Message)
        Write-Host "  Something else is holding that file open -- an Explorer preview"
        Write-Host "  pane or an open archive window is the usual culprit."
        exit 1
    }
}

$destDir = Split-Path -Parent $DestPath
if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
    New-Item -Path $destDir -ItemType Directory -Force | Out-Null
}

$root = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')

# Empty directories carry no files, so an entry-by-entry walk would drop
# them. Collect them separately and write explicit directory entries, so the
# extracted tree matches the built tree exactly.
$emptyDirs = @(Get-ChildItem -LiteralPath $SourceDir -Recurse -Force -Directory |
    Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Recurse -Force -File) })

$expected = $srcFiles.Count + $emptyDirs.Count

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $zip = [System.IO.Compression.ZipFile]::Open(
        $DestPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $srcFiles) {
            $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $f.FullName, $rel,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach ($d in $emptyDirs) {
            $rel = $d.FullName.Substring($root.Length + 1).Replace('\', '/') + '/'
            $zip.CreateEntry($rel) | Out-Null
        }
    } finally {
        $zip.Dispose()
    }
} catch {
    $sw.Stop()
    Write-Host "ERROR: creating the zip failed."
    Write-Host ("  " + $_.Exception.GetType().FullName)
    Write-Host ("  " + $_.Exception.Message)
    if ($_.Exception.InnerException) {
        Write-Host ("  inner: " + $_.Exception.InnerException.Message)
    }
    exit 1
}
$sw.Stop()

# Verify rather than assume. The old code checked an exit code and nothing
# else, so a truncated or empty archive would have sailed through to the NAS
# and to testers.
if (-not (Test-Path -LiteralPath $DestPath)) {
    Write-Host "ERROR: zip reported success but no file exists at $DestPath"
    exit 1
}

$zipItem = Get-Item -LiteralPath $DestPath
$entryCount = -1
try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($DestPath)
    try { $entryCount = $archive.Entries.Count } finally { $archive.Dispose() }
} catch {
    Write-Host "ERROR: the zip was written but cannot be read back."
    Write-Host ("  " + $_.Exception.GetType().FullName + ": " + $_.Exception.Message)
    exit 1
}

if ($entryCount -ne $expected) {
    Write-Host "ERROR: zip is incomplete."
    Write-Host ("  expected " + $expected + " entries (" + $srcFiles.Count +
                " files + " + $emptyDirs.Count + " empty dirs), found " + $entryCount)
    exit 1
}

$mb = [math]::Round($zipItem.Length / 1MB, 1)
$srcMb = [math]::Round((($srcFiles | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
Write-Host ("  " + $srcFiles.Count + " files, " + $srcMb + " MB -> " + $mb + " MB in " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + "s (" + $entryCount + " entries verified)")

exit 0
