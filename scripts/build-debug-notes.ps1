# Emit a debug NOTES file for build-debug.bat.
# Called as: build-debug-notes.ps1 -Version <X.Y.Z.Y> -GitSha <short>
#                                  -OutPath <abs path to NOTES file>
#                                  [-BodyPath <abs path to user-supplied body>]
#
# Isolated from the batch file so em-dashes, parens, and backticks don't need
# cmd-escaping every time. UTF-8-no-BOM output so screen readers and editors
# render consistently.

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
    $body = Get-Content -Raw -LiteralPath $BodyPath
    $text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine + $body
} else {
    $recent = @('Recent commits:') + (git log --oneline -n 10 HEAD)
    $text = (($lines + $recent) -join [Environment]::NewLine) + [Environment]::NewLine
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutPath, $text, $utf8NoBom)
Write-Host "  wrote $OutPath"
