# backup-codex-to-nas.ps1
#
# Snapshots the part of Codex's home folder, %USERPROFILE%\.codex, that is
# worth keeping, to the NAS as a dated series (task 575).
#
# WHY THIS EXISTS. Codex works on this project under Claude's lead, and its
# home folder holds config.toml (written by hand, with its reasoning in the
# comments), astra.config.toml, its rules and skills, and its session history.
# No other seal script covers it: backup-claude-state-to-nas.ps1 takes
# ~\.claude, backup-dev-to-nas.ps1 mirrors C:\dev, and backup-private-to-nas.ps1
# takes JJFlex-private.
#
# WHAT IT TAKES: an ALLOW-LIST, never "everything except". The folder holds a
# live sign-in token and sandbox secrets, so anything Codex adds later stays
# OUT until someone decides it belongs here.
#
#   files   : config.toml, astra.config.toml, session_index.jsonl,
#             history.jsonl, version.json, installation_id
#   folders : rules, skills, sessions
#   SQLite  : memories_1.sqlite, goals_1.sqlite, state_5.sqlite,
#             thread_history_1.sqlite (each with its -wal and -shm, if any)
#
# The SQLite databases are taken ONLY when Codex is not running. While it
# runs they are open with write-ahead logs, and a copy can be half-written;
# the run then leaves them all out, says so, and exits 5.
#
# NEVER TAKEN, by construction (none is on the allow-list): auth.json,
# auth.json.lock and auth.lock (a live ChatGPT sign-in, left out for the reason
# the Claude backup leaves out .credentials.json); cap_sid and .sandbox-secrets
# (the sandbox's identity and secrets); packages (the installed CLI, about
# 3.3 GB); cache, models_cache.json, tmp, .tmp, .sandbox, .sandbox-bin;
# sandbox.*.log and logs_2.sqlite (regenerable logs). Everything else not
# named above is left out too: the orchestrator's rule for what was not judged.
#
# HOW IT WORKS (the private backup's pattern, task 872):
#   1. Stage: robocopy each allowed item to a local temp folder. A file
#      robocopy cannot read is left out, named, and the run says so (exit 5).
#   2. Zip the staged copy locally, copy the zip to the NAS, read it back.
#   3. Prune: keep the newest -KeepLast snapshots (12, as the Claude-state
#      backup keeps). Only files named codex-YYYYMMDD-HHMMSS.zip are ever
#      considered, and only after the new snapshot is confirmed on the NAS.
#      -KeepLast 0 keeps all.
#
# NEVER TEST THIS AGAINST THE REAL .codex OR THE REAL NAS PATH. Pass a scratch
# -Source and a scratch -Nas, and -CodexRunning Yes or No, so a test never
# depends on whether Codex happens to be running.
#
# Exit codes:
#   0  snapshot made, complete
#   2  Codex home folder not found
#   3  NAS folder could not be created (NAS offline?)
#   4  backup failed: no snapshot was made (nothing was pruned)
#   5  snapshot made, but something is not in it: the SQLite databases while
#      Codex runs, or files that could not be read (named above); pruning
#      still ran

param(
    [string] $Source       = "$env:USERPROFILE\.codex",
    [string] $Nas          = "\\nas.macaw-jazz.ts.net\jjflex",
    # Keep the N most recent snapshots on the NAS; 0 keeps everything.
    [int]    $KeepLast     = 12,
    # Auto asks Windows whether a Codex process is running. Yes and No are
    # for tests, and for a person who knows better than the process list.
    [ValidateSet('Auto', 'Yes', 'No')]
    [string] $CodexRunning = 'Auto'
)

$ErrorActionPreference = 'Stop'

$allowFiles   = @('config.toml', 'astra.config.toml', 'session_index.jsonl', 'history.jsonl',
                  'version.json', 'installation_id')
$allowFolders = @('rules', 'skills', 'sessions')
$allowSqlite  = @('memories_1.sqlite', 'goals_1.sqlite', 'state_5.sqlite', 'thread_history_1.sqlite')

if (-not (Test-Path -LiteralPath $Source)) {
    # Not Write-Error: under ErrorActionPreference Stop it throws, and the
    # script then exits 1 instead of the code below.
    [Console]::Error.WriteLine("Codex home folder not found: $Source")
    exit 2
}
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\')

$running = switch ($CodexRunning) {
    'Yes'   { $true }
    'No'    { $false }
    default { @(Get-Process -Name 'codex' -ErrorAction SilentlyContinue).Count -gt 0 }
}

$destDir = Join-Path (Join-Path $Nas 'historical') 'codex'

Write-Host ""
Write-Host "============================================"
Write-Host "Backup Codex home to NAS"
Write-Host "  source : $Source"
Write-Host "  NAS    : $destDir"
Write-Host "  keep   : $(if ($KeepLast -gt 0) { "newest $KeepLast" } else { 'all' })"
Write-Host "  Codex  : $(if ($running) { 'running, so its SQLite databases are left out' } else { 'not running' })"
Write-Host "============================================"

if (-not (Test-Path -LiteralPath $destDir)) {
    try {
        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
        Write-Host "Created NAS folder: $destDir"
    } catch {
        [Console]::Error.WriteLine("Could not create NAS folder $destDir. NAS offline? $_")
        exit 3
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zipName   = "codex-$timestamp.zip"
$zipPath   = Join-Path $destDir $zipName
$tempRoot  = [System.IO.Path]::GetTempPath()
$stage     = Join-Path $tempRoot "codex-stage-$timestamp"
$tempZip   = Join-Path $tempRoot $zipName
$rcLog     = Join-Path $tempRoot "codex-stage-$timestamp.log"

$unreadable = @()
$leftOut    = @()
$made = $false

# One robocopy call; returns its exit code. Robocopy's code is a bitmask:
# 0-7 copied or had nothing to do; 8 some files could not be copied; 16 a
# fatal error with no copying at all.
function Stage([string]$from, [string]$to, [string[]]$files, [bool]$tree) {
    $a = @($from, $to)
    if ($files) { $a += $files }
    if ($tree)  { $a += '/E' }
    $a += @('/R:0', '/W:0', '/NP', '/NJH', '/NJS', '/NDL', "/LOG+:$rcLog")
    robocopy @a | Out-Null
    return $LASTEXITCODE
}

try {
    try {
        Write-Host ""
        Write-Host "Staging..."
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        $worst = 0

        $files = @($allowFiles | Where-Object { Test-Path -LiteralPath (Join-Path $Source $_) -PathType Leaf })
        if ($running) {
            $leftOut = @($allowSqlite | Where-Object { Test-Path -LiteralPath (Join-Path $Source $_) -PathType Leaf })
        } else {
            foreach ($db in $allowSqlite) {
                foreach ($f in @($db, "$db-wal", "$db-shm")) {
                    if (Test-Path -LiteralPath (Join-Path $Source $f) -PathType Leaf) { $files += $f }
                }
            }
        }
        if ($files.Count -gt 0) {
            $rc = Stage $Source $stage $files $false
            if ($rc -gt $worst) { $worst = $rc }
        }
        foreach ($d in $allowFolders) {
            $from = Join-Path $Source $d
            if (Test-Path -LiteralPath $from -PathType Container) {
                $rc = Stage $from (Join-Path $stage $d) $null $true
                if ($rc -gt $worst) { $worst = $rc }
            }
        }

        if ($worst -ge 16) { throw "robocopy could not stage the Codex home (exit code $worst)" }
        if ($worst -ge 8) {
            # The files it could not read, by name, from its own log.
            if (Test-Path -LiteralPath $rcLog) {
                $unreadable = @(Select-String -LiteralPath $rcLog -Pattern 'ERROR \d+ \(0x[0-9A-Fa-f]+\) Copying File (.+)$' |
                                ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() } | Select-Object -Unique)
            }
            if ($unreadable.Count -eq 0) { $unreadable = @("(robocopy exit code $worst; see its log)") }
        }

        $staged = @(Get-ChildItem -LiteralPath $stage -Recurse -File -ErrorAction SilentlyContinue)
        $rawMB  = [math]::Round((($staged | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
        Write-Host "  staged $($staged.Count) files ($rawMB MB)"
        if ($staged.Count -eq 0) { throw "nothing was staged" }

        # Belt and braces over the allow-list: a sign-in token or a sandbox
        # secret in the stage is a defect, and no snapshot is better than one
        # that carries it.
        # Checked at the top of the stage, where the home folder's own items
        # land, so a folder of the same name deep inside skills is not refused.
        $forbidden = @($staged | Where-Object {
            $rel = $_.FullName.Substring($stage.Length)
            $rel -match '^\\(auth\.json|auth\.json\.lock|auth\.lock|cap_sid|models_cache\.json|logs_2\.sqlite[^\\]*|sandbox\.[^\\]*\.log)$' -or
            $rel -match '^\\(\.sandbox[^\\]*|packages|cache|tmp|\.tmp|plugins)\\'
        })
        if ($forbidden.Count -gt 0) { throw "the stage holds something that must never be taken: $($forbidden[0].FullName)" }

        Write-Host "Compressing..."
        if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stage, $tempZip,
            [System.IO.Compression.CompressionLevel]::Optimal, $false)

        Write-Host "Copying to NAS..."
        Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
        $local  = (Get-Item -LiteralPath $tempZip).Length
        $landed = if (Test-Path -LiteralPath $zipPath) { (Get-Item -LiteralPath $zipPath).Length } else { -1 }
        if ($landed -ne $local) { throw "the snapshot on the NAS is $landed bytes, not $local; not trusting it" }
        $made = $true

        Write-Host ""
        Write-Host "Snapshot saved: $zipName"
        Write-Host "  files : $($staged.Count)"
        Write-Host ("  size  : {0:N2} MB" -f ($landed / 1MB))
        Write-Host "  path  : $zipPath"
    } catch {
        [Console]::Error.WriteLine("Backup failed: $_")
        exit 4
    }

    # Prune only now that the new snapshot is confirmed on the NAS, and only
    # files this script names.
    if ($made -and $KeepLast -gt 0) {
        $old = @(Get-ChildItem -LiteralPath $destDir -File |
                 Where-Object { $_.Name -match '^codex-\d{8}-\d{6}\.zip$' } |
                 Sort-Object Name -Descending | Select-Object -Skip $KeepLast)
        foreach ($o in $old) {
            try {
                Remove-Item -LiteralPath $o.FullName -Force
                Write-Host "  pruned: $($o.Name)"
            } catch {
                Write-Host "  could not prune $($o.Name): $_"
            }
        }
    }
} finally {
    if (Test-Path -LiteralPath $stage)   { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $rcLog)   { Remove-Item -LiteralPath $rcLog -Force -ErrorAction SilentlyContinue }
}

$partial = $false
if ($leftOut.Count -gt 0) {
    Write-Host ""
    Write-Host "Codex is running, so these SQLite databases are NOT in this snapshot (a copy could be half-written):"
    foreach ($f in $leftOut) { Write-Host "  $f" }
    Write-Host "Run it again once Codex has stopped to take them."
    $partial = $true
}
if ($unreadable.Count -gt 0) {
    Write-Host ""
    Write-Host "WARNING: $($unreadable.Count) file(s) could not be read and are NOT in this snapshot:"
    foreach ($f in ($unreadable | Select-Object -First 20)) { Write-Host "  $f" }
    if ($unreadable.Count -gt 20) { Write-Host "  ... and $($unreadable.Count - 20) more" }
    Write-Host "Run it again once whatever holds them has finished."
    $partial = $true
}
if ($partial) { exit 5 }

Write-Host ""
Write-Host "============================================"
Write-Host "Codex home snapshot complete"
Write-Host "============================================"
exit 0
