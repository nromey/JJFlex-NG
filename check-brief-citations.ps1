<#
.SYNOPSIS
  Print the real heading of every task number cited in an agent brief.

.DESCRIPTION
  A brief that cites a task number is making a claim about what that task says,
  and the agent receiving it cannot check. This prints the citation and the
  task's ACTUAL heading side by side so the mismatch is visible to a human
  before the brief is launched.

  It makes NO semantic judgement -- it cannot know whether #345 supports the
  sentence citing it. It only puts the two next to each other, which is enough:
  a sentence about arrow keys sitting beside "Delete the Slice Operations sound
  key" reads wrong instantly.

  Written 2026-09-20, after exactly that pair shipped in a design brief and cost
  most of an xhigh five-hour window. See CLAUDE.md, "write the prohibition":
  an agent can TEST a goal and cannot test a prohibition, so a prohibition must
  carry evidence someone opened.

.PARAMETER Path
  A brief to check. Defaults to every .md directly in for-codex\ -- that is,
  the briefs not yet moved to done\.

.EXAMPLE
  & "C:\dev\JJFlex-NG\check-brief-citations.ps1"
  & "C:\dev\JJFlex-NG\check-brief-citations.ps1" -Path .\my-brief.md
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [string]   $PlanningRoot = "C:\Users\nrome\JJFlex-private\planning"
)

$ErrorActionPreference = 'Stop'

$register = Join-Path $PlanningRoot 'active\tasks.md'
$archive  = Join-Path $PlanningRoot 'active\tasks-archive.md'

foreach ($f in @($register, $archive)) {
    if (-not (Test-Path $f)) { Write-Error "Register not found: $f"; exit 1 }
}

# Build number -> heading once. Both files use "### #NNN - title" with an em
# dash, and the status line "> #NNN . STATUS . ..." right underneath.
$titles  = @{}
$status  = @{}
foreach ($f in @($register, $archive)) {
    foreach ($line in [System.IO.File]::ReadLines($f)) {
        if ($line -match '^###\s+#(\d+)\s+.\s+(.*)$') {
            $titles[[int]$Matches[1]] = $Matches[2].Trim()
        }
        elseif ($line -match '^>\s+#(\d+)\s+.\s+([A-Z ]+)\s+.') {
            $status[[int]$Matches[1]] = $Matches[2].Trim()
        }
    }
}

if (-not $Path) {
    $Path = Get-ChildItem (Join-Path $PlanningRoot 'for-codex') -Filter *.md -File |
            Select-Object -ExpandProperty FullName
}
if (-not $Path) { "No briefs to check."; exit 0 }

$problems = 0

foreach ($brief in $Path) {
    if (-not (Test-Path $brief)) { Write-Warning "Not found: $brief"; continue }

    "`n=== $(Split-Path $brief -Leaf) ==="

    $seen = @{}
    $n    = 0
    foreach ($line in [System.IO.File]::ReadLines($brief)) {
        $n++
        foreach ($m in [regex]::Matches($line, '#(\d{1,4})\b')) {
            $num = [int]$m.Groups[1].Value

            # One report per number per brief. A brief that cites #517 six
            # times has one claim to check, not six.
            if ($seen.ContainsKey($num)) { continue }
            $seen[$num] = $true

            if (-not $titles.ContainsKey($num)) {
                "  #$num  -- NO SUCH TASK in either register file (line $n)"
                $problems++
                continue
            }

            $st = if ($status.ContainsKey($num)) { $status[$num] } else { '?' }
            $closed = if ($st -ne 'OPEN') { "  [$st]" } else { '' }

            "  #$num$closed"
            "      brief says : " + $line.Trim()
            "      task IS    : " + $titles[$num]
        }
    }
    if ($seen.Count -eq 0) { "  (no task numbers cited)" }
}

"`nRead each pair and ask whether the task supports the sentence. This script"
"cannot tell you that; it can only make the two visible at the same time."
if ($problems) { "`n$problems citation(s) name no task at all." }
exit 0
