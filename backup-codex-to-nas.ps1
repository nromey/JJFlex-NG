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
# the run then leaves out every *.sqlite, -wal and -shm file AT ANY DEPTH,
# names them, and exits 5.
#
# NEVER TAKEN, at the top and AT ANY DEPTH below an allowed folder, as a file
# or a folder, in any letter case (repair 1: a recursive copy of an allowed
# folder used to carry them through): auth.json,
# auth.json.lock and auth.lock (a live ChatGPT sign-in, left out for the reason
# the Claude backup leaves out .credentials.json); cap_sid and .sandbox-secrets
# (the sandbox's identity and secrets); packages (the installed CLI, about
# 3.3 GB); cache, models_cache.json, tmp, .tmp, .sandbox, .sandbox-bin;
# sandbox.*.log and logs_2.sqlite (regenerable logs); and plugins. Everything
# else not named above is left out too: the orchestrator's rule for what was
# not judged. NO junction or symbolic link is followed or copied, at the top
# or below: an allowed item that is one is left out and named (exit 5), since
# a link can point outside the Codex home. The whole stage is checked again
# for all of this, at every depth, before it is zipped (exit 4 on a hit).
#
# HOW IT WORKS (the private backup's pattern, task 872):
#   1. Choose every file by walking the allowed items one level at a time
#      (never a recursive copy), then stage the chosen files with robocopy,
#      named per folder. A file robocopy cannot read is left out, named, and
#      the run says so (exit 5).
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
#      Codex runs, a junction or symbolic link (never followed), or files
#      that could not be read (each named above); pruning still ran

param(
    [string] $Source       = "$env:USERPROFILE\.codex",
    [string] $Nas          = "\\nas.macaw-jazz.ts.net\jjflex",
    # Keep the N most recent snapshots on the NAS; 0 keeps everything.
    [int]    $KeepLast     = 12,
    # Auto asks Windows whether a Codex process is running. Yes and No are
    # for tests, and for a person who knows better than the process list.
    # Auto is asked ONCE, at the start: a Codex started part-way through a
    # run is not seen, so run this while Codex is stopped for a clean copy.
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
# The local working names carry milliseconds and this process's id, so two
# runs started in the same second never share a stage, zip or log (repair 1).
$unique    = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + "-$PID"
$stage     = Join-Path $tempRoot "codex-stage-$unique"
$tempZip   = Join-Path $tempRoot "codex-$unique.zip"
$rcLog     = Join-Path $tempRoot "codex-stage-$unique.log"

$unreadable = @()
$leftOut    = @()
$links      = @()
$made = $false

# WHAT IS NEVER TAKEN, AT ANY DEPTH (repair 1). The allow-list says which
# root items are entered; these say what may never come out of them, at any
# depth, case-insensitively, as a file or a folder. Applied while choosing
# what to copy, and again over the whole stage before it is zipped.
$neverNames = @('auth.json', 'auth.json.lock', 'auth.lock', 'cap_sid', 'models_cache.json',
                'packages', 'cache', 'tmp', '.tmp', 'plugins')
function NeverTaken([string]$name) {
    $n = $name.ToLowerInvariant()
    return ($neverNames -contains $n) -or $n.StartsWith('.sandbox') -or
           ($n -like 'sandbox.*.log') -or $n.StartsWith('logs_2.sqlite')
}
# While Codex runs, no database file is taken, at any depth.
function IsDatabaseFile([string]$name) {
    $n = $name.ToLowerInvariant()
    return $n.EndsWith('.sqlite') -or $n.EndsWith('.sqlite-wal') -or $n.EndsWith('.sqlite-shm') -or
           $n.EndsWith('-wal') -or $n.EndsWith('-shm')
}
function IsLink($item) {
    return ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

# The files to copy, chosen by this script, never by a recursive copy: a
# folder is walked one level at a time and a junction or symbolic link is
# never entered or copied (it is named instead). Adds relative paths to $picked.
$picked = New-Object System.Collections.Generic.List[string]
function Choose([string]$root, [string]$rel) {
    foreach ($item in @(Get-ChildItem -LiteralPath (Join-Path $root $rel) -Force)) {
        $childRel = if ($rel) { Join-Path $rel $item.Name } else { $item.Name }
        if (NeverTaken $item.Name) { continue }
        if (IsLink $item) { $script:links += $childRel; continue }
        if ($item.PSIsContainer) {
            Choose $root $childRel
        } elseif ($script:running -and (IsDatabaseFile $item.Name)) {
            $script:leftOut += $childRel
        } else {
            $script:picked.Add($childRel)
        }
    }
}

# Copy the chosen files, one robocopy call per folder with the files named,
# so a file another program holds is left out and named rather than failing
# the run. /XJ is belt and braces: robocopy documents it as excluding both
# file and directory symbolic links and junctions. Returns the worst code
# (a bitmask: 0-7 copied or nothing to do; 8 some files not copied; 16 fatal).
function Stage([string[]]$relFiles) {
    $worst = 0
    foreach ($g in ($relFiles | Group-Object { Split-Path $_ -Parent })) {
        $from = if ($g.Name) { Join-Path $Source $g.Name } else { $Source }
        $to   = if ($g.Name) { Join-Path $stage $g.Name } else { $stage }
        $names = @($g.Group | ForEach-Object { Split-Path $_ -Leaf })
        for ($i = 0; $i -lt $names.Count; $i += 40) {
            $a = @($from, $to) + $names[$i..([Math]::Min($i + 39, $names.Count - 1))]
            $a += @('/XJ', '/R:0', '/W:0', '/NP', '/NJH', '/NJS', '/NDL', "/LOG+:$rcLog")
            robocopy @a | Out-Null
            if ($LASTEXITCODE -gt $worst) { $worst = $LASTEXITCODE }
        }
    }
    return $worst
}

try {
    try {
        Write-Host ""
        Write-Host "Staging..."
        New-Item -ItemType Directory -Force -Path $stage | Out-Null

        $files = @()
        foreach ($f in $allowFiles + $allowSqlite + @($allowSqlite | ForEach-Object { "$_-wal"; "$_-shm" })) {
            $p = Join-Path $Source $f
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { continue }
            $item = Get-Item -LiteralPath $p -Force
            if (IsLink $item) { $links += $f; continue }
            if ($running -and (IsDatabaseFile $f)) { $leftOut += $f; continue }
            $files += $f
        }
        foreach ($d in $allowFolders) {
            $p = Join-Path $Source $d
            if (-not (Test-Path -LiteralPath $p -PathType Container)) { continue }
            if (IsLink (Get-Item -LiteralPath $p -Force)) { $links += $d; continue }
            Choose $Source $d
        }
        $files += @($picked)
        $worst = if ($files.Count -gt 0) { Stage $files } else { 0 }

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

        # Belt and braces over the choice above: the WHOLE stage, at every
        # depth, every file and every folder on its path. A sign-in token, a
        # sandbox secret, a link, or a database while Codex runs in the stage
        # is a defect, and no snapshot is better than one that carries it.
        $forbidden = @(Get-ChildItem -LiteralPath $stage -Recurse -Force | Where-Object {
            $item = $_
            $parts = $item.FullName.Substring($stage.Length).TrimStart('\').Split('\')
            (IsLink $item) -or
            @($parts | Where-Object { NeverTaken $_ }).Count -gt 0 -or
            ($running -and -not $item.PSIsContainer -and (IsDatabaseFile $item.Name))
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
if ($links.Count -gt 0) {
    Write-Host ""
    Write-Host "These are junctions or symbolic links, and were NOT followed or taken (a link can point outside the Codex home):"
    foreach ($f in ($links | Select-Object -First 20)) { Write-Host "  $f" }
    if ($links.Count -gt 20) { Write-Host "  ... and $($links.Count - 20) more" }
    $partial = $true
}
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
