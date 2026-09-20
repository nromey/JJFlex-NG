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

# Two counters, because they are two different claims. Conflating them made the
# first run report "1 citation(s) name no task at all" when every citation was
# fine and the 1 was a phantom symbol -- a tool about misattribution,
# misattributing its own finding.
$problems = 0
$phantoms = 0

foreach ($brief in $Path) {
    if (-not (Test-Path $brief)) { Write-Warning "Not found: $brief"; continue }

    "`n=== $(Split-Path $brief -Leaf) ==="

    $seen = @{}
    $n    = 0
    foreach ($line in [System.IO.File]::ReadLines($brief)) {
        $n++
        # "BlindCat anti-pattern #1" is a house phrase with its own numbering,
        # not a register citation. Excluded by name rather than by magnitude --
        # low numbers ARE real tasks (#19 is cited constantly), so a "ignore
        # anything under ten" rule would hide the thing this script is for.
        $scan = $line -replace '(?i)anti-pattern\s+#\d+', 'anti-pattern'

        foreach ($m in [regex]::Matches($scan, '#(\d{1,4})\b')) {
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

    # --- backticked symbols -------------------------------------------------
    # The same brief that miscited #345 also told the agent to read AdjustVFO,
    # which had been DELETED. It read the tombstone and refused; a less careful
    # agent reconstructs an obsolete route to satisfy the instruction.
    #
    # This is deliberately DUMBER than check-memory-drift.ps1, which owns the
    # memory tree and the register and has a real tokenizer with an exemption
    # list. CLAUDE.md warns those two checkers must not grow into each other,
    # and briefs are a third corpus checked at a different moment -- before
    # launch, not at the seal. A brief is short, so a few false positives cost
    # one glance; importing the drift checker's machinery costs a second thing
    # to keep in step. If this ever needs the exemption list, move the CORPUS
    # to that script rather than copying its code here.
    $syms = @{}
    foreach ($line in [System.IO.File]::ReadLines($brief)) {
        foreach ($m in [regex]::Matches($line, '`([A-Za-z_][A-Za-z0-9_]*)`')) {
            $t = $m.Groups[1].Value
            # A bare lowercase word in backticks is nearly always prose or a
            # config key, not a symbol. Require an internal capital or an
            # underscore -- the distinctive half, same reasoning as the drift
            # checker's narrowness.
            if ($t -cmatch '^[a-z]+$') { continue }
            $syms[$t] = $true
        }
    }

    # IT DOES NOT CATCH THE CASE THAT MOTIVATED IT, and that is worth stating
    # rather than letting a green run imply coverage. `git grep AdjustVFO`
    # returns four files -- the tombstone comment that records the deletion,
    # plus stale references in Agent.md and globals.vb. The name survives its
    # own funeral, so a string search sees it and stays quiet. Codex caught it
    # by READING the tombstone and understanding what it said.
    #
    # So this finds a symbol that was never there or was fully erased. A symbol
    # deleted in code but still named in comments is invisible to it, and that
    # is exactly the shape a rename or a removal leaves behind. Treat a clean
    # result here as "no obvious phantoms", never as "the symbols are real".
    $missing = @()
    foreach ($t in $syms.Keys) {
        # -w whole word, -F literal, -l names only: fast enough per token that
        # a brief's dozen symbols cost well under a second in total.
        $null = & git -C $PSScriptRoot grep -lwF -- $t 2>$null
        if ($LASTEXITCODE -ne 0) { $missing += $t }
    }

    if ($missing.Count) {
        "  -- backticked symbols found NOWHERE in the repo:"
        foreach ($t in ($missing | Sort-Object)) { "       $t" }
        "     (candidates, not errors -- a brief may legitimately name a symbol"
        "      it is PROPOSING, or one in another repo. Check each.)"
        $phantoms += $missing.Count
    }
}

"`nRead each pair and ask whether the task supports the sentence. This script"
"cannot tell you that; it can only make the two visible at the same time."
if ($problems) { "`n$problems citation(s) name no task at all." }
if ($phantoms) { "$phantoms backticked symbol(s) appear nowhere in the repo." }
exit 0
