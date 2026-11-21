<#
.SYNOPSIS
    Put the braille trial's NVDA app module into NVDA's developer scratchpad,
    or take it out again.

.DESCRIPTION
    Copies nvda\appModules\jjbrailletrial into
    %AppData%\nvda\scratchpad\appModules\jjbrailletrial, exactly as
    tools\readercapture\install-nvda.ps1 does its plugin. Nothing is installed
    and nothing is registered: the app module loads only while
    jjbrailletrial.exe is running, and removing it is deleting one folder
    (-Remove does that).

    THIS SCRIPT NEVER TOUCHES A RUNNING SCREEN READER. It does not edit
    nvda.ini, does not restart NVDA, and does not reload plugins. It copies or
    deletes files, reports whether the scratchpad is switched on, and tells you
    which key to press. The reader is yours.

.EXAMPLE
    .\install-nvda-trial.ps1
    .\install-nvda-trial.ps1 -Remove
    .\install-nvda-trial.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'nvda\appModules\jjbrailletrial'

$scratch = Join-Path $env:APPDATA 'nvda\scratchpad'
if (-not (Test-Path $scratch)) {
    Write-Host "No NVDA scratchpad at $scratch. Is NVDA installed for this user?"
    exit 2
}
$dest = Join-Path $scratch 'appModules\jjbrailletrial'

if ($Remove) {
    if (Test-Path $dest) {
        if ($PSCmdlet.ShouldProcess($dest, 'delete the trial app module')) {
            Remove-Item $dest -Recurse -Force
            Write-Host "Removed $dest"
        }
    } else {
        Write-Host "Nothing to remove: $dest does not exist."
    }
    Write-Host ""
    Write-Host "Press NVDA+Control+F3 to reload plugins, or restart NVDA, so the"
    Write-Host "removed app module is forgotten. This script does neither for you."
    exit 0
}

if (-not (Test-Path (Join-Path $source '__init__.py'))) {
    Write-Host "App module source missing at $source."
    exit 2
}

if ($PSCmdlet.ShouldProcess($dest, 'copy the trial app module')) {
    New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item $source $dest -Recurse
    Get-ChildItem $dest -Recurse -Directory -Filter '__pycache__' |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Copied the trial app module to:"
    Write-Host "  $dest"
}

# Report the scratchpad switch. Do not change it: it is the operator's live
# reader configuration.
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
    Write-Host "until you turn it on. This script will not turn it on for you."
    Write-Host ""
    Write-Host "  NVDA menu, Preferences, Settings, Advanced."
    Write-Host "  Tick the 'I understand that changing these settings' box."
    Write-Host "  Tick 'Enable loading custom code from Developer Scratchpad Directory'."
    Write-Host "  OK, then restart NVDA."
}
Write-Host ""
Write-Host "The app module only loads while jjbrailletrial.exe runs. Its lines in"
Write-Host "NVDA's log (NVDA menu, Tools, View log) start with 'jjbrailletrial:'."
Write-Host "To remove it: .\install-nvda-trial.ps1 -Remove"
