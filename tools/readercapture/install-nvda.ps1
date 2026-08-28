<#
.SYNOPSIS
    Install the NVDA half of the reader capture instrument.

.DESCRIPTION
    Two routes, and the default is the gentler one.

    SCRATCHPAD (default). Copies the plugin into NVDA's developer scratchpad.
    Nothing is installed, nothing is registered, and removing it is deleting a
    folder. This is the right home for a debugging instrument that must never
    drift toward being a product.

    PACKAGE (-Package). Builds a .nvda-addon file for a tester who is not going
    to enable a scratchpad. Installing it is a normal add-on install and needs
    an NVDA restart.

    THIS SCRIPT NEVER TOUCHES A RUNNING SCREEN READER. It does not edit
    nvda.ini, it does not restart NVDA, and it does not reload plugins. It
    copies files and then tells you which key to press. The operator's reader is
    theirs.

.EXAMPLE
    .\install-nvda.ps1
    .\install-nvda.ps1 -Package
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Package,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'nvda'
$plugin = Join-Path $src 'globalPlugins\jjfcapture'

if (-not (Test-Path $plugin)) {
    Write-Host "Plugin source missing at $plugin."
    exit 2
}

if ($Package) {
    if (-not $OutputDir) { $OutputDir = $here }
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("jjfcapture-addon-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        Copy-Item (Join-Path $src 'manifest.ini') $staging
        Copy-Item (Join-Path $here 'README.md') (Join-Path $staging 'README.md') -ErrorAction SilentlyContinue
        $gp = Join-Path $staging 'globalPlugins'
        New-Item -ItemType Directory -Path $gp -Force | Out-Null
        Copy-Item $plugin $gp -Recurse
        Get-ChildItem $gp -Recurse -Directory -Filter '__pycache__' |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        $zip = Join-Path $OutputDir 'jjfcapture-0.1.0.zip'
        $addon = Join-Path $OutputDir 'jjfcapture-0.1.0.nvda-addon'
        if (Test-Path $zip) { Remove-Item $zip -Force }
        if (Test-Path $addon) { Remove-Item $addon -Force }
        if ($PSCmdlet.ShouldProcess($addon, 'package')) {
            Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
            Move-Item $zip $addon
            Write-Host "Built $addon"
            Write-Host ""
            Write-Host "Install it with NVDA menu, Tools, Add-on store, Install from"
            Write-Host "external source. NVDA restarts afterwards."
        }
    } finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

$scratch = Join-Path $env:APPDATA 'nvda\scratchpad'
if (-not (Test-Path $scratch)) {
    Write-Host "No NVDA scratchpad at $scratch. Is NVDA installed for this user?"
    exit 2
}
$dest = Join-Path $scratch 'globalPlugins\jjfcapture'

if ($PSCmdlet.ShouldProcess($dest, 'copy plugin')) {
    New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item $plugin $dest -Recurse
    Get-ChildItem $dest -Recurse -Directory -Filter '__pycache__' |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Copied the plugin to:"
    Write-Host "  $dest"
}

# Report the scratchpad switch. Do not change it: this is the operator's live
# reader, and a script that silently rewrites their configuration is exactly the
# class of accident this project has already paid for once.
$ini = Join-Path $env:APPDATA 'nvda\nvda.ini'
$enabled = $false
if (Test-Path $ini) {
    $text = Get-Content $ini -Raw
    if ($text -match '(?ms)^\[development\].*?enableScratchpadDir\s*=\s*(\w+)') {
        $enabled = ($Matches[1] -eq 'True')
    }
}

Write-Host ""
if ($enabled) {
    Write-Host "The developer scratchpad is ENABLED in this NVDA."
    Write-Host "Press NVDA+Control+F3 to reload plugins. NVDA does not restart."
} else {
    Write-Host "The developer scratchpad is currently DISABLED, so nothing will load"
    Write-Host "until you turn it on. This script will not turn it on for you: it is"
    Write-Host "your live reader's configuration."
    Write-Host ""
    Write-Host "  NVDA menu, Preferences, Settings, Advanced."
    Write-Host "  Tick the 'I understand that changing these settings' box."
    Write-Host "  Tick 'Enable loading custom code from Developer Scratchpad Directory'."
    Write-Host "  OK, then restart NVDA."
}

Write-Host ""
Write-Host "Keys, everywhere (rebindable under Input Gestures, category"
Write-Host "'JJ Flexible capture'):"
Write-Host "  NVDA+Shift+J          copy the capture to the clipboard"
Write-Host "  NVDA+Shift+K          plant a marker"
Write-Host "  NVDA+Control+Shift+J  pause or resume capturing"
Write-Host "  NVDA+Alt+J            run the positive control"
Write-Host ""
Write-Host "Run the positive control BEFORE trusting an empty capture. An"
Write-Host "instrument that records nothing looks exactly like a reader that"
Write-Host "received nothing."
Write-Host ""
Write-Host "Captures are written to %LOCALAPPDATA%\jjfcapture as JSON Lines."
Write-Host "To remove: delete $dest and reload plugins."
