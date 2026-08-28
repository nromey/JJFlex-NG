<#
.SYNOPSIS
    The strong positive control for the JAWS half: send a known token through
    the exact doors JJ Flexible uses, then look for it in the capture.

.DESCRIPTION
    The in-script control (Insert+Alt+J) proves the capture is recording. It
    does NOT prove the door JJ Flexible actually comes through is open, because
    it emits from inside JAWS.

    This script emits from outside, through the same two entry points the
    application uses. Both were read out of Prism's own source rather than
    assumed:

      speech   IJawsApi::SayString(text, flush)        source/backends/jaws.cpp
      braille  IJawsApi::RunFunction('BrailleString("...")')   same file

    So a token that arrives here and appears in the capture proves the whole
    path. A token that does not appear tells you WHICH half is broken, which is
    the question that cost a whole session of listening on 2026-08-27.

    FOCUS IS LOAD-BEARING, NOT COSMETIC. JAWS resolves a function name in the
    application script file only while that application has focus. If PowerShell
    still has focus when the braille token is sent, the built-in BrailleString
    runs, the capture's override never sees it, and you will read that as a
    braille fault that is really a focus artefact. Hence the delay: start the
    script, then move to JJ Flexible and stay there.

.EXAMPLE
    .\jaws-ingress-control.ps1
    .\jaws-ingress-control.ps1 -DelaySeconds 10 -SpeechOnly
#>
[CmdletBinding()]
param(
    [int]$DelaySeconds = 6,
    [switch]$SpeechOnly,
    [switch]$BrailleOnly
)

$ErrorActionPreference = 'Stop'

$token = 'jjfing{0:D4}' -f (Get-Random -Minimum 0 -Maximum 9999)

Write-Host "Token for this run: $token"
Write-Host ""
Write-Host "Move to JJ Flexible NOW and leave it focused. Sending in $DelaySeconds seconds."
Write-Host "JAWS will speak the token out loud; that is the control working, not a fault."

Start-Sleep -Seconds $DelaySeconds

try {
    $jaws = New-Object -ComObject 'freedomsci.jawsapi'
} catch {
    Write-Host ""
    Write-Host "FAILED to create freedomsci.jawsapi. Either JAWS is not running, or"
    Write-Host "FSAPI is not registered. Nothing was sent, so this run says nothing"
    Write-Host "about the capture."
    exit 2
}

$speechSent = $null
$brailleSent = $null

if (-not $BrailleOnly) {
    # Second argument is bInterrupt. FALSE on purpose: interrupting is a
    # behaviour under investigation, and a control must not exercise the thing
    # it is being used to measure.
    $speechSent = $jaws.SayString($token, $false)
    Write-Host "SayString returned: $speechSent"
    Write-Host "  Note this proves only that JAWS SCHEDULED the text. FSAPI"
    Write-Host "  documents the return as 'the text was scheduled to be spoken'."
    Write-Host "  Whether it was spoken is what the capture answers."
}

if (-not $SpeechOnly) {
    Start-Sleep -Milliseconds 400
    $call = 'BrailleString("' + $token + '")'
    $brailleSent = $jaws.RunFunction($call)
    Write-Host "RunFunction returned: $brailleSent"
    Write-Host "  Again: scheduled, not performed."
}

Write-Host ""
Write-Host "Now press Insert+Shift+J inside JJ Flexible to copy the capture, and"
Write-Host "look for $token in it."
Write-Host ""
Write-Host "How to read the result:"
Write-Host "  token on the speech channel and the braille channel  - both doors open"
Write-Host "  token on speech only    - braille never reached the script layer"
Write-Host "  token on braille only   - it arrived, and was dropped before the synth"
Write-Host "  token on neither        - either nothing arrived, or the capture is"
Write-Host "                            not recording. Run Insert+Alt+J to tell"
Write-Host "                            those two apart before concluding anything."
