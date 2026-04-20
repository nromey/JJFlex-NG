param(
    [Parameter(Mandatory=$true)] [string] $ProjectRoot,
    [Parameter(Mandatory=$true)] [string] $Version,
    [Parameter(Mandatory=$true)] [string[]] $Archs,
    [switch] $Release
)

$ErrorActionPreference = 'Stop'

$nas         = '\\nas.macaw-jazz.ts.net\jjflex'
$stable      = Join-Path $nas 'stable'
$archive     = Join-Path $nas 'stable\archive'
$historical  = Join-Path $nas 'historical'
# Timestamped installers now live under historical\<ver>\installers\ (no
# separate flat nightly\ folder — one history tree per version).

$mode = if ($Release) { 'RELEASE' } else { 'DAILY' }

Write-Host ""
Write-Host "============================================"
Write-Host "NAS publish ($mode): $Version"
Write-Host "Target: $nas"
Write-Host "============================================"

if (-not (Test-Path $nas)) {
    Write-Warning "NAS not reachable at $nas -- skipping publish."
    Write-Warning "(Check: Tailscale up? cached creds for this session?)"
    exit 0
}

foreach ($dir in @($stable, $archive, $historical)) {
    if (-not (Test-Path $dir)) {
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
    }
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmm'
$dateTag = Get-Date -Format 'yyyyMMdd'

foreach ($arch in $Archs) {
    $srcName     = "Setup JJFlex_${Version}_${arch}.exe"
    $srcPath     = Join-Path $ProjectRoot $srcName

    if (-not (Test-Path $srcPath)) {
        Write-Warning "[$arch] Installer not found at $srcPath -- skipping."
        continue
    }

    Write-Host ""
    Write-Host "[$arch] $srcName"

    # --- Historical: everything for this version lives under historical\<ver>\ ---
    #   <ver>\<arch>\        exe + pdb (same-version rebuilds overwrite)
    #   <ver>\installers\    timestamped installer (keeps every build)
    $histDir = Join-Path $historical "$Version\$arch"
    $instDir = Join-Path $historical "$Version\installers"
    foreach ($d in @($histDir, $instDir)) {
        if (-not (Test-Path $d)) { New-Item -Path $d -ItemType Directory -Force | Out-Null }
    }

    $installerName = "Setup JJFlex_${Version}_${stamp}_${arch}.exe"
    Copy-Item -Path $srcPath -Destination (Join-Path $instDir $installerName) -Force
    Write-Host "  installer : $installerName -> $Version\installers\"

    $binDir = Join-Path $ProjectRoot "bin\$arch\Release\net10.0-windows\win-$arch"
    $exe = Join-Path $binDir 'JJFlexRadio.exe'
    $pdb = Join-Path $binDir 'JJFlexRadio.pdb'

    if (Test-Path $exe) {
        Copy-Item -Path $exe -Destination $histDir -Force
        Write-Host "  historical: JJFlexRadio.exe -> $Version\$arch\"
    } else {
        Write-Warning "  [$arch] exe not found at $exe"
    }
    if (Test-Path $pdb) {
        Copy-Item -Path $pdb -Destination $histDir -Force
        Write-Host "  historical: JJFlexRadio.pdb -> $Version\$arch\"
    } else {
        Write-Warning "  [$arch] pdb not found (crashes for this build will not symbolicate)"
    }

    # --- Release-only: promote to stable\, archive prior non-matching stables ---
    if ($Release) {
        $olderInArch = Get-ChildItem -Path $stable -Filter "Setup JJFlex_*_${arch}.exe" -File -ErrorAction SilentlyContinue |
                       Where-Object { $_.Name -ne $srcName }
        foreach ($old in $olderInArch) {
            $dest = Join-Path $archive $old.Name
            Move-Item -Path $old.FullName -Destination $dest -Force
            Write-Host "  archived  : $($old.Name)"
        }
        $stableDest = Join-Path $stable $srcName
        Copy-Item -Path $srcPath -Destination $stableDest -Force
        Write-Host "  stable    : $srcName"
    }
}

Write-Host ""
if ($Release) {
    Write-Host "Publish complete (RELEASE). historical\$Version\ and stable\ updated."
    Write-Host "Dropbox stable promotion is a separate step; run the dropbox publish"
    Write-Host "command at end-of-day (or explicitly for a release cut)."
} else {
    Write-Host "Publish complete (DAILY). historical\$Version\ updated; Dropbox NOT touched."
    Write-Host "At end of day, say 'done developing' and I'll promote the latest"
    Write-Host "daily to Dropbox (one notification, replaces prior daily)."
}
Write-Host "============================================"
