<#
.SYNOPSIS
    Generate JJFlex-privateplanningctive	ask-register.md from the Claude task store.

.DESCRIPTION
    The task store is one JSON file per task under
    ~/.claude/tasks/<session-id>/<id>.json. It lives in the user profile, is not
    in git, and is invisible to every other Claude window, worktree and machine.
    This script mirrors it into the repo so the backlog survives a dead terminal
    and can be read by a session that did not create it.

    WHY THIS IS A SCRIPT AND NOT A DOCUMENT YOU EDIT.

    A hand-maintained mirror already existed. research-queue.md carried an
    "OPEN WORK REGISTER - mirrored from the task store 2026-08-14" section,
    created for exactly this reason after the task server disconnected
    mid-session, and it carried the instruction "Keep this current."

    Nine days later it said 72 tasks and 34 open while the store held 197 files
    and 77 open - missing about two thirds of the backlog. Nothing flagged it,
    because a mirror that drifts looks identical to a mirror that is correct.

    So the generated file is authoritative and hand edits are discarded. Fix the
    task, not the register.

.PARAMETER Check
    Do not write. Compare what WOULD be generated against the file on disk and
    exit 1 if they differ. This is the point of the whole exercise: the seal can
    VERIFY the mirror rather than trusting that somebody remembered. Exit 0 means
    the register genuinely matches the store.

.PARAMETER SessionId
    Which task-store session to mirror. Defaults to the most recently modified
    directory, and the choice is always REPORTED rather than assumed - silently
    mirroring the wrong session would produce a confident, wrong register.

.EXAMPLE
    & "C:\dev\JJFlex-NG\export-task-register.ps1"
    & "C:\dev\JJFlex-NG\export-task-register.ps1" -Check
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$SessionId,
    [string]$RepoRoot = $PSScriptRoot,
    [string]$TasksRoot = (Join-Path $env:USERPROFILE '.claude\tasks')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $RepoRoot 'JJFlexRadio.sln'))) {
    Write-Error "RepoRoot '$RepoRoot' does not contain JJFlexRadio.sln"
    exit 2
}
if (-not (Test-Path $TasksRoot)) {
    Write-Error "Task store not found at $TasksRoot"
    exit 2
}

# Pick the session. Report it either way - a wrong session silently mirrored is
# worse than no register at all.
if ($SessionId) {
    $sessionDir = Join-Path $TasksRoot $SessionId
    if (-not (Test-Path $sessionDir)) { Write-Error "No such session: $SessionId"; exit 2 }
} else {
    $candidate = Get-ChildItem -Path $TasksRoot -Directory |
        Where-Object { (Get-ChildItem $_.FullName -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0 } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $candidate) { Write-Error "No task-store session contains any tasks"; exit 2 }
    $sessionDir = $candidate.FullName
    $SessionId = $candidate.Name
}

$files = Get-ChildItem -Path $sessionDir -Filter '*.json'
Write-Host "Task store : $sessionDir"
Write-Host "Session    : $SessionId"
Write-Host "Task files : $($files.Count)"

# BROKEN INSTRUMENT check, same contract as radiocheck: an empty read must never
# be reported as an empty backlog. Overwriting a good register with nothing is
# exactly the silent failure this file exists to prevent.
if ($files.Count -eq 0) {
    Write-Error "BROKEN INSTRUMENT: session $SessionId contains zero task files. Refusing to write an empty register."
    exit 3
}

$tasks = foreach ($f in $files) {
    $t = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    [pscustomobject]@{
        Num         = [int]$t.id
        Id          = $t.id
        Subject     = $t.subject
        Description = $t.description
        Status      = $t.status
        Blocks      = @($t.blocks)
        BlockedBy   = @($t.blockedBy)
    }
}
$tasks = $tasks | Sort-Object Num

$open   = @($tasks | Where-Object { $_.Status -ne 'completed' })
$closed = @($tasks | Where-Object { $_.Status -eq 'completed' })

# Date only, never a time. Re-running on the same day must produce an identical
# file, or -Check reports drift on every run and stops meaning anything.
$stamp = (Get-Date).ToString('yyyy-MM-dd')

$sb = [System.Text.StringBuilder]::new()
function Add-Line { param([string]$s = '') [void]$sb.AppendLine($s) }

Add-Line '# Task register'
Add-Line ''
Add-Line '**GENERATED FILE - do not edit.** Produced by `export-task-register.ps1`'
Add-Line 'from the Claude task store. Hand edits are discarded on the next run.'
Add-Line 'To change something here, change the task.'
Add-Line ''
Add-Line "**Generated:** $stamp from session ``$SessionId``."
Add-Line "**Totals:** $($tasks.Count) tasks - $($open.Count) open, $($closed.Count) closed."
Add-Line ''
Add-Line 'Why this file exists: the task store lives under the user profile, is not'
Add-Line 'in git, and is invisible to every other window, worktree and machine. This'
Add-Line 'is the copy that survives a dead terminal.'
Add-Line ''
Add-Line 'A hand-maintained version of this already existed inside `research-queue.md`'
Add-Line 'and drifted from 34 open to 77 open in nine days without anything noticing.'
Add-Line 'Run `export-task-register.ps1 -Check` to prove this one has not.'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line "## Open ($($open.Count))"
Add-Line ''

foreach ($t in $open) {
    Add-Line "### #$($t.Id) - $($t.Subject)"
    Add-Line ''
    if ($t.BlockedBy.Count) { Add-Line "**Blocked by:** $(($t.BlockedBy | ForEach-Object { "#$_" }) -join ', ')"; Add-Line '' }
    if ($t.Blocks.Count)    { Add-Line "**Blocks:** $(($t.Blocks    | ForEach-Object { "#$_" }) -join ', ')"; Add-Line '' }
    if ($t.Description) {
        foreach ($line in ($t.Description -split "`r?`n")) { Add-Line $line }
        Add-Line ''
    }
}

Add-Line '---'
Add-Line ''
Add-Line "## Closed ($($closed.Count))"
Add-Line ''
Add-Line 'Subjects only. Full descriptions stay in the task store; these are here so'
Add-Line 'a number in a commit message or a plan can be resolved to what it meant.'
Add-Line ''
foreach ($t in $closed) { Add-Line "- **#$($t.Id)** - $($t.Subject)" }
Add-Line ''

$content  = $sb.ToString()
$outPath  = 'C:\Users\nrome\JJFlex-private\planning\active\task-register.md'

if ($Check) {
    if (-not (Test-Path $outPath)) {
        Write-Host "DRIFT: $outPath does not exist."
        exit 1
    }
    $onDisk = Get-Content $outPath -Raw -Encoding UTF8
    # Normalise line endings only; any real content difference is drift.
    if (($onDisk -replace "`r`n", "`n") -eq ($content -replace "`r`n", "`n")) {
        Write-Host "OK: register matches the task store ($($open.Count) open, $($closed.Count) closed)."
        exit 0
    }
    Write-Host "DRIFT: the register does not match the task store. Re-run without -Check."
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
Set-Content -Path $outPath -Value $content -Encoding UTF8 -NoNewline
Write-Host "Wrote $outPath"
Write-Host "  $($open.Count) open, $($closed.Count) closed"
