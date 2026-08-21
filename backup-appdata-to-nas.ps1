# backup-appdata-to-nas.ps1
#
# Snapshots the OPERATOR'S CONFIGURATION out of %AppData%\JJFlexRadio\ to the
# NAS, as a dated series. Small, fast, and safe to run often.
#
# WHY THIS EXISTS. On 2026-08-21 a background agent launched its own build from
# a worktree, and because every instance shares %AppData% regardless of which
# binary started it, that build ran its default-key merge against Noel's LIVE
# configuration and rewrote KeyDefs.xml. Nothing was obviously broken, but there
# was NO PRIOR COPY ANYWHERE to diff against, so "did that damage anything?"
# could not be answered. It still cannot, for that day.
#
# Nothing else covers this directory. backup-dev-to-nas.ps1 mirrors C:\dev;
# backup-claude-state-to-nas.ps1 takes ~\.claude; backup-private-to-nas.ps1
# takes JJFlex-private. All three live outside %AppData%.
#
# WHAT IT TAKES, AND WHAT IT DELIBERATELY DOES NOT
#
# Config is tiny and irreplaceable — losing it means reconfiguring every key,
# audio device, radio entry and connection profile by hand, which for a blind
# operator is a genuinely expensive afternoon. Measured 2026-08-21: well under
# a megabyte all in.
#
# Diagnostics are large and regenerable. Measured the same day: Errors\ held
# 1,890.6 MB in NINE files, three of them raw .dmp at 428-516 MB each, and one
# of those was redundant because its .zip bundle sat beside it. Traces are
# rewritten constantly. Copying that on every run would make the useful part
# slow enough that it stops getting run.
#
# So: config every time, diagnostics only when asked for with -IncludeDiagnostics,
# and RAW .dmp files never — they are enormous and the .zip bundle is the thing
# a support conversation actually needs.
#
#   .\backup-appdata-to-nas.ps1                       # config only
#   .\backup-appdata-to-nas.ps1 -IncludeDiagnostics   # plus Errors\*.zip and Traces\
#   .\backup-appdata-to-nas.ps1 -Keep 30              # retention (default 24)

param(
    [string] $AppData            = "$env:APPDATA\JJFlexRadio",
    [string] $Nas                = "\\nas.macaw-jazz.ts.net\jjflex",
    [switch] $IncludeDiagnostics,
    [int]    $Keep               = 24
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $AppData)) {
    Write-Host "No JJFlexRadio AppData at $AppData" -ForegroundColor Red
    exit 2
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dest  = Join-Path $Nas "historical\appdata"
$zip   = Join-Path $dest "appdata-$stamp.zip"

Write-Host "============================================"
Write-Host "Backup JJFlexRadio AppData config to NAS"
Write-Host "  source : $AppData"
Write-Host "  NAS    : $dest"
Write-Host "  scope  : $(if ($IncludeDiagnostics) { 'config + diagnostics' } else { 'config only' })"
Write-Host "============================================"

$stage = Join-Path $env:TEMP "jjflex-appdata-$stamp"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$copied = 0
$bytes  = 0

# $Recurse defaults ON for named subdirectories, and must be OFF for the root.
# First run, 2026-08-21: a recursive root scan for *.json swept in NINETEEN
# WebView2 browser-cache files — TrustTokenKeyCommitments, OriginTrials,
# PKIMetadata and friends. Harmless in size and wrong in principle: a backup
# that quietly includes cache is a backup that will quietly miss something.
function Take([string]$relative, [string]$filter, [bool]$Recurse = $true) {
    $src = Join-Path $AppData $relative
    if (-not (Test-Path $src)) { return }
    $items = if ($Recurse) {
        Get-ChildItem $src -Recurse -File -Filter $filter -ErrorAction SilentlyContinue
    } else {
        Get-ChildItem $src -File -Filter $filter -ErrorAction SilentlyContinue
    }
    foreach ($i in $items) {
        # Preserve the tree shape so a restore is a straight copy back.
        $rel = $i.FullName.Substring($AppData.Length).TrimStart('\')
        $out = Join-Path $stage $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
        Copy-Item $i.FullName $out -Force
        $script:copied++
        $script:bytes += $i.Length
    }
    if ($items) { Write-Host ("  {0,-24} {1,4} files" -f ($relative + '\' + $filter), $items.Count) }
}

Write-Host ""
Write-Host "Config:"
# Root-level settings, NOT recursive. KeyDefs.xml is the one that motivated
# this script; the named subdirectories below cover everything under it.
Take "." "*.xml"  $false
Take "." "*.json" $false
# Per-radio, per-operator and connection state.
Take "connection-profiles" "*"
Take "Radios" "*"
Take "Operators" "*"

if ($IncludeDiagnostics) {
    Write-Host ""
    Write-Host "Diagnostics:"
    # ZIP bundles only. The raw .dmp files are 400-500 MB each and the bundle
    # beside them is what a support conversation actually reads.
    Take "Errors" "*.zip"
    Take "Errors" "*.txt"
    Take "Traces" "*.txt"
}

if ($copied -eq 0) {
    Write-Host ""
    Write-Host "NOTHING WAS COPIED. That is not success — it means the layout" -ForegroundColor Red
    Write-Host "changed or the path is wrong. Investigate before trusting this." -ForegroundColor Red
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

Write-Host ""
Write-Host ("  staged {0} files, {1:N2} MB" -f $copied, ($bytes / 1MB))

Write-Host "Compressing..."
$localZip = Join-Path $env:TEMP "appdata-$stamp.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $localZip -CompressionLevel Optimal -Force
Write-Host ("  zip is {0:N2} MB" -f ((Get-Item $localZip).Length / 1MB))

if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Force -Path $dest | Out-Null }
Copy-Item $localZip $zip -Force
Write-Host "  saved: $zip"

Remove-Item $stage    -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $localZip -Force          -ErrorAction SilentlyContinue

# Retention. Config snapshots are tiny, so keep a generous run of them — the
# whole point is being able to diff against a state from several days ago.
$old = Get-ChildItem $dest -Filter "appdata-*.zip" -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -Skip $Keep
foreach ($o in $old) {
    Remove-Item $o.FullName -Force -ErrorAction SilentlyContinue
    Write-Host "  pruned: $($o.Name)"
}

Write-Host ""
Write-Host "============================================"
Write-Host "AppData config snapshot complete"
Write-Host "============================================"
