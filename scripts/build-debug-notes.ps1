# Emit a debug NOTES file for build-debug.bat.
# Called as: build-debug-notes.ps1 -Version <X.Y.Z.Y> -GitSha <short>
#                                  -OutPath <abs path to NOTES file>
#                                  [-BodyPath <abs path to user-supplied body>]
#
# Isolated from the batch file so em-dashes, parens, and backticks don't need
# cmd-escaping every time. UTF-8 WITH BOM output (2026-04-20 fix): Windows
# Notepad and Explorer's file-preview pane default to Windows-1252 for files
# without a BOM, which renders em-dashes (—) and other Unicode as mojibake
# ("â€""). The BOM costs 3 bytes and makes every Windows text-reading tool
# treat the file as UTF-8 unambiguously. VS Code, screen readers, and modern
# editors already handle this correctly; the BOM helps legacy tools and
# preserves typography in the NOTES that testers actually open in Notepad.

param(
    [Parameter(Mandatory=$true)] [string] $Version,
    [Parameter(Mandatory=$true)] [string] $GitSha,
    [Parameter(Mandatory=$true)] [string] $OutPath,
    [string] $BodyPath = ""
)

$ErrorActionPreference = 'Stop'

$built = Get-Date -Format 'yyyy-MM-dd HH:mm'

$lines = @(
    'JJ Flexible Radio Access -- Debug Build',
    "Version: $Version (Debug x64)",
    "Built:   $built",
    "Commit:  $GitSha",
    ''
)

if ($BodyPath -and (Test-Path -LiteralPath $BodyPath)) {
    # Explicit -Encoding UTF8 so PowerShell 5.1 decodes the body correctly
    # even when the body file has no BOM (Claude Code's default Write is
    # UTF-8 without BOM). Without this, PS5.1 falls back to the system's
    # default codepage which mangles Unicode on the read side.
    $body = Get-Content -Raw -LiteralPath $BodyPath -Encoding UTF8
    $text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine + $body
} else {
    $recent = @('Recent commits:') + (git log --oneline -n 10 HEAD)
    $text = (($lines + $recent) -join [Environment]::NewLine) + [Environment]::NewLine
}

# UTF-8 WITH BOM — see header comment. $true = emit BOM.
$utf8WithBom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($OutPath, $text, $utf8WithBom)
Write-Host "  wrote $OutPath"
