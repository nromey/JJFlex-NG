<#
.SYNOPSIS
  Run a named speech scenario, or re-read one, and diff it against its baseline.

.DESCRIPTION
  The third script in the seal step 3e set, and the one that turns a capture
  into a REPEATABLE TEST rather than a thing somebody reads once.

  start-speech-capture.ps1 gets NVDA logging and marks the log.
  read-speech-capture.ps1 shows what happened after the mark.
  This runs a NAMED scenario, normalises what came back, and compares it to the
  accepted run - saying what is now said that was not, what is no longer said,
  and what changed order.

  ORDER IS REPORTED AS A FIRST-CLASS RESULT because #521 is entirely about
  things arriving in the wrong sequence. Every sentence in its captured failure
  is a sentence the app is allowed to say; only the ordering is wrong.

  ALL THE PARSING, NORMALISING, FLAGGING AND DIFFING LIVES IN
  tools/speech-transcript/SpeechTranscript.psm1. This file is a driver. The
  xUnit tests in Radios.Tests drive this same script, so the thing under test
  is the thing the operator runs.

  WHAT IT WILL NOT DO

  It never restarts NVDA. That takes the operator's screen reader away for
  several seconds and it is his call, not a script's. -Live marks the log where
  it stands and refuses to continue if NVDA is not already capturing speech.

  It never writes a raw log into the repository. -Live saves the raw slice to
  JJFlex-private, because a transcript holds everything NVDA spoke in every
  application and this repository is public.

.PARAMETER Scenario
  Which scenario. -List names them.

.PARAMETER Live
  Mark the log where it stands, wait while the operator performs the scenario,
  then read back from the mark.

.PARAMETER FromLog
  Analyse this file instead of the live log.

.PARAMETER FromByte
  Start at this offset. Defaults to the mark left by start-speech-capture.ps1
  when reading the live log.

.PARAMETER Record
  Write what came back as the scenario's baseline. Recording a baseline ASSERTS
  THAT A PERSON LISTENED TO THE RUN AND JUDGED IT CORRECT. Nothing else can
  establish that, so nothing else should record one.

.PARAMETER BaselinePath
  Use this baseline file instead of the scenario's own.

.PARAMETER StationAlias
  Station or profile names to normalise out. A name like "6300inshack" matches
  no pattern and has to be declared. They are replaced before anything else.

.PARAMETER Json
  Emit the artifact, flags and diff as JSON.

.PARAMETER List
  Name the scenarios and what each one asks the operator to do.

.EXAMPLE
  & "C:\dev\JJFlex-NG\speech-scenario.ps1" -List

.EXAMPLE
  & "C:\dev\JJFlex-NG\speech-scenario.ps1" -Scenario connect -Live

.EXAMPLE
  & "C:\dev\JJFlex-NG\speech-scenario.ps1" -Scenario connect -Live -Record
  Accept this run as the baseline. Only after listening to it.

.NOTES
  Exit codes, so this can gate something:
    0  matched the baseline, no new flags
    1  the harness could not run
    2  differed from the baseline, or picked up flags the baseline did not have
    3  there is no baseline for this scenario yet - UNKNOWN, never "passed"
#>
[CmdletBinding()]
param(
    [string]   $Scenario,
    [switch]   $Live,
    [string]   $FromLog,
    [int]      $FromByte = -1,
    [switch]   $Record,
    [string]   $BaselinePath,
    [string[]] $StationAlias = @(),
    [switch]   $Json,
    [switch]   $List
)

$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'tools\speech-transcript\SpeechTranscript.psm1'
if (-not (Test-Path $modulePath)) {
    Write-Host "the speech transcript module is missing at $modulePath"
    exit 1
}
Import-Module $modulePath -Force

if ($List) {
    Write-Host "Speech scenarios"
    Write-Host ""
    foreach ($s in (Get-SpeechScenario)) {
        $bp = Get-BaselinePath -Scenario $s.Name
        $has = 'no baseline recorded'
        if (Test-Path $bp) {
            $n = @(Get-Content $bp | Where-Object { $_ -notmatch '^\s*#' -and $_ -match '\S' }).Count
            $has = "baseline recorded, $n utterances"
        }
        Write-Host ("  " + $s.Name)
        Write-Host ("      do: " + $s.What)
        Write-Host ("     why: " + $s.Why)
        Write-Host ("  status: " + $has)
        Write-Host ""
    }
    exit 0
}

if (-not $Scenario) {
    Write-Host "name a scenario with -Scenario, or list them with -List"
    exit 1
}

$def = Get-SpeechScenario -Name $Scenario

# ---------------------------------------------------------------------------
# Get the events
# ---------------------------------------------------------------------------

$liveLog  = Join-Path $env:TEMP 'nvda.log'
$markFile = Join-Path $env:TEMP 'nvda-mark.txt'
$rawSlicePath = $null

if ($FromLog) {
    if ($FromByte -lt 0) { $FromByte = 0 }
    # A read that fails must stop here. Letting it fall through produced a
    # report of nought utterances and no flags, which is indistinguishable from
    # a clean run - and a harness that reports success when it read nothing is
    # worse than no harness.
    try {
        $events = Read-NvdaTranscript -Path $FromLog -FromByte $FromByte
    } catch {
        Write-Host ("could not read " + $FromLog + ": " + $_.Exception.Message)
        exit 1
    }
}
elseif ($Live) {
    if (-not (Test-Path $liveLog)) {
        Write-Host "no NVDA log at $liveLog. Run start-speech-capture.ps1 first."
        exit 1
    }

    # Positive control before the run, not after it. A capture taken at the
    # wrong level looks exactly like a working one until you go looking for
    # speech that is not there - and by then the test has been spent.
    $already = (Select-String -Path $liveLog -Pattern 'Speaking' -SimpleMatch | Measure-Object).Count
    if ($already -lt 1) {
        Write-Host "NVDA is not logging speech - there is not one utterance in the log."
        Write-Host "  Do NOT run the scenario yet: it would capture nothing and read as a"
        Write-Host "  clean run. Restart NVDA at Input/Output level first, which is the"
        Write-Host "  operator's call, not this script's:"
        Write-Host "      nvda -r -l 12"
        Write-Host "  The -r is load-bearing; -l alone leaves the running instance untouched."
        exit 1
    }

    $mark = (Get-Item $liveLog).Length
    Set-Content $markFile $mark

    Write-Host ("Scenario: " + $def.Name)
    Write-Host ("Do this:  " + $def.What)
    Write-Host ""
    Write-Host ("Marked at byte $mark, with $already utterances already logged.")
    Write-Host "Perform the scenario now, then come back here and press Enter."
    [void](Read-Host "Press Enter when the scenario is finished")

    $events = Read-NvdaTranscript -Path $liveLog -FromByte $mark

    # The raw slice goes to JJFlex-private. It can carry anything NVDA said in
    # any application while the scenario ran, so it never goes near the repo.
    $privateDir = 'C:\Users\nrome\JJFlex-private\nvda-logs\scenarios'
    try {
        if (-not (Test-Path $privateDir)) { New-Item -ItemType Directory -Path $privateDir -Force | Out-Null }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $rawSlicePath = Join-Path $privateDir ($def.Name + '-' + $stamp + '.log')
        $fs = [IO.File]::Open($liveLog, 'Open', 'Read', 'ReadWrite')
        try {
            [void]$fs.Seek($mark, 'Begin')
            $sr = New-Object IO.StreamReader($fs)
            try { Set-Content -Path $rawSlicePath -Value $sr.ReadToEnd() -Encoding UTF8 } finally { $sr.Close() }
        } finally { $fs.Dispose() }
    } catch {
        Write-Host ("  could not save the raw slice: " + $_.Exception.Message)
        $rawSlicePath = $null
    }
}
else {
    if (-not (Test-Path $liveLog)) {
        Write-Host "no NVDA log at $liveLog. Use -FromLog to analyse a saved capture."
        exit 1
    }
    if ($FromByte -lt 0) {
        if (Test-Path $markFile) { $FromByte = [int](Get-Content $markFile) } else { $FromByte = 0 }
    }
    $events = Read-NvdaTranscript -Path $liveLog -FromByte $FromByte
}

$artifact = New-ScenarioArtifact -Scenario $def.Name -Events $events -StationAlias $StationAlias

# ---------------------------------------------------------------------------
# Record, or compare
# ---------------------------------------------------------------------------

if ($Record) {
    if ($artifact.Utterances.Count -eq 0) {
        Write-Host "refusing to record an empty baseline."
        Write-Host "  An empty capture is a dead instrument, never 'the app correctly said"
        Write-Host "  nothing'. Check that NVDA is at Input/Output level and that the mark"
        Write-Host "  was set before the scenario, not after it."
        exit 1
    }
    $path = Export-ScenarioBaseline -Artifact $artifact -Path $BaselinePath
    Write-Host ("Recorded " + $artifact.Utterances.Count + " utterances as the baseline for '" + $def.Name + "'.")
    Write-Host ("  " + $path)
    Write-Host "  This asserts that a person listened to this run and judged it correct."
    exit 0
}

$baseline = Import-ScenarioBaseline -Scenario $def.Name -Path $BaselinePath
$diff = $null
if ($baseline) { $diff = Compare-ScenarioArtifact -Baseline $baseline -Artifact $artifact }

if ($Json) {
    $payload = [pscustomobject]@{
        Scenario     = $def.Name
        HasBaseline  = ($baseline -ne $null)
        Thresholds   = (Get-SpeechTranscriptThresholds)
        Artifact     = $artifact
        Diff         = $diff
        RawSlicePath = $rawSlicePath
    }
    $payload | ConvertTo-Json -Depth 8
}
else {
    $fc = $artifact.FlagCounts
    Write-Host ("Scenario: " + $def.Name)
    Write-Host ("  " + $artifact.Utterances.Count + " utterances, " +
                $artifact.GestureCount + " input gestures, " +
                $artifact.TypedCount + " typed events (content never recorded)")
    Write-Host ("  flags: burst " + $fc.BURST + ", echo " + $fc.ECHO +
                ", salvage " + $fc.SALVAGE + ", repeat " + $fc.REPEAT +
                ", contradiction " + $fc.CONTRADICTION)
    if ($rawSlicePath) { Write-Host ("  raw slice saved to " + $rawSlicePath) }
    Write-Host ""

    Write-Host "--- what happened, in order ---"
    foreach ($line in (Format-SpeechTimeline -Events $events -Findings $artifact.Flags)) {
        Write-Host $line
    }
    Write-Host ""

    if ($artifact.Volatile.Count -gt 0) {
        Write-Host "--- values normalised out of the diff, shown here so they stay visible ---"
        Write-Host "  A count that is wrong lives entirely in its number, so removing numbers"
        Write-Host "  from the diff would hide it. This is where #555's 'listed / online' pair"
        Write-Host "  shows itself."
        foreach ($v in $artifact.Volatile) {
            Write-Host ("  " + $v.Normalized)
            foreach ($seen in $v.Seen) { Write-Host ("      seen as: " + $seen) }
        }
        Write-Host ""
    }

    if (-not $baseline) {
        Write-Host "--- no baseline ---"
        Write-Host ("  There is no accepted run for '" + $def.Name + "' yet, so this run is")
        Write-Host "  UNKNOWN, not correct. Listen to it, and if it is right, record it:"
        Write-Host ("      speech-scenario.ps1 -Scenario " + $def.Name + " -Live -Record")
    }
    else {
        Write-Host "--- against the baseline ---"
        Write-Host ("  " + $diff.BaselineCount + " utterances accepted, " + $diff.CurrentCount + " this run")

        if ($diff.Added.Count -gt 0) {
            Write-Host ""
            Write-Host "  NOW SAID, and was not before:"
            foreach ($a in $diff.Added) { Write-Host ("    at position " + $a.Position + ": " + $a.Text) }
        }
        if ($diff.Removed.Count -gt 0) {
            Write-Host ""
            Write-Host "  NO LONGER SAID:"
            foreach ($r in $diff.Removed) { Write-Host ("    was at position " + $r.Position + ": " + $r.Text) }
        }
        if ($diff.Moved.Count -gt 0) {
            Write-Host ""
            Write-Host "  CHANGED ORDER:"
            foreach ($m in $diff.Moved) { Write-Host ("    now at position " + $m.Position + ": " + $m.Text) }
        }
        if ($diff.FlagRegression.Count -gt 0) {
            Write-Host ""
            Write-Host "  FLAGS GOT WORSE, even where the words did not change:"
            foreach ($w in $diff.FlagRegression) {
                Write-Host ("    " + $w.Flag + ": was " + $w.Was + ", now " + $w.Now)
            }
        }
        if ($diff.Matches) { Write-Host "  matches the baseline." }
    }
}

if (-not $baseline) { exit 3 }
if ($diff.Matches)  { exit 0 }
exit 2
