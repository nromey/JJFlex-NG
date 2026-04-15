# publish-daily-to-dropbox.ps1
# End-of-day promotion: take the most recent NAS nightly for each arch and
# copy it to Dropbox top level with a -daily suffix. Deletes any prior
# Dropbox daily first so only one daily exists per arch at any time.
#
# Triggered manually (or by Claude when you say "done developing"). Do NOT
# run this automatically per build -- each copy into the Dropbox JJFlexRadio
# folder fires a notification to every shared user.

param(
    [string] $DropboxRoot = 'C:\Users\nrome\Dropbox\JJFlexRadio',
    [string] $NasRoot     = '\\nas.macaw-jazz.ts.net\jjflex',
    [string[]] $Archs     = @('x64','x86')
)

$ErrorActionPreference = 'Stop'

$historical = Join-Path $NasRoot 'historical'

Write-Host ""
Write-Host "============================================"
Write-Host "Promoting latest NAS installer to Dropbox"
Write-Host "  NAS historical : $historical"
Write-Host "  Dropbox        : $DropboxRoot"
Write-Host "============================================"

if (-not (Test-Path $historical)) {
    Write-Error "NAS historical folder not reachable: $historical"
    exit 2
}
if (-not (Test-Path $DropboxRoot)) {
    Write-Error "Dropbox folder not found: $DropboxRoot"
    exit 3
}

$today = (Get-Date).Date
$anyCopied = $false

foreach ($arch in $Archs) {
    # Installers now live under historical\<ver>\installers\. Scan all versions
    # and pick the most recently written installer for this arch — the filename
    # timestamp (_YYYYMMDD_HHMM_) is monotonic but LastWriteTime is what we
    # used before, so preserve that tiebreaker.
    $candidates = Get-ChildItem -Path $historical -Recurse -Filter "Setup JJFlex_*_${arch}.exe" -File -ErrorAction SilentlyContinue |
                  Where-Object { $_.Directory.Name -eq 'installers' } |
                  Sort-Object LastWriteTime -Descending
    if (-not $candidates) {
        Write-Warning "[$arch] No installers found under historical\*\installers\ -- nothing to promote."
        continue
    }

    $latest = $candidates[0]
    $ageDays = ($today - $latest.LastWriteTime.Date).TotalDays

    Write-Host ""
    Write-Host "[$arch] Latest installer: $($latest.Name)"
    Write-Host "        modified     : $($latest.LastWriteTime)"
    if ($ageDays -ge 1) {
        Write-Warning "        This build is $ageDays day(s) old -- no newer build happened today."
        Write-Warning "        Promoting it anyway, but confirm this is what you want."
    }

    # The nightly filename contains a _YYYYMMDD_HHMM_ timestamp segment.
    # Strip that to recover the base Setup JJFlex_<version>_<arch>.exe, then
    # append -daily before the extension for the Dropbox name.
    $bareName = $latest.Name -replace '_\d{8}_\d{4}_', '_'
    $dailyName = $bareName -replace "_${arch}\.exe$", "_${arch}-daily.exe"
    $dropboxDest = Join-Path $DropboxRoot $dailyName

    # Delete any existing daily for this arch (filename may differ if version changed).
    $existing = Get-ChildItem -Path $DropboxRoot -Filter "Setup JJFlex_*_${arch}-daily.exe" -File -ErrorAction SilentlyContinue
    foreach ($old in $existing) {
        if ($old.Name -ne $dailyName) {
            Remove-Item -Path $old.FullName -Force
            Write-Host "        removed prior: $($old.Name)"
        }
    }

    Copy-Item -Path $latest.FullName -Destination $dropboxDest -Force
    Write-Host "        copied to    : $dailyName"
    $anyCopied = $true
}

Write-Host ""
if ($anyCopied) {
    Write-Host "Dropbox daily updated. One notification fires per file copied."
} else {
    Write-Host "Nothing was copied. No change to Dropbox."
}
Write-Host "============================================"
