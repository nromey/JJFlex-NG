# backup-appdata-to-nas.ps1
#
# Snapshots the operator's settings tree, %AppData%\JJFlexRadio\, to the NAS
# as a dated series.
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
# WHAT IT TAKES (task 728, ruled by Noel 2026-11-19 and 2026-11-20)
#
# Everything under the settings root, EXCEPT:
#   - WebView2\   the SmartLink sign-in browser's cache: regenerable, and it
#                 holds sign-in cookies.
#   - *.dmp       raw crash dumps, 400-500 MB each. The .zip bundle beside each
#                 one IS taken; it is what a support conversation reads.
# Until then it took five config categories only, and dated measurement data
# (alarm-journal and the rest) existed in exactly one place. Noel: "Take every
# folder. We really don't need to keep all the crash dumps." This script never
# moves or deletes a dump, or anything else in the settings tree.
#
# Size, measured 2026-11-20: about 800 MB a run (Recordings alone 343 MB).
# With 8 kept, about 6.4 GB on the NAS. Re-measure rather than trusting this.
#
# HOW IT WORKS (the private backup's pattern, task 872):
#   1. Stage: robocopy the tree to a local temp folder. One file a running app
#      holds open used to stop the whole script with no snapshot. A file
#      robocopy cannot read is now left out, named, and the run says so (5).
#   2. Zip the staged copy locally, copy the zip to the NAS, read it back.
#   3. Prune: keep the newest -Keep snapshots (8). Only files named
#      appdata-YYYYMMDD-HHMMSS.zip are ever considered, and only after the new
#      snapshot is confirmed on the NAS. -Keep 0 keeps all.
#
# NEVER TEST THE PRUNING AGAINST THE REAL NAS PATH. Pass a scratch -Nas.
#
#   .\backup-appdata-to-nas.ps1              # everything but WebView2 and *.dmp
#   .\backup-appdata-to-nas.ps1 -Keep 12     # retention (default 8)
#
# -IncludeDiagnostics is still accepted, and changes nothing: diagnostics are
# taken by default now, so it is no longer needed.
#
# Exit codes:
#   0  snapshot made, complete
#   2  no JJFlexRadio settings folder at -AppData
#   3  NAS folder could not be created (NAS offline?)
#   4  backup failed: no snapshot was made (nothing was pruned)
#   5  snapshot made, but some files could not be read and are not in it
#      (they are named above); pruning still ran

param(
    [string] $AppData            = "$env:APPDATA\JJFlexRadio",
    [string] $Nas                = "\\nas.macaw-jazz.ts.net\jjflex",
    [switch] $IncludeDiagnostics,
    [int]    $Keep               = 8
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $AppData)) {
    # Not Write-Error: under ErrorActionPreference Stop it throws, and the
    # script then exits 1 instead of the code below.
    [Console]::Error.WriteLine("No JJFlexRadio AppData at $AppData")
    exit 2
}
$AppData = (Resolve-Path -LiteralPath $AppData).ProviderPath.TrimEnd('\')

$dest = Join-Path (Join-Path $Nas "historical") "appdata"

Write-Host "============================================"
Write-Host "Backup JJFlexRadio AppData to NAS"
Write-Host "  source : $AppData"
Write-Host "  NAS    : $dest"
Write-Host "  scope  : everything except WebView2 and raw .dmp crash dumps"
Write-Host "  keep   : $(if ($Keep -gt 0) { "newest $Keep" } else { 'all' })"
if ($IncludeDiagnostics) { Write-Host "  (-IncludeDiagnostics is no longer needed: diagnostics are taken by default)" }
Write-Host "============================================"

if (-not (Test-Path -LiteralPath $dest)) {
    try {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Write-Host "Created NAS folder: $dest"
    } catch {
        [Console]::Error.WriteLine("Could not create NAS folder $dest. NAS offline? $_")
        exit 3
    }
}

$stamp    = Get-Date -Format "yyyyMMdd-HHmmss"
$zipName  = "appdata-$stamp.zip"
$zip      = Join-Path $dest $zipName
$tempRoot = [System.IO.Path]::GetTempPath()
$stage    = Join-Path $tempRoot "jjflex-appdata-$stamp"
$localZip = Join-Path $tempRoot $zipName
$rcLog    = Join-Path $tempRoot "jjflex-appdata-$stamp.log"

$unreadable = @()
$made = $false
try {
    try {
        Write-Host ""
        Write-Host "Staging..."
        # WebView2 by its full path, so only the settings root's own browser
        # cache is left out; *.dmp anywhere in the tree.
        $rcArgs = @(
            $AppData, $stage,
            '/E', '/R:0', '/W:0', '/NP', '/NJH', '/NJS', '/NDL',
            "/LOG:$rcLog",
            '/XD', (Join-Path $AppData 'WebView2'),
            '/XF', '*.dmp'
        )
        robocopy @rcArgs | Out-Null
        $rc = $LASTEXITCODE
        # Robocopy's exit code is a bitmask: 0-7 copied or had nothing to do;
        # 8 means some files could not be copied; 16 a fatal error with no
        # copying at all. It is NOT an error count.
        if ($rc -ge 16) { throw "robocopy could not stage the settings tree (exit code $rc)" }
        if ($rc -ge 8) {
            # The files it could not read, by name, from its own log.
            if (Test-Path -LiteralPath $rcLog) {
                $unreadable = @(Select-String -LiteralPath $rcLog -Pattern 'ERROR \d+ \(0x[0-9A-Fa-f]+\) Copying File (.+)$' |
                                ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() } | Select-Object -Unique)
            }
            if ($unreadable.Count -eq 0) { $unreadable = @("(robocopy exit code $rc; see its log)") }
        }

        $staged = @(Get-ChildItem -LiteralPath $stage -Recurse -File -ErrorAction SilentlyContinue)
        $rawMB  = [math]::Round((($staged | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
        Write-Host "  staged $($staged.Count) files ($rawMB MB)"
        if ($staged.Count -eq 0) {
            # Not success: the layout changed or the path is wrong.
            throw "nothing was staged; investigate before trusting this"
        }

        Write-Host "Compressing..."
        if (Test-Path -LiteralPath $localZip) { Remove-Item -LiteralPath $localZip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stage, $localZip,
            [System.IO.Compression.CompressionLevel]::Optimal, $false)

        Write-Host "Copying to NAS..."
        Copy-Item -LiteralPath $localZip -Destination $zip -Force
        $local  = (Get-Item -LiteralPath $localZip).Length
        $landed = if (Test-Path -LiteralPath $zip) { (Get-Item -LiteralPath $zip).Length } else { -1 }
        if ($landed -ne $local) { throw "the snapshot on the NAS is $landed bytes, not $local; not trusting it" }
        $made = $true

        Write-Host ""
        Write-Host "Snapshot saved: $zipName"
        Write-Host "  files : $($staged.Count)"
        Write-Host ("  size  : {0:N2} MB" -f ($landed / 1MB))
        Write-Host "  path  : $zip"
    } catch {
        [Console]::Error.WriteLine("Backup failed: $_")
        exit 4
    }

    # Prune only now that the new snapshot is confirmed on the NAS, and only
    # files this script names.
    if ($made -and $Keep -gt 0) {
        $old = @(Get-ChildItem -LiteralPath $dest -File |
                 Where-Object { $_.Name -match '^appdata-\d{8}-\d{6}\.zip$' } |
                 Sort-Object Name -Descending | Select-Object -Skip $Keep)
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
    if (Test-Path -LiteralPath $stage)    { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $localZip) { Remove-Item -LiteralPath $localZip -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $rcLog)    { Remove-Item -LiteralPath $rcLog -Force -ErrorAction SilentlyContinue }
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
Write-Host "AppData snapshot complete"
Write-Host "============================================"
exit 0
