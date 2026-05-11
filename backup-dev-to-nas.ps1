# backup-dev-to-nas.ps1
# Mirror the entire C:\dev tree to the NAS as a rolling backup.
#
# C:\dev is Noel's working set: JJFlex-NG plus sibling repos (jjf-data,
# active worktrees, vendor research like smartsdr extracts and Dot Pad
# SDK material), plus per-project Claude state under .claude\. Most of
# that is git-backed and recoverable from origin, but several things are
# NOT: vendor research material (only on disk), per-project .claude\
# memory/sessions, uncommitted work in worktrees, anything Noel has
# cloned to look at and never pushed back.
#
# Single rolling mirror semantics: each run overwrites the previous
# snapshot. Recovery window is "today only" -- git history is the time
# machine for source repos, and memory/private already get their own
# dated snapshots via backup-memory-to-nas.ps1 / backup-private-to-nas.ps1.
#
# Destination:
#   \\nas.macaw-jazz.ts.net\jjflex\historical\dev-mirror\
#
# Exclusions: build artifacts (bin, obj, .vs), dependency caches
# (node_modules, packages, target), and IDE junk. .git is INCLUDED so
# uncommitted refs and stashes survive a drive failure.
#
# Runs manually any time, or auto-invoked at the tail of
# publish-daily-to-dropbox.ps1 alongside the memory + private backups.

param(
    [string] $DevRoot = 'C:\dev',
    [string] $NasRoot = '\\nas.macaw-jazz.ts.net\jjflex'
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "============================================"
Write-Host "Backup C:\dev mirror to NAS"
Write-Host "  Source : $DevRoot"
Write-Host "  NAS    : $NasRoot"
Write-Host "============================================"

if (-not (Test-Path $DevRoot)) {
    Write-Error "Dev folder not found: $DevRoot"
    exit 2
}

$destDir = Join-Path (Join-Path $NasRoot 'historical') 'dev-mirror'
if (-not (Test-Path $destDir)) {
    try {
        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
        Write-Host "Created NAS folder: $destDir"
    } catch {
        Write-Error "Could not create NAS folder $destDir. NAS offline? $_"
        exit 3
    }
}

# Folders to skip: build/IDE noise + dependency caches. .git is NOT
# excluded -- we deliberately preserve uncommitted refs and stashes.
$excludeDirs = @(
    'bin', 'obj', '.vs', '.vshistory',
    'node_modules', 'packages', 'dist', 'target', 'out', '.gradle',
    '.idea', '__pycache__', '.pytest_cache', '.mypy_cache',
    'TestResults'
)

# File-level skips: VS solution user state, swap files, OS junk.
$excludeFiles = @(
    '*.suo', '*.user', 'Thumbs.db', '.DS_Store', '*.swp'
)

# Robocopy semantics:
#   /MIR  -- mirror (deletes files at dest that no longer exist at src)
#   /XJ   -- skip junctions (avoid loops; dotnet drops some)
#   /XD   -- exclude these directories anywhere in the tree
#   /XF   -- exclude these file patterns
#   /R:1  -- one retry on transient errors (default 1M is absurd)
#   /W:3  -- wait 3 seconds between retries
#   /NP   -- no per-file progress (cleaner log; would flood otherwise)
#   /NDL  -- no directory list (cuts log volume further)
#   /MT:8 -- 8-thread copy (NAS over Tailscale benefits)
#
# We deliberately do NOT pass /LOG:<path> to robocopy. PowerShell 7's
# native-command argument splatter mis-parses arguments of the form
# `/FLAG:C:\path\with\colon`, splitting on the colon and producing
# `/FLAG:` and `C:\path` as two separate tokens. Robocopy then bails
# with "Invalid Parameter" before opening the log file, leaving exit 16
# and no log to diagnose with. Using PowerShell-side stdout redirection
# (`> $logPath`) sidesteps the parser entirely.
$logPath = Join-Path ([System.IO.Path]::GetTempPath()) 'backup-dev-to-nas.log'

$robocopyArgs = @(
    $DevRoot,
    $destDir,
    '/MIR', '/XJ',
    '/R:1', '/W:3',
    '/NP', '/NDL', '/MT:8'
)
$robocopyArgs += '/XD'; $robocopyArgs += $excludeDirs
$robocopyArgs += '/XF'; $robocopyArgs += $excludeFiles

Write-Host ""
Write-Host "Starting robocopy mirror (this can take a few minutes)..."
Write-Host "  log: $logPath"
Write-Host ""

$startTime = Get-Date
& robocopy @robocopyArgs *> $logPath
$exitCode = $LASTEXITCODE
$duration = (Get-Date) - $startTime

# Robocopy exit codes:
#   0    = no files copied (already up to date)
#   1    = files copied successfully
#   2    = extras (files at dest not at src) -- normal with /MIR cleanup
#   3    = 1+2 combined
#   4-7  = mismatches/skipped, but no fatal error
#   8+   = fatal failure
if ($exitCode -ge 8) {
    Write-Error "Robocopy failed with exit code $exitCode. See $logPath."
    exit 4
}

# Stat the mirror for the seal log
$fileCount = (Get-ChildItem -Path $destDir -File -Recurse -ErrorAction SilentlyContinue).Count
$totalSize = (Get-ChildItem -Path $destDir -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
$sizeGB    = if ($totalSize) { [math]::Round($totalSize / 1GB, 2) } else { 0 }

Write-Host ""
Write-Host "Mirror complete (robocopy exit $exitCode)"
Write-Host "  files    : $fileCount"
Write-Host "  size     : $sizeGB GB"
Write-Host "  duration : $([math]::Round($duration.TotalSeconds, 1)) s"
Write-Host "  path     : $destDir"
Write-Host ""
Write-Host "============================================"
Write-Host "Dev mirror complete"
Write-Host "============================================"

# Robocopy success codes (0-7) become PowerShell $LASTEXITCODE, which
# propagates to the script's exit code if we don't explicitly override.
# Calling shells (and the test harness) read non-zero as failure even
# though robocopy 1/2/3 are normal success outcomes. Force exit 0 on
# the success path so callers see a clean success.
exit 0
