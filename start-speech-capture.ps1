<#
.SYNOPSIS
  Restart NVDA into speech-logging mode and mark the log, ready for a test.

.DESCRIPTION
  Seal step 3e makes a speech transcript standing practice for any session that
  presses keys. This is the one command that sets it up, because getting there
  by hand cost four exchanges on 2026-09-05 and two of the mistakes were silent.

  WHAT IT DOES

    1. Restarts NVDA at Input/Output level.
    2. Waits for it to come back and PROVES it is capturing, by reading real
       utterances back. A capture taken at the wrong level looks identical to a
       working one until you go looking for speech that is not there.
    3. Records the byte offset, so the test can be read without the setup noise.

  WHY -r AND WHY 12

    -r (--replace-running) is load-bearing. `-l` alone does NOT change a running
    instance: NVDA carries on at the old level, with no error and no clue. Both
    mistakes were made on 2026-09-05; the log kept the same pid and start time
    and nothing said otherwise.

    Level 12 is Input/Output. The ladder is DEBUG 10, IO 12, DEBUGWARNING 15,
    INFO 20, WARNING 30, ERROR 40. Speech, braille and input gestures are all
    logged at IO. DEBUG (10) is a strict superset and buries them under NVDA's
    internals - more data, less signal.

  WHAT THE CAPTURE IS GOOD FOR

    Speech, including what NVDA says on its OWN account - window titles, focus
    changes, title rewrites - which our trace structurally cannot see because we
    never said them. Braille output too, when a display is attached. And every
    input gesture, which is what NVDA SAW pressed rather than what we infer the
    app received.

  Pair with archive-nvda-logs.ps1, which preserves sessions before NVDA's
  two-log rotation loses them.

.PARAMETER Level
  NVDA log level. 12 (Input/Output) by default. 10 is full DEBUG.

.PARAMETER SkipRestart
  Only mark the log. Use when NVDA is already capturing and a second test is
  starting - restarting would throw away the session so far.

.EXAMPLE
  & "C:\dev\JJFlex-NG\start-speech-capture.ps1"

.EXAMPLE
  & "C:\dev\JJFlex-NG\start-speech-capture.ps1" -SkipRestart
  Mark the log between two tests in one session.
#>
[CmdletBinding()]
param(
    [int]    $Level = 12,
    [switch] $SkipRestart
)

$ErrorActionPreference = 'Stop'
$Log      = Join-Path $env:TEMP 'nvda.log'
$MarkFile = Join-Path $env:TEMP 'nvda-mark.txt'

function Get-SpeechCount([string] $path) {
    if (-not (Test-Path $path)) { return -1 }
    try { return (Select-String -Path $path -Pattern 'Speaking' -SimpleMatch | Measure-Object).Count }
    catch { return -1 }
}

if (-not $SkipRestart) {
    # Archive first. The restart rotates the logs, and a session two restarts
    # back is simply gone - which is the whole reason archive-nvda-logs exists.
    $archiver = Join-Path $PSScriptRoot 'archive-nvda-logs.ps1'
    if (Test-Path $archiver) {
        Write-Host "Preserving the outgoing session first..."
        & $archiver | ForEach-Object { "  $_" }
    }

    $nvda = @(
        "$env:ProgramFiles\NVDA\nvda.exe",
        "${env:ProgramFiles(x86)}\NVDA\nvda.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $nvda) { $nvda = 'nvda' }   # fall back to PATH

    Write-Host "Restarting NVDA at level $Level (Input/Output)..."
    Write-Host "  You will hear a moment of silence while it comes back."
    Start-Process -FilePath $nvda -ArgumentList '-r', '-l', "$Level" | Out-Null

    # Wait for the new session, identified by the log shrinking or its header
    # changing. Ten seconds is generous; NVDA logged 5.7 s of slow start once.
    $before = if (Test-Path $Log) { (Get-Item $Log).Length } else { 0 }
    $deadline = (Get-Date).AddSeconds(20)
    $restarted = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (-not (Test-Path $Log)) { continue }
        if ((Get-Item $Log).Length -lt $before -or $before -eq 0) { $restarted = $true; break }
        $before = (Get-Item $Log).Length
    }
    Start-Sleep -Seconds 3   # let it finish starting and say something
}

# ---- the positive control -------------------------------------------------
# A level that did not take is invisible until you look for speech that is not
# there, so look now rather than after a test has been spent on it.
$count = Get-SpeechCount $Log
Write-Host ""
if ($count -lt 1) {
    Write-Host "FAILED: no utterances in the log."
    Write-Host "  NVDA is not capturing speech. Do NOT run a test yet."
    Write-Host "  Check that -r took: if the log still shows the old pid and start"
    Write-Host "  time, the running instance was never replaced."
    exit 1
}

Write-Host "Capturing: $count utterances in the log so far."
Write-Host "  Most recent, as a control - these should be things just heard:"

# Extract through the shared module rather than a local regex. The local one
# matched single-quoted payloads only, and NVDA writes Python repr - which
# switches to DOUBLE quotes whenever the string contains an apostrophe. On a
# measured log that was 337 of 1,545 utterances. A positive control that cannot
# render the last thing spoken is not a control.
$modulePath = Join-Path $PSScriptRoot 'tools\speech-transcript\SpeechTranscript.psm1'
if (Test-Path $modulePath) {
    Import-Module $modulePath -Force
    Select-String -Path $Log -Pattern 'Speaking' -SimpleMatch |
        Select-Object -Last 2 |
        ForEach-Object {
            $said = Get-SpokenTextFromPayload $_.Line
            if ($said) { "    " + $said.Substring(0, [math]::Min(90, $said.Length)) }
        }
} else {
    Write-Host "    (the speech transcript module is missing, so the last lines are not shown)"
}

$mark = (Get-Item $Log).Length
Set-Content $MarkFile $mark
Write-Host ""
Write-Host "Marked at byte $mark. Everything after this is the test."
Write-Host "Read it back with read-speech-capture.ps1, or archive with archive-nvda-logs.ps1."
