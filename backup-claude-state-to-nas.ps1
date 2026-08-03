# backup-claude-state-to-nas.ps1
# Snapshot the whole Claude Code state tree to the NAS historical tree.
#
# WHY THIS EXISTS (added 2026-08-02): backup-memory-to-nas.ps1 captures only
# the memory\ subfolders. It does NOT capture session transcripts -- the
# .jsonl files under .claude\projects\<slug>\ that hold every conversation
# Claude Code has ever had. Those are the only record of a session's
# reasoning, and they are the thing `claude --resume` reads.
#
# They are also on a retention timer. Claude Code sweeps transcripts older
# than cleanupPeriodDays at startup. On 2026-08-01 that sweep silently
# removed nine sessions from June across Civ VI Access and the flashdrive
# project. cleanupPeriodDays is now pinned to 365 in ~\.claude\settings.json,
# but retention only widens the window -- it is not a backup. This is.
#
# NOT covered by any other backup path:
#   - backup-dev-to-nas.ps1 mirrors C:\dev; this tree is under the user profile
#   - backup-memory-to-nas.ps1 takes memory\ only
#   - git covers none of it
#
# Layout on the NAS:
#   historical\claude-state\claude-state-YYYYMMDD-HHMMSS.zip
#
# Runs manually any time, or as part of the end-of-day seal.

param(
    [string] $ClaudeRoot = 'C:\Users\nrome\.claude',

    # ~\.claude.json lives OUTSIDE .claude\ but holds per-project trust,
    # history pointers, and MCP config. Useless to lose alongside the rest.
    [string] $ClaudeJson = 'C:\Users\nrome\.claude.json',

    [string] $NasRoot = '\\nas.macaw-jazz.ts.net\jjflex',

    # file-history\ is ~150 MB of pre-edit file snapshots backing /rewind.
    # Useful but bulky and short-lived in value, so it is opt-in.
    [switch] $IncludeFileHistory,

    # Keep the N most recent snapshots on the NAS; 0 keeps everything.
    [int] $KeepLast = 12
)

$ErrorActionPreference = 'Stop'

# Regenerable, machine-local, or secret -- none of it belongs in a snapshot.
#   cache/plugins/chrome  : re-downloaded on demand
#   shell-snapshots       : per-shell scratch, meaningless once the shell exits
#   session-env/paste-cache/downloads : transient
$excludeDirs = @(
    'cache', 'plugins', 'chrome', 'downloads',
    'paste-cache', 'session-env', 'shell-snapshots'
)
if (-not $IncludeFileHistory) { $excludeDirs += 'file-history' }

# .credentials.json is a live OAuth token. Re-auth is one `claude` launch, so
# copying it to a network share buys nothing and widens its blast radius.
$excludeFiles = @('.credentials.json', 'daemon.lock')

Write-Host ""
Write-Host "============================================"
Write-Host "Backup Claude Code state to NAS"
Write-Host "  Source   : $ClaudeRoot"
Write-Host "  NAS      : $NasRoot"
Write-Host "  History  : $(if ($IncludeFileHistory) { 'included' } else { 'excluded (-IncludeFileHistory to add)' })"
Write-Host "============================================"

if (-not (Test-Path $ClaudeRoot)) {
    Write-Error "Claude root not found: $ClaudeRoot"
    exit 2
}

$dest = Join-Path (Join-Path $NasRoot 'historical') 'claude-state'
if (-not (Test-Path $dest)) {
    try {
        New-Item -Path $dest -ItemType Directory -Force | Out-Null
        Write-Host "Created NAS folder: $dest"
    } catch {
        Write-Error "Could not create NAS folder $dest. NAS offline? $_"
        exit 3
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zipName   = "claude-state-$timestamp.zip"
$stage     = Join-Path ([System.IO.Path]::GetTempPath()) "claude-state-$timestamp"
$tempZip   = Join-Path ([System.IO.Path]::GetTempPath()) $zipName

try {
    # Stage first so the zip is built from a stable set of files. Claude Code
    # appends to the live session's .jsonl while this runs; robocopy tolerates
    # that, whereas zipping the live tree can trip on a file mid-write.
    Write-Host ""
    Write-Host "Staging..."
    $rcArgs = @(
        $ClaudeRoot, (Join-Path $stage '.claude'),
        '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP'
    )
    if ($excludeDirs.Count  -gt 0) { $rcArgs += @('/XD') + $excludeDirs }
    if ($excludeFiles.Count -gt 0) { $rcArgs += @('/XF') + $excludeFiles }

    robocopy @rcArgs | Out-Null
    # Robocopy uses a bitmask: 0-7 means files were copied / nothing to do.
    # 8 and above are genuine failures. $LASTEXITCODE is NOT an error count.
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

    if (Test-Path $ClaudeJson) {
        Copy-Item -LiteralPath $ClaudeJson -Destination (Join-Path $stage '.claude.json') -Force
    } else {
        Write-Warning "  $ClaudeJson not found -- skipping"
    }

    $staged = @(Get-ChildItem $stage -Recurse -File)
    $rawMB  = [math]::Round((($staged | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
    Write-Host "  staged $($staged.Count) files ($rawMB MB)"

    Write-Host "Compressing..."
    if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # ZipFile beats Compress-Archive by a wide margin at this size, and the
    # tree is nearly all JSON, so Optimal earns its keep (~10:1 typical).
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $tempZip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $zipMB = [math]::Round((Get-Item $tempZip).Length / 1MB, 1)
    Write-Host "  zip is $zipMB MB"

    Write-Host "Copying to NAS..."
    $zipPath = Join-Path $dest $zipName
    Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
    Write-Host "  saved: $zipPath"

    if ($KeepLast -gt 0) {
        $old = @(Get-ChildItem $dest -Filter 'claude-state-*.zip' |
                 Sort-Object Name -Descending | Select-Object -Skip $KeepLast)
        foreach ($o in $old) {
            Remove-Item -LiteralPath $o.FullName -Force
            Write-Host "  pruned: $($o.Name)"
        }
    }
} finally {
    if (Test-Path $stage)   { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "============================================"
Write-Host "Claude state snapshot complete"
Write-Host "============================================"
exit 0
