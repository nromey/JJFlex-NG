# backup-memory-to-nas.ps1
# Snapshot Claude's memory folders to the NAS historical tree.
#
# Memory is Claude's evolving picture of a project across sessions
# (product vision, user preferences, decisions, tester relationships). It
# lives outside the repo and outside normal backup paths.
#
# SCOPE (changed 2026-08-01): this script used to back up ONLY the
# JJFlex-NG memory folder. Noel works across several projects that each
# carry their own Claude memory tree -- Freight Fate, Civ VI Access, and
# others under C:\Users\nrome\.claude\projects\. Those had NEVER been
# backed up. The C:\dev mirror does not cover them either, because the
# memory trees live under the user profile, not under C:\dev.
#
# Layout on the NAS:
#   historical\memory\memory-YYYYMMDD-HHMMSS.zip          <- JJFlex (legacy series, unbroken)
#   historical\memory\projects\<slug>\memory-YYYYMMDD-HHMMSS.zip  <- every other project
#
# JJFlex deliberately stays at the flat path so its existing dated series
# continues without a break. Everything else gets a per-project series.
#
# Runs manually any time, or as part of the end-of-day seal.

param(
    # Root containing all per-project Claude state directories.
    [string] $ProjectsRoot = 'C:\Users\nrome\.claude\projects',

    # The project whose memory keeps the legacy flat path.
    [string] $PrimarySlug  = 'C--dev-jjflex-ng',

    [string] $NasRoot      = '\\nas.macaw-jazz.ts.net\jjflex',

    # Back up only the primary project (old behaviour).
    [switch] $PrimaryOnly
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "============================================"
Write-Host "Backup Claude memory to NAS"
Write-Host "  Projects : $ProjectsRoot"
Write-Host "  NAS      : $NasRoot"
Write-Host "  Scope    : $(if ($PrimaryOnly) { 'primary only' } else { 'all projects' })"
Write-Host "============================================"

if (-not (Test-Path $ProjectsRoot)) {
    Write-Error "Projects root not found: $ProjectsRoot"
    exit 2
}

$memoryDest = Join-Path (Join-Path $NasRoot 'historical') 'memory'
if (-not (Test-Path $memoryDest)) {
    try {
        New-Item -Path $memoryDest -ItemType Directory -Force | Out-Null
        Write-Host "Created NAS folder: $memoryDest"
    } catch {
        Write-Error "Could not create NAS folder $memoryDest. NAS offline? $_"
        exit 3
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$tempDir   = [System.IO.Path]::GetTempPath()

# Discover every project directory that actually holds memory content.
# Projects with no memory\ folder (session transcripts only) are skipped
# silently -- there is nothing to preserve.
$targets = @()
foreach ($proj in (Get-ChildItem $ProjectsRoot -Directory)) {
    $memPath = Join-Path $proj.FullName 'memory'
    if (-not (Test-Path $memPath)) { continue }

    $files = @(Get-ChildItem $memPath -File -Recurse -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { continue }

    $isPrimary = ($proj.Name -ieq $PrimarySlug)
    if ($PrimaryOnly -and -not $isPrimary) { continue }

    $targets += [pscustomobject]@{
        Slug      = $proj.Name
        MemPath   = $memPath
        FileCount = $files.Count
        IsPrimary = $isPrimary
        # Primary keeps the flat legacy path; everyone else is namespaced.
        DestDir   = if ($isPrimary) { $memoryDest }
                    else { Join-Path (Join-Path $memoryDest 'projects') $proj.Name }
    }
}

if ($targets.Count -eq 0) {
    Write-Error "No project memory folders found under $ProjectsRoot"
    exit 2
}

$ok     = 0
$failed = @()

foreach ($t in ($targets | Sort-Object -Property @{E={-not $_.IsPrimary}}, Slug)) {
    Write-Host ""
    Write-Host "-- $($t.Slug) ($($t.FileCount) files)$(if ($t.IsPrimary) { '  [primary]' })"

    try {
        if (-not (Test-Path $t.DestDir)) {
            New-Item -Path $t.DestDir -ItemType Directory -Force | Out-Null
        }

        $zipName = "memory-$timestamp.zip"
        $zipPath = Join-Path $t.DestDir $zipName

        # Build the zip locally, then copy to the NAS. Two reasons:
        #   1. Compress-Archive writing straight to a UNC path has had bugs.
        #   2. A network hiccup mid-compress would leave a partial zip on the NAS.
        # Temp name is namespaced by slug so concurrent projects cannot collide.
        $tempZip = Join-Path $tempDir "$($t.Slug)-$zipName"
        if (Test-Path $tempZip) { Remove-Item -LiteralPath $tempZip -Force }

        Compress-Archive -Path (Join-Path $t.MemPath '*') -DestinationPath $tempZip -Force
        Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
        Remove-Item -LiteralPath $tempZip -Force

        $size = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
        Write-Host "   saved: $zipPath  ($size KB)"
        $ok++
    } catch {
        Write-Warning "   FAILED for $($t.Slug): $_"
        $failed += $t.Slug
    }
}

Write-Host ""
Write-Host "============================================"
Write-Host "Memory snapshot complete: $ok of $($targets.Count) projects"
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')"
    Write-Host "============================================"
    exit 4
}
Write-Host "============================================"
exit 0
