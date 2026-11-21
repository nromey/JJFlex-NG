<#
.SYNOPSIS
    Copy the braille trial's JAWS script into the JAWS USER settings folder and
    compile it there, or take it out again.

.DESCRIPTION
    Copies jaws\jjbrailletrial.jss, .jkm and .jbs into
    %AppData%\Freedom Scientific\JAWS\<version>\Settings\<language>\ and runs
    scompile.exe on jjbrailletrial.jss from inside that folder, the way
    tools\readercapture\install-jaws.ps1 does. scompile cannot compile a file
    outside a JAWS settings folder (established 2026-08-28, register #347), so
    the copy IS the compile check.

    The USER folder only. NOTHING HERE TOUCHES default.jss OR default.jsb, and
    nothing touches the shared Program Data tree. Every file is named for
    jjbrailletrial.exe, so JAWS loads it only while the trial program is in
    front and behaves exactly as before everywhere else. Removing it is
    deleting four files (-Remove does that).

    It does not start, stop or restart JAWS. JAWS picks up the compiled script
    the next time the trial program comes to the foreground.

    Several JAWS versions can be installed (this machine has 2026 and 2027).
    By default the newest user settings folder is used; -Version picks one.

.EXAMPLE
    .\compile-jaws-trial.ps1
    .\compile-jaws-trial.ps1 -Version 2026
    .\compile-jaws-trial.ps1 -Remove
    .\compile-jaws-trial.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Version,
    [string]$Language = 'enu',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'jaws'
$files = @('jjbrailletrial.jss', 'jjbrailletrial.jkm', 'jjbrailletrial.jbs')

$fsRoot = Join-Path $env:APPDATA 'Freedom Scientific\JAWS'
if (-not (Test-Path $fsRoot)) {
    Write-Host "No JAWS user settings tree at $fsRoot. Is JAWS installed for this user?"
    exit 2
}
$versions = Get-ChildItem $fsRoot -Directory | Where-Object { $_.Name -match '^\d+$' }
if ($Version) {
    $chosen = $versions | Where-Object { $_.Name -eq $Version } | Select-Object -First 1
} else {
    $chosen = $versions | Sort-Object { [int]$_.Name } -Descending | Select-Object -First 1
}
if (-not $chosen) {
    Write-Host "No JAWS user settings folder for version '$Version' under $fsRoot."
    Write-Host "Found: $(($versions | ForEach-Object Name) -join ', ')"
    exit 2
}

$dest = Join-Path $chosen.FullName "Settings\$Language"
if (-not (Test-Path $dest)) {
    Write-Host "No settings folder at $dest."
    exit 2
}

Write-Host "JAWS $($chosen.Name), user settings at:"
Write-Host "  $dest"
Write-Host ""

if ($Remove) {
    foreach ($name in $files + @('jjbrailletrial.jsb')) {
        $path = Join-Path $dest $name
        if (Test-Path $path) {
            if ($PSCmdlet.ShouldProcess($path, 'delete')) {
                Remove-Item $path -Force
                Write-Host "  deleted $name"
            }
        }
    }
    Write-Host ""
    Write-Host "Removed. Switch away from the trial program and back, or close it,"
    Write-Host "so JAWS stops using the script."
    exit 0
}

foreach ($name in $files) {
    $from = Join-Path $src $name
    if (-not (Test-Path $from)) {
        Write-Host "Missing $from."
        exit 2
    }
    $to = Join-Path $dest $name
    if (Test-Path $to) {
        Write-Host "  replacing existing $name"
    }
    if ($PSCmdlet.ShouldProcess($to, 'copy')) {
        Copy-Item $from $to -Force
        Write-Host "  wrote $name"
    }
}

$jawsProgram = Get-ChildItem 'C:\Program Files\Freedom Scientific\JAWS' -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq $chosen.Name } |
    Select-Object -First 1
$scompile = if ($jawsProgram) { Join-Path $jawsProgram.FullName 'scompile.exe' } else { $null }

if (-not $scompile -or -not (Test-Path $scompile)) {
    Write-Host ""
    Write-Host "scompile.exe not found for JAWS $($chosen.Name). Compile in JAWS instead:"
    Write-Host "Insert+0 opens Script Manager; File, Open, jjbrailletrial.jss; then File, Save."
    exit 0
}

if ($PSCmdlet.ShouldProcess('jjbrailletrial.jss', 'compile with scompile.exe')) {
    $jsb = Join-Path $dest 'jjbrailletrial.jsb'
    $before = if (Test-Path $jsb) { (Get-Item $jsb).LastWriteTime } else { $null }
    Push-Location $dest
    try {
        & $scompile 'jjbrailletrial.jss'
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    $after = if (Test-Path $jsb) { (Get-Item $jsb).LastWriteTime } else { $null }
    if ($after -and ($null -eq $before -or $after -gt $before)) {
        Write-Host ""
        Write-Host "Compiled. jjbrailletrial.jsb written at $after."
    } else {
        Write-Host ""
        Write-Host "scompile exited $code and wrote no new jjbrailletrial.jsb. The script"
        Write-Host "is NOT in use. Open it in Script Manager (Insert+0) to see the errors."
        exit 1
    }
}

Write-Host ""
Write-Host "JAWS loads it the next time jjbrailletrial.exe comes to the foreground."
Write-Host "Inside the trial program only:"
Write-Host "  Insert+Alt+Shift+B   status (press this first)"
Write-Host "  Insert+Alt+Shift+V   switch between the Home view and the field view"
Write-Host "Log: %LOCALAPPDATA%\jjbrailletrial\jaws-trial-<date>-<time>.log"
Write-Host "To remove: .\compile-jaws-trial.ps1 -Remove"
