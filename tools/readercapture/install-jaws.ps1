<#
.SYNOPSIS
    Install the JAWS half of the reader capture instrument.

.DESCRIPTION
    Copies jjflexible.jss and jjflexible.jkm into the JAWS USER settings folder
    and compiles the script.

    The user settings folder, not the shared one, on purpose: the shared tree is
    overwritten by JAWS updates and repairs, and this is a debugging instrument
    that should be trivially removable.

    -WhatIf shows exactly what would be written without writing it.

    NOTHING HERE TOUCHES THE DEFAULT SCRIPT FILE. Everything is scoped to
    jjflexible.exe, so JAWS behaves exactly as before in every other program,
    and uninstalling is deleting four files.

.NOTES
    SCompile.exe is documented in the FSDN Getting Started page: "use the
    application SCompile.exe, located in the JAWS program folder, to compile
    them from the command line. Call SCompile, passing in script source file
    names." It is run from inside the settings folder because, per the same
    page, "storing script files in the JAWS\Settings\(Language) folder ensures
    that all necessary include files are present at compile time."
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Language = 'enu',
    [switch]$SkipCompile
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'jaws'

$fsRoot = Join-Path $env:APPDATA 'Freedom Scientific\JAWS'
if (-not (Test-Path $fsRoot)) {
    Write-Host "No JAWS user settings tree at $fsRoot. Is JAWS installed for this user?"
    exit 2
}
$version = Get-ChildItem $fsRoot -Directory |
    Where-Object { $_.Name -match '^\d+$' } |
    Sort-Object { [int]$_.Name } -Descending |
    Select-Object -First 1
if (-not $version) {
    Write-Host "No versioned JAWS folder under $fsRoot."
    exit 2
}

$dest = Join-Path $version.FullName "Settings\$Language"
if (-not (Test-Path $dest)) {
    Write-Host "No settings folder at $dest."
    exit 2
}

Write-Host "JAWS $($version.Name), user settings at:"
Write-Host "  $dest"
Write-Host ""

foreach ($file in @('jjflexible.jss', 'jjflexible.jkm')) {
    $from = Join-Path $src $file
    $to = Join-Path $dest $file
    if (Test-Path $to) {
        Write-Host "REPLACING existing $file. If that file was yours and not ours,"
        Write-Host "stop now and move it aside first."
    }
    if ($PSCmdlet.ShouldProcess($to, 'copy')) {
        Copy-Item $from $to -Force
        Write-Host "  wrote $file"
    }
}

if ($SkipCompile) {
    Write-Host ""
    Write-Host "Skipping compile as asked. Compile it yourself with Insert+0 in JAWS"
    Write-Host "(Script Manager), File then Open, pick jjflexible.jss, then File then Save."
    exit 0
}

$jawsProgram = Get-ChildItem 'C:\Program Files\Freedom Scientific\JAWS' -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq $version.Name } |
    Select-Object -First 1
$scompile = if ($jawsProgram) { Join-Path $jawsProgram.FullName 'scompile.exe' } else { $null }

if (-not $scompile -or -not (Test-Path $scompile)) {
    Write-Host ""
    Write-Host "scompile.exe not found. Compile in JAWS instead: Insert+0 opens Script"
    Write-Host "Manager, File then Open, pick jjflexible.jss, then File then Save."
    exit 0
}

if ($PSCmdlet.ShouldProcess('jjflexible.jss', 'compile')) {
    Push-Location $dest
    try {
        & $scompile 'jjflexible.jss'
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    $jsb = Join-Path $dest 'jjflexible.jsb'
    if (Test-Path $jsb) {
        Write-Host ""
        Write-Host "Compiled. jjflexible.jsb written at $((Get-Item $jsb).LastWriteTime)."
    } else {
        Write-Host ""
        Write-Host "scompile exited $code and produced no jjflexible.jsb. The script did"
        Write-Host "NOT install. Open it in Script Manager (Insert+0) to see the error;"
        Write-Host "the command line compiler is terse about what it disliked."
        exit 1
    }
}

Write-Host ""
Write-Host "JAWS loads the new binary the next time jjflexible.exe comes to the"
Write-Host "foreground, so switch away from JJ Flexible and back."
Write-Host ""
Write-Host "Keys, inside JJ Flexible only:"
Write-Host "  Insert+Shift+J          copy the capture to the clipboard"
Write-Host "  Insert+Shift+K          plant a marker"
Write-Host "  Insert+Control+Shift+J  pause or resume capturing"
Write-Host "  Insert+Alt+J            run the positive control"
Write-Host ""
Write-Host "Run the positive control BEFORE trusting an empty capture. An"
Write-Host "instrument that records nothing looks exactly like a reader that"
Write-Host "received nothing."
Write-Host ""
Write-Host "To remove: delete jjflexible.jss, .jsb and .jkm from"
Write-Host "  $dest"
