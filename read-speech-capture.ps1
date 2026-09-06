<#
.SYNOPSIS
  Read back what NVDA said and saw since the mark.

.DESCRIPTION
  The other half of start-speech-capture.ps1. Prints the utterances and input
  gestures from the marked point, in order, with timestamps - and flags the two
  patterns that turned out to matter on 2026-09-05.

  WHAT IT FLAGS, AND WHY

  BURST: several utterances emitted within a few milliseconds. The connect
  summary went out as four lines in 4 ms and takes about 13 seconds to say, so
  the operator hears the first fragment and nothing else. That gap between
  emission and delivery is #521 in one line of evidence.

  REPEAT: the same text emitted again after a pause near 600 ms. That is #503's
  salvage settle window. It interrupts, waits, decides the utterance went
  unheard, and re-speaks it - and it can never stop, because nothing tells it
  the first attempt was delivered. Measured at 611 ms and 606 ms.

  Neither is visible by ear: the repeats sound like the same opening fragment
  over and over, which reads as ordinary chatter rather than as a fault.

.PARAMETER Tail
  Show only the last N entries.

.PARAMETER GesturesOnly
  Input gestures only - what NVDA SAW pressed. Useful for key work, where we
  have otherwise only ever inferred what the app received.

.EXAMPLE
  & "C:\dev\JJFlex-NG\read-speech-capture.ps1"
#>
[CmdletBinding()]
param(
    [int]    $Tail = 0,
    [switch] $GesturesOnly
)

$ErrorActionPreference = 'Stop'
$Log      = Join-Path $env:TEMP 'nvda.log'
$MarkFile = Join-Path $env:TEMP 'nvda-mark.txt'

if (-not (Test-Path $Log))      { Write-Host "no NVDA log at $Log"; exit 1 }
if (-not (Test-Path $MarkFile)) { Write-Host "no mark - run start-speech-capture.ps1 first"; exit 1 }

$mark = [int](Get-Content $MarkFile)
$size = (Get-Item $Log).Length
if ($size -lt $mark) {
    Write-Host "The log has ROTATED since the mark - NVDA restarted and the marked"
    Write-Host "session is now in nvda-old.log. Run archive-nvda-logs.ps1 to rescue it."
    exit 1
}

# NVDA holds the log open, so read through a sharing stream.
$fs = [IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite')
$fs.Seek($mark, 'Begin') | Out-Null
$sr = New-Object IO.StreamReader($fs)
$text = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
$lines = $text -split "`r?`n"

$events = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l -notmatch '\((\d{2}):(\d{2}):(\d{2})\.(\d{3})\)') { continue }
    $ms = ([int]$Matches[1])*3600000 + ([int]$Matches[2])*60000 + ([int]$Matches[3])*1000 + [int]$Matches[4]
    $ts = "$($Matches[1]):$($Matches[2]):$($Matches[3]).$($Matches[4])"

    if ($l -match 'Input:.*kb\([^)]*\):(\S+)') {
        [void]$events.Add([pscustomobject]@{ Ms=$ms; Ts=$ts; Kind='KEY'; Text=$Matches[1] })
    }
    elseif ($l -match 'speech\.speak') {
        $nxt = if ($i+1 -lt $lines.Count) { $lines[$i+1] } else { '' }
        $t = [regex]::Matches($nxt, "'((?:[^'\\]|\\.)*)'") | ForEach-Object { $_.Groups[1].Value }
        $said = (($t | Where-Object { $_ -notmatch '^en_US$|^en$' }) -join ' ').Trim()
        if ($said) { [void]$events.Add([pscustomobject]@{ Ms=$ms; Ts=$ts; Kind='SAID'; Text=$said }) }
    }
}

if ($GesturesOnly) { $events = @($events | Where-Object { $_.Kind -eq 'KEY' }) }
if ($Tail -gt 0 -and $events.Count -gt $Tail) { $events = @($events | Select-Object -Last $Tail) }

if ($events.Count -eq 0) { Write-Host "nothing captured since the mark"; exit 0 }

"$($events.Count) events since the mark"
""
$prev = $null
$recent = @{}
$bursts = 0; $repeats = 0
foreach ($e in $events) {
    $flag = '  '
    if ($e.Kind -eq 'SAID') {
        if ($prev -and $prev.Kind -eq 'SAID' -and ($e.Ms - $prev.Ms) -le 5) { $flag = '!!'; $bursts++ }
        $key = $e.Text.Substring(0, [math]::Min(48, $e.Text.Length))
        if ($recent.ContainsKey($key)) {
            $gap = $e.Ms - $recent[$key]
            if ($gap -ge 400 -and $gap -le 1200) { $flag = '@@'; $repeats++ }
        }
        $recent[$key] = $e.Ms
    }
    $shown = if ($e.Text.Length -gt 96) { $e.Text.Substring(0,96) + '...' } else { $e.Text }
    "{0} {1}  {2,-4} {3}" -f $flag, $e.Ts, $e.Kind, $shown
    $prev = $e
}
""
"--- flags ---"
"  !!  emitted within 5 ms of the previous utterance  (burst: $bursts)"
"      Emission is not delivery. A four-line block goes out in 4 ms and takes"
"      13 seconds to say, so only the first fragment is ever heard. See #521."
"  @@  same text re-emitted after 400-1200 ms  (repeat: $repeats)"
"      #503's salvage settle window is 600 ms. Measured at 611 and 606. It"
"      rescues what it thinks went unheard and can never learn otherwise."
