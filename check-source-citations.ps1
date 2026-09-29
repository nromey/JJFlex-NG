<#
.SYNOPSIS
  Every #NNN cited in SOURCE, checked against the task register.

.DESCRIPTION
  check-brief-citations.ps1 reads BRIEFS. Nothing read SOURCE COMMENTS, and
  that is where the worst instance lived.

  Found by hand on 2026-09-23 (#616): four numbers cited in shipped code named
  nothing in either register file, and TWO OF THEM ATTRIBUTED A RULING TO
  NOEL, with a date and — in one case — a quotation he never said. One had sat
  in the speech path for over two weeks being treated as settled authority.

  A wrong number points at something real that fails to support the claim, and
  opening it reveals the mismatch. A FABRICATED number points at nothing and
  cannot be checked by opening anything. It carries a name, a plausible date,
  and words nobody said.

  This also catches the register defect the same sweep found: #570's heading
  read "### #569b" while its own status line read "> #570", so every tool
  keyed on headings could not see it and every tool keyed on status lines
  could. It had been invisible since 2026-09-07.

  CANDIDATES, NOT ERRORS — the same discipline as the other two checkers. A
  number can legitimately appear in source for a reason that is not a task
  citation. Judge, then fix the comment or the register.

.PARAMETER Root
  Repository root to scan. Defaults to this script's own directory.

.PARAMETER Register
  Folder holding tasks.md and tasks-archive.md.
#>
[CmdletBinding()]
param(
    [string] $Root = $PSScriptRoot,
    [string] $Register = 'C:\Users\nrome\JJFlex-private\planning\active'
)

$ErrorActionPreference = 'Stop'

$tasksFile   = Join-Path $Register 'tasks.md'
$archiveFile = Join-Path $Register 'tasks-archive.md'

foreach ($f in @($tasksFile, $archiveFile)) {
    if (-not (Test-Path $f)) { Write-Error "Register file not found: $f"; exit 1 }
}

# ---------------------------------------------------------------- the register
#
# Read BOTH shapes and compare them. A task is real if either its heading or
# its status line names it; a task whose two disagree is itself a finding, and
# is the reason this block does not just take one of them.

$headingNums = @{}
$statusNums  = @{}

foreach ($f in @($tasksFile, $archiveFile)) {
    foreach ($line in (Get-Content -LiteralPath $f)) {
        if ($line -match '^###\s+#(\d+)\b')  { $headingNums[[int]$Matches[1]] = $true }
        if ($line -match '^>\s*#(\d+)\s*·')  { $statusNums[[int]$Matches[1]]  = $true }
    }
}

$known = @{}
foreach ($n in $headingNums.Keys) { $known[$n] = $true }
foreach ($n in $statusNums.Keys)  { $known[$n] = $true }

# A status line with no heading, or a heading with no status line. Either way
# something that reads the register one way cannot see what the other sees.
$mismatched = @()
foreach ($n in ($known.Keys | Sort-Object)) {
    if (-not $headingNums.ContainsKey($n)) { $mismatched += "#$n has a status line but NO heading" }
    elseif (-not $statusNums.ContainsKey($n)) { $mismatched += "#$n has a heading but NO status line" }
}

# ------------------------------------------------------------------- the scan
#
# Source and tests, ours only. Vendor trees carry their own issue numbers and
# are not ours to police.

$scanDirs = @('Radios', 'Radios.Tests', 'JJFlexWpf', 'JJFlexWpf.Tests', 'JJTrace', 'JJLogLib') |
    ForEach-Object { Join-Path $Root $_ } | Where-Object { Test-Path $_ }

$scanFiles = @()
foreach ($d in $scanDirs) {
    $scanFiles += Get-ChildItem -LiteralPath $d -Recurse -File -Include *.cs, *.vb -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
}
foreach ($rootFile in @('globals.vb')) {
    $p = Join-Path $Root $rootFile
    if (Test-Path $p) { $scanFiles += Get-Item -LiteralPath $p }
}

# Numbers that look like citations but are not. Measured on the 2026-09-23
# sweep, which returned 358 distinct numbers of which most were these.
function Test-IsCitationCandidate {
    param([string] $Line, [int] $Num, [int] $Offset)

    # HTML/XML entities: &#8203; zero-width space, &#8212; em dash.
    if ($Offset -gt 0 -and $Line[$Offset - 1] -eq '&') { return $false }

    # CSS colours. "#1e1e1e" matches #1 followed by hex letters, and the Fixer
    # page and the help renderer are full of them.
    $after = $Offset + 1 + $Num.ToString().Length
    if ($after -lt $Line.Length -and $Line[$after] -match '[0-9a-fA-F]') { return $false }

    # BlindCat's anti-patterns are numbered in their own scheme, not ours, and
    # they are quoted constantly. The brief checker excludes them too.
    $before = $Line.Substring(0, $Offset)
    if ($before -match '(?i)anti-pattern\s*$') { return $false }

    # The register's own range. Numbers outside it are versions, ports,
    # sample rates, byte counts, vendor ticket numbers.
    if ($Num -lt 1 -or $Num -gt 2000) { return $false }

    return $true
}

$findings = @()
$total = 0

foreach ($file in $scanFiles) {
    $lineNo = 0
    foreach ($line in (Get-Content -LiteralPath $file.FullName)) {
        $lineNo++
        foreach ($m in [regex]::Matches($line, '#(\d+)')) {
            $num = [int] $m.Groups[1].Value
            if (-not (Test-IsCitationCandidate -Line $line -Num $num -Offset $m.Index)) { continue }
            $total++
            if ($known.ContainsKey($num)) { continue }

            $rel = $file.FullName.Substring($Root.Length).TrimStart('\')

            # An attributed ruling on a phantom number is the expensive case:
            # it cannot be checked by opening anything, and it carries a name.
            $attributed = $line -match '(?i)ruled by|Noel (ruled|said)|per Noel'

            $findings += [pscustomobject]@{
                Number     = $num
                Where      = "${rel}:${lineNo}"
                Attributed = $attributed
                Text       = $line.Trim()
            }
        }
    }
}

# ----------------------------------------------------------------- the report

Write-Output "Source citation check"
Write-Output "  scanned : $($scanFiles.Count) files under $Root"
Write-Output "  register: $tasksFile"
Write-Output "            $archiveFile"
Write-Output "  citations considered: $total"
Write-Output ""

if ($mismatched.Count -gt 0) {
    Write-Output "REGISTER DISAGREES WITH ITSELF - a tool reading one shape cannot see these:"
    foreach ($m in $mismatched) { Write-Output "  $m" }
    Write-Output ""
}

$attributedFindings = @($findings | Where-Object { $_.Attributed })
if ($attributedFindings.Count -gt 0) {
    Write-Output "*** A RULING IS ATTRIBUTED ON A NUMBER THAT NAMES NO TASK ***"
    Write-Output "    This is the case that cannot be checked by opening anything."
    foreach ($f in $attributedFindings) {
        Write-Output "  #$($f.Number)  $($f.Where)"
        Write-Output "      $($f.Text)"
    }
    Write-Output ""
}

$plain = @($findings | Where-Object { -not $_.Attributed })
if ($plain.Count -gt 0) {
    Write-Output "Cited in source, names no task in either register file:"
    foreach ($f in ($plain | Sort-Object Number, Where)) {
        Write-Output "  #$($f.Number)  $($f.Where)"
        Write-Output "      $($f.Text)"
    }
    Write-Output ""
}

if ($findings.Count -eq 0 -and $mismatched.Count -eq 0) {
    Write-Output "Nothing flagged."
    Write-Output ""
}

Write-Output "CANDIDATES, NOT ERRORS. A number can appear in source for reasons that"
Write-Output "are not a task citation, and the filters here are measured rather than"
Write-Output "proven. Judge each: fix the comment, or fix the register."
Write-Output ""
Write-Output "DO NOT CHASE THE COUNT TO ZERO. The number to watch is a NEW entry"
Write-Output "appearing - and an attributed ruling on a phantom number, every time."

exit 0
