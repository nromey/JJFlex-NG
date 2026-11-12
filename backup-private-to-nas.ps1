# backup-private-to-nas.ps1
# Snapshot Noel's private JJFlex docs folder to the NAS historical tree.
#
# Contents: the human-curated private docs that live outside the repo
# (detailed TODO, easter-egg manifest with unlock plaintexts, any future
# private planning). These files contain information we deliberately keep
# out of the public repo -- unlock words, internal plans, personal notes.
#
# Sibling folder to `historical\memory\`:
#   \\nas...\jjflex\historical\private\private-YYYYMMDD-HHMMSS.zip
#
# Private docs change on a human cadence (when Noel edits them), NOT per
# build. So like memory backups, they get their own sibling tree under
# historical\ rather than being nested inside per-version folders.
#
# Runs manually any time, and at every "done developing" seal (CLAUDE.md,
# step 3). RUN IT ON ITS OWN, or check its exit code: chained with another
# script by ';' its failure goes unnoticed.
#
# HOW IT WORKS (rewritten for task 872, 2026-11-11):
#   1. Stage: robocopy the private tree to a local temp folder. Robocopy
#      reads files another process holds open, where zipping the live tree
#      failed whole on ONE such file ("Stream was not readable", the
#      2026-11-04 seal, while Codex lanes wrote their logs). A file robocopy
#      still cannot read is left out, named, and the run says so (exit 5).
#   2. Leave out regenerable build trees: bin, obj, .vs, node_modules,
#      __pycache__, .pytest_cache. Codex's logs and test results ARE kept:
#      they are the evidence behind the check verdicts.
#   3. Zip the staged copy locally, copy the zip to the NAS, read it back.
#   4. Prune: keep the newest -KeepLast snapshots (30; ruled by Noel
#      2026-11-11: "There's no real need to keep months of seals"). Only
#      files named private-YYYYMMDD-HHMMSS.zip are ever considered, and only
#      after the new snapshot is confirmed on the NAS. -KeepLast 0 keeps all.
#
# NEVER TEST THE PRUNING AGAINST THE REAL NAS PATH. Pass a scratch -NasRoot.
#
# Exit codes:
#   0  snapshot made, complete
#   2  private docs folder not found
#   3  NAS folder could not be created (NAS offline?)
#   4  backup failed: no snapshot was made (nothing was pruned)
#   5  snapshot made, but some files could not be read and are not in it
#      (they are named above); pruning still ran

param(
    [string] $PrivateRoot = 'C:\Users\nrome\JJFlex-private',
    [string] $NasRoot     = '\\nas.macaw-jazz.ts.net\jjflex',
    # Keep the N most recent snapshots on the NAS; 0 keeps everything.
    [int]    $KeepLast    = 30
)

$ErrorActionPreference = 'Stop'

# Regenerable trees: build output and caches. Matched by folder NAME anywhere
# in the tree, which is how robocopy's /XD treats a bare name.
$excludeDirs = @('bin', 'obj', '.vs', 'node_modules', '__pycache__', '.pytest_cache')

Write-Host ""
Write-Host "============================================"
Write-Host "Backup JJFlex private docs to NAS"
Write-Host "  Private : $PrivateRoot"
Write-Host "  NAS     : $NasRoot"
Write-Host "  Keep    : $(if ($KeepLast -gt 0) { "newest $KeepLast" } else { 'all' })"
Write-Host "============================================"

if (-not (Test-Path $PrivateRoot)) {
    # Not Write-Error: under ErrorActionPreference Stop it throws, and the
    # script then exits 1 instead of the code below.
    [Console]::Error.WriteLine("Private docs folder not found: $PrivateRoot")
    exit 2
}

$destDir = Join-Path (Join-Path $NasRoot 'historical') 'private'
if (-not (Test-Path $destDir)) {
    try {
        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
        Write-Host "Created NAS folder: $destDir"
    } catch {
        [Console]::Error.WriteLine("Could not create NAS folder $destDir. NAS offline? $_")
        exit 3
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zipName   = "private-$timestamp.zip"
$zipPath   = Join-Path $destDir $zipName
$tempRoot  = [System.IO.Path]::GetTempPath()
$stage     = Join-Path $tempRoot "private-stage-$timestamp"
$tempZip   = Join-Path $tempRoot $zipName
$rcLog     = Join-Path $tempRoot "private-stage-$timestamp.log"

$unreadable = @()
$made = $false
try {
    try {
        Write-Host ""
        Write-Host "Staging..."
        $rcArgs = @(
            $PrivateRoot, $stage,
            '/E', '/R:0', '/W:0', '/NP', '/NJH', '/NJS', '/NDL',
            "/LOG:$rcLog"
        )
        $rcArgs += @('/XD') + $excludeDirs
        robocopy @rcArgs | Out-Null
        $rc = $LASTEXITCODE
        # Robocopy's exit code is a bitmask: 0-7 copied or had nothing to do;
        # 8 means some files could not be copied; 16 a fatal error with no
        # copying at all. It is NOT an error count.
        if ($rc -ge 16) { throw "robocopy could not stage the private docs (exit code $rc)" }
        if ($rc -ge 8) {
            # The files it could not read, by name, from its own log.
            if (Test-Path $rcLog) {
                $unreadable = @(Select-String -Path $rcLog -Pattern 'ERROR \d+ \(0x[0-9A-Fa-f]+\) Copying File (.+)$' |
                                ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() } | Select-Object -Unique)
            }
            if ($unreadable.Count -eq 0) { $unreadable = @("(robocopy exit code $rc; see its log)") }
        }

        $staged = @(Get-ChildItem $stage -Recurse -File -ErrorAction SilentlyContinue)
        $rawMB  = [math]::Round((($staged | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
        Write-Host "  staged $($staged.Count) files ($rawMB MB)"
        if ($staged.Count -eq 0) { throw "nothing was staged" }

        Write-Host "Compressing..."
        if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stage, $tempZip,
            [System.IO.Compression.CompressionLevel]::Optimal, $false)

        Write-Host "Copying to NAS..."
        Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
        $local = (Get-Item -LiteralPath $tempZip).Length
        $landed = if (Test-Path -LiteralPath $zipPath) { (Get-Item -LiteralPath $zipPath).Length } else { -1 }
        if ($landed -ne $local) { throw "the snapshot on the NAS is $landed bytes, not $local; not trusting it" }
        $made = $true

        Write-Host ""
        Write-Host "Snapshot saved: $zipName"
        Write-Host "  files : $($staged.Count)"
        Write-Host "  size  : $landed bytes"
        Write-Host "  path  : $zipPath"
    } catch {
        [Console]::Error.WriteLine("Backup failed: $_")
        exit 4
    }

    # Prune only now that the new snapshot is confirmed on the NAS, and only
    # files this script names.
    if ($made -and $KeepLast -gt 0) {
        $old = @(Get-ChildItem -LiteralPath $destDir -File |
                 Where-Object { $_.Name -match '^private-\d{8}-\d{6}\.zip$' } |
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
    if (Test-Path $stage)   { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue }
    if (Test-Path $rcLog)   { Remove-Item -LiteralPath $rcLog -Force -ErrorAction SilentlyContinue }
}

if ($unreadable.Count -gt 0) {
    Write-Host ""
    Write-Host "WARNING: $($unreadable.Count) file(s) could not be read and are NOT in this snapshot:"
    foreach ($f in ($unreadable | Select-Object -First 20)) { Write-Host "  $f" }
    if ($unreadable.Count -gt 20) { Write-Host "  ... and $($unreadable.Count - 20) more" }
    Write-Host "Run it again once whatever holds them has finished."
    exit 5
}

Write-Host ""
Write-Host "============================================"
Write-Host "Private docs snapshot complete"
Write-Host "============================================"
exit 0
