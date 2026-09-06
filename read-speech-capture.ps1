<#
.SYNOPSIS
  Read back what NVDA said and saw since the mark.

.DESCRIPTION
  The other half of start-speech-capture.ps1. Prints the utterances and input
  gestures from the marked point, in order, with the flags in the margin.

  THE PARSING AND THE RULES LIVE IN tools/speech-transcript/SpeechTranscript.psm1.
  This file used to carry its own copy of both, and both were wrong:

    - The extractor matched single-quoted payloads only. NVDA writes Python
      repr, which switches to DOUBLE quotes whenever the string contains an
      apostrophe - 337 of 1,545 utterances in a measured log, 22 percent. Every
      one of those was dropped or mangled, and a dropped utterance looks exactly
      like an utterance that was never spoken.

    - The repeat rule looked for identical text 400 to 1200 ms apart, measured
      from the previous utterance. Run against #554's own capture it finds
      NOTHING: those gaps are 4,083 ms and 1,284 ms. The 611 ms and 606 ms in
      the register are measured FROM THE INTERRUPT, which is what the salvage
      actually waits on. The rule now measures the same thing the salvage does.

  WHAT THE FLAGS MEAN

  !!  BURST - handed over within a few milliseconds of the previous utterance.
      The connect summary leaves as four lines in 4 ms and takes about 13
      seconds to say, so the operator hears the first fragment and nothing else.
      Emission is not delivery. See #521.

  ==  ECHO - the same text twice within 100 ms. Two genuine emissions, not a
      rescue. #550 measured 19 ms.

  @@  SALVAGE - the same text re-spoken inside #503's settle window after an
      interrupt. The rescue cannot know the first attempt was heard, so it can
      never stop. #554.

  ~~  REPEAT - said again within ten seconds with no interrupt to explain it.

  XX  CONTRADICTION - asserts a state that an utterance just before it denies.
      #521's signature is "Connected to ..." spoken between two disconnect
      announcements, and a diff of words alone cannot see it.

  For a NAMED scenario compared against an accepted run, use speech-scenario.ps1.

.PARAMETER Tail
  Show only the last N entries.

.PARAMETER GesturesOnly
  Input gestures only - what NVDA SAW pressed. Useful for key work, where we
  have otherwise only ever inferred what the app received.

.PARAMETER StationAlias
  Station or profile names to treat as one identity, so a rescue of the same
  sentence is recognised as the same sentence.

.EXAMPLE
  & "C:\dev\JJFlex-NG\read-speech-capture.ps1"
#>
[CmdletBinding()]
param(
    [int]      $Tail = 0,
    [switch]   $GesturesOnly,
    [string[]] $StationAlias = @()
)

$ErrorActionPreference = 'Stop'
$Log      = Join-Path $env:TEMP 'nvda.log'
$MarkFile = Join-Path $env:TEMP 'nvda-mark.txt'

$modulePath = Join-Path $PSScriptRoot 'tools\speech-transcript\SpeechTranscript.psm1'
if (-not (Test-Path $modulePath)) {
    Write-Host "the speech transcript module is missing at $modulePath"
    exit 1
}
Import-Module $modulePath -Force

if (-not (Test-Path $Log))      { Write-Host "no NVDA log at $Log"; exit 1 }
if (-not (Test-Path $MarkFile)) { Write-Host "no mark - run start-speech-capture.ps1 first"; exit 1 }

$mark = [int](Get-Content $MarkFile)

try {
    $events = Read-NvdaTranscript -Path $Log -FromByte $mark
} catch {
    Write-Host $_.Exception.Message
    exit 1
}

if ($GesturesOnly) { $events = @($events | Where-Object { $_.Kind -eq 'KEY' }) }
if ($Tail -gt 0 -and $events.Count -gt $Tail) { $events = @($events | Select-Object -Last $Tail) }

if ($events.Count -eq 0) {
    Write-Host "nothing captured since the mark."
    Write-Host "  That is not the same as 'nothing was said'. Check that NVDA is at"
    Write-Host "  Input/Output level and that the mark was set BEFORE the test:"
    Write-Host "      nvda -r -l 12"
    exit 0
}

$findings = Get-SpeechFlags -Events $events -StationAlias $StationAlias
$counts   = Get-FlagCounts -Findings $findings

Write-Host ("$($events.Count) events since the mark")
Write-Host ""
foreach ($line in (Format-SpeechTimeline -Events $events -Findings $findings)) {
    Write-Host $line
}

$t = Get-SpeechTranscriptThresholds
Write-Host ""
Write-Host "--- flags ---"
Write-Host ("  !!  burst          $($counts.BURST)".PadRight(28) + "within $($t.BurstMs) ms of the previous utterance (#521)")
Write-Host ("  ==  echo           $($counts.ECHO)".PadRight(28) + "same text within $($t.EchoMs) ms - two emissions (#550)")
Write-Host ("  @@  salvage        $($counts.SALVAGE)".PadRight(28) + "re-spoken $($t.SalvageMinMs)-$($t.SalvageMaxMs) ms after an interrupt (#503, #554)")
Write-Host ("  ~~  repeat         $($counts.REPEAT)".PadRight(28) + "same text again within $($t.RepeatWindowMs) ms, unexplained")
Write-Host ("  XX  contradiction  $($counts.CONTRADICTION)".PadRight(28) + "denies something just said (#521)")
Write-Host ""
Write-Host "NVDA records no cancellation at IO level, so an interrupt is INFERRED from an"
Write-Host "input gesture between the two emissions. With the operator's hands still, a real"
Write-Host "salvage loop shows up as repeat rather than salvage."
