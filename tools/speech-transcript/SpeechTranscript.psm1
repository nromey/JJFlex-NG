<#
.SYNOPSIS
  Parse, normalise, flag and diff NVDA speech transcripts.

.DESCRIPTION
  This module is the ONE implementation of the speech-transcript instrument.
  Everything else - the root scripts an operator runs at the bench, and the
  xUnit tests in Radios.Tests - calls into here. That is deliberate: the repo
  has already paid for the alternative, and SpeechQueueDepthHarnessTests says
  so out loud, that a second implementation of a rule is "the description-drift
  defect this codebase keeps paying to remove, planted in the instrument meant
  to catch it".

  It is written to run under Windows PowerShell 5.1 as well as PowerShell 7, so
  the tests can drive it through whichever host is actually resolvable from a
  .NET process. No ternaries, no null-coalescing, no three-argument Join-Path.

  ------------------------------------------------------------------------
  THE LOG FORMAT, AS MEASURED - not as remembered
  ------------------------------------------------------------------------

  Verified 2026-09-06 against a live 24,801-line NVDA 2026.2 log at IO level.
  An utterance is TWO lines: a header carrying the wall clock, and a payload:

      IO - speech.speech.speak (12:15:16.436) - MainThread (32276):
      Speaking [LangChangeCommand ('en_US'), 'Connected to FLEX-8600']

  An input gesture is two lines as well:

      IO - inputCore.InputManager.executeGesture (13:09:49.833) - winInputHook (15872):
      Input: kb(laptop):escape

  Three facts about that payload cost real accuracy, and only one of them was
  being handled before:

  1. THE PAYLOAD IS PYTHON repr, SO THE QUOTING SWITCHES. A string containing
     an apostrophe comes back DOUBLE quoted: "Sid Meier's ...". In the measured
     log, 337 of 1,545 Speaking lines - 22 percent - use double quotes. A
     single-quote-only regex silently drops or mangles every one of them, and
     an utterance that is never extracted looks exactly like an utterance that
     was never spoken. Both quote styles are parsed here.

  2. COMMAND TOKENS CARRY THEIR OWN QUOTED ARGUMENTS. The measured vocabulary
     is LangChangeCommand, CallbackCommand, CharacterModeCommand,
     EndUtteranceCommand, PitchCommand and CancellableSpeech. They are stripped
     STRUCTURALLY, before any literal is extracted, rather than filtered out
     afterwards by matching the language codes we happen to have seen. A filter
     on 'en_US' passes 'en_GB' straight into the transcript as if it had been
     spoken.

  3. ONE UTTERANCE CAN ARRIVE AS MANY LITERALS. When the app speaks through
     nvdaController_speakSsml - which is exactly what #521's completion work
     does - the payload is a word-by-word interleave of literals and
     CallbackCommand(name=SsmlMark_wN). Those are joined back into one
     utterance, because one utterance is what a person hears.

  There is NO cancellation record at IO level. Every CancellableSpeech in the
  measured log reads "still valid" with isCanceledCache False, because that is
  a snapshot taken at emission. So an interrupt is not directly observable and
  this module never claims to observe one - it INFERS an interrupt from an
  input gesture landing between two utterances, which is the mechanism #554
  describes.

  ------------------------------------------------------------------------
  PRIVACY IS A PARSING CONCERN, NOT JUST A FILING ONE
  ------------------------------------------------------------------------

  The log holds everything NVDA spoke in EVERY application, and the repository
  is public. Two consequences are enforced here rather than left to whoever
  remembers:

  - speech.speech.speakTypedCharacters lines echo what the operator TYPED
    ("typed word: password"). There were 185 of them in the measured log. They
    are recorded as a TYPED event carrying a character count and NEVER the
    characters. There is no switch to turn that off.
  - Normalisation replaces callsigns, station names, radio models, frequencies
    and versions with placeholders. That is done for diff stability, but it is
    also what makes a baseline safe to commit.

  Neither of those makes a raw log safe. Raw logs go to JJFlex-private or the
  NAS. Only normalised artifacts belong in the repo.
#>

Set-StrictMode -Version 2.0

# ---------------------------------------------------------------------------
# Tuning constants. Every one of these is a measurement, and the register entry
# it came from is named so the number can be re-derived rather than inherited.
# ---------------------------------------------------------------------------

# #554 / #521: the connect summary leaves as four lines in 4 ms and takes about
# 13 seconds to say. 5 ms is the existing threshold and it caught that block.
$script:BurstMs = 5

# #550: the window and its focus were each announced twice, 19 ms apart. That is
# nowhere near a salvage gap - it is two genuine emissions - so it needs its own
# class rather than being swept into REPEAT.
$script:EchoMs = 100

# #503's salvage settle window was built at a bracketed 600 ms. #554 measured the
# rescue firing at 611 ms and 606 ms AFTER THE INTERRUPT.
$script:SalvageMinMs = 400
$script:SalvageMaxMs = 1200

# Anything else identical inside this window is worth a look but is not
# attributable to the salvage.
$script:RepeatWindowMs = 10000

# #521's captured sequence spans a disconnect, a connect and a second disconnect.
# #554's spans about five seconds. Fifteen seconds covers both with room.
$script:ContradictionMs = 15000

function Get-SpeechTranscriptThresholds {
    <#
    .SYNOPSIS
      The tuning constants, so a report can state what it measured against
      rather than describing them in prose that drifts.
    #>
    return [pscustomobject]@{
        BurstMs         = $script:BurstMs
        EchoMs          = $script:EchoMs
        SalvageMinMs    = $script:SalvageMinMs
        SalvageMaxMs    = $script:SalvageMaxMs
        RepeatWindowMs  = $script:RepeatWindowMs
        ContradictionMs = $script:ContradictionMs
    }
}

# ---------------------------------------------------------------------------
# Parsing
# ---------------------------------------------------------------------------

function ConvertFrom-PythonLiteral {
    <#
    .SYNOPSIS
      Undo the escaping in one Python repr string literal.

    .DESCRIPTION
      ONE PASS, with an evaluator, and deliberately not a chain of -replace
      calls through a placeholder. The chain version shipped first and was
      wrong in a way that only appeared under Windows PowerShell 5.1:

        - 5.1 has no `u escape, so "`u{0001}" is the literal text u{0001}.
        - -replace takes a REGEX, and u{0001} means "u, exactly once".

      So the final step rewrote every letter u as a backslash. "Default"
      became "Defa\lt" and "PC audio on." became "PC a\dio on." - under 5.1
      only, while 7 was perfect. A single pass has no placeholder to collide
      with, and handles \\ correctly by consuming both characters at once.
    #>
    param([string] $Value)

    if (-not $Value) { return $Value }

    return [regex]::Replace($Value, '\\(.)', {
        param($m)
        $c = $m.Groups[1].Value
        switch ($c) {
            'n'  { ' ' }
            'r'  { ' ' }
            't'  { ' ' }
            '\'  { '\' }
            "'"  { "'" }
            '"'  { '"' }
            default { $c }
        }
    })
}

function Get-SpokenTextFromPayload {
    <#
    .SYNOPSIS
      Turn a "Speaking [...]" payload into the sentence a person would hear.
    .DESCRIPTION
      Strips the command tokens structurally first, then takes what is left,
      then joins. See the module header for why each step is in that order.
    #>
    param([string] $Payload)

    if (-not $Payload) { return '' }
    if ($Payload -notmatch '^\s*Speaking\s*\[') { return '' }

    $body = $Payload -replace '^\s*Speaking\s*\[', ''
    $body = $body -replace '\]\s*$', ''

    # Command tokens carry quoted arguments of their own. Remove them whole,
    # before any literal is extracted.
    $body = [regex]::Replace($body, 'CancellableSpeech\s*\([^)]*\)', ' ')
    $body = [regex]::Replace($body, '\b\w*Command\s*\([^)]*\)', ' ')
    $body = [regex]::Replace($body, '\b\w*Command\b', ' ')

    # What remains is the spoken material, as one or more Python string
    # literals in either quoting style.
    $pattern = '"((?:[^"\\]|\\.)*)"' + '|' + "'((?:[^'\\]|\\.)*)'"
    $parts = @()
    foreach ($m in [regex]::Matches($body, $pattern)) {
        $raw = $m.Groups[1].Value
        if (-not $raw) { $raw = $m.Groups[2].Value }
        $piece = (ConvertFrom-PythonLiteral $raw)
        if ($piece -ne $null -and $piece.Trim().Length -gt 0) { $parts += $piece }
    }
    if ($parts.Count -eq 0) { return '' }

    $said = ($parts -join ' ')
    $said = [regex]::Replace($said, '\s+', ' ')
    return $said.Trim()
}

function Read-NvdaTranscript {
    <#
    .SYNOPSIS
      Read an NVDA log into an ordered event list.

    .PARAMETER Path
      The log. May be the live %TEMP%\nvda.log, which NVDA holds open - the
      read goes through a sharing stream for exactly that reason.

    .PARAMETER FromByte
      Start here. This is how a scenario is separated from the setup noise
      around it.

    .OUTPUTS
      Objects with Ms (milliseconds since midnight, monotonic across a midnight
      crossing), Ts (the clock as written), Kind (SAID, KEY or TYPED), Text and
      Index.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [int] $FromByte = 0
    )

    # A failure here MUST throw. The first version of this function let a failed
    # open print an error and carry on, and the caller then reported "0
    # utterances" and a clean bill of health - the exact absence-is-not-evidence
    # failure this instrument exists to catch, sitting inside the instrument.
    $ErrorActionPreference = 'Stop'

    if (-not (Test-Path $Path)) { throw "no transcript at $Path" }

    # .NET's working directory is not PowerShell's, so a relative path that
    # Test-Path accepts can still fail inside [IO.File]::Open. Resolve it once.
    $Path = (Resolve-Path -LiteralPath $Path).ProviderPath

    $size = (Get-Item $Path).Length
    if ($FromByte -gt $size) {
        throw ("the mark is at byte $FromByte but the log is only $size bytes. " +
               "The log has ROTATED - NVDA restarted and the marked session is now " +
               "in nvda-old.log. Rescue it with archive-nvda-logs.ps1 before it is lost.")
    }

    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$fs.Seek($FromByte, 'Begin')
        $sr = New-Object IO.StreamReader($fs)
        try { $text = $sr.ReadToEnd() } finally { $sr.Close() }
    } finally { $fs.Dispose() }

    $lines = $text -split "`r?`n"
    $events = New-Object System.Collections.ArrayList
    $dayOffset = 0
    $lastMs = -1
    $index = 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $head = $lines[$i]
        if ($head -notmatch '^\w+ - ') { continue }
        if ($head -notmatch '\((\d{2}):(\d{2}):(\d{2})\.(\d{3})\)') { continue }

        $ts = "$($Matches[1]):$($Matches[2]):$($Matches[3]).$($Matches[4])"
        $raw = ([int]$Matches[1]) * 3600000 + ([int]$Matches[2]) * 60000 +
               ([int]$Matches[3]) * 1000 + [int]$Matches[4]

        # NVDA's log carries no date. A session that crosses midnight would
        # otherwise produce a negative gap and turn every timing rule off.
        if ($lastMs -ge 0 -and $raw + $dayOffset -lt $lastMs - 1000) { $dayOffset += 86400000 }
        $ms = $raw + $dayOffset
        if ($ms -gt $lastMs) { $lastMs = $ms }

        $payload = ''
        if ($i + 1 -lt $lines.Count) { $payload = $lines[$i + 1] }

        $kind = $null
        $body = $null

        if ($head -match 'speech\.speech\.speakTypedCharacters') {
            # NEVER the characters. See the module header.
            $typed = $payload -replace '^\s*typed word:\s*', ''
            $kind = 'TYPED'
            $body = "typed (" + $typed.Trim().Length + " characters, not recorded)"
        }
        elseif ($head -match 'speech\.speech\.speak ') {
            $said = Get-SpokenTextFromPayload $payload
            if ($said) { $kind = 'SAID'; $body = $said }
        }
        elseif ($head -match 'executeGesture') {
            if ($payload -match '^\s*Input:\s*(.+?)\s*$') {
                $kind = 'KEY'
                $body = $Matches[1]
            }
        }

        if ($kind) {
            $index++
            [void]$events.Add([pscustomobject]@{
                Index = $index
                Ms    = $ms
                Ts    = $ts
                Kind  = $kind
                Text  = $body
            })
        }
    }

    return , @($events.ToArray())
}

# ---------------------------------------------------------------------------
# Normalisation
# ---------------------------------------------------------------------------
#
# NORMALISATION IS THE WHOLE JOB, so every rule below states what it treats as
# noise and why. The test is always the same question: would two correct runs
# of the same scenario differ here? If yes it is noise, and a diff that reports
# it is a diff nobody reads. If no it is signal and it stays.
#
# The rules that erase a VALUE also record what they erased. That matters for
# #555, where "3 listed, 1 online" is wrong and the wrongness lives entirely in
# the number. Normalising the number out of the diff without surfacing it
# somewhere would hide the defect this instrument exists to find - so
# Get-VolatileValues reports them separately, out of the diff but in front of
# the reader.

function ConvertTo-NormalizedUtterance {
    <#
    .SYNOPSIS
      Reduce one utterance to the form that is comparable between runs.
    #>
    param(
        [string]   $Text,
        [string[]] $StationAlias = @()
    )

    if (-not $Text) { return '' }
    $t = $Text

    # Operator-supplied names first: a station a person named "6300inshack"
    # cannot be recognised by any pattern, so it has to be declared. Longest
    # first, so one alias cannot eat the prefix of another.
    foreach ($alias in ($StationAlias | Sort-Object -Property Length -Descending)) {
        if ($alias) { $t = $t -replace [regex]::Escape($alias), '<station>' }
    }

    # Build version. Changes on every commit; never signal.
    $t = [regex]::Replace($t, '\bversion\s+\d+(\.\d+){1,3}\b', 'version <version>', 'IgnoreCase')

    # Radio model. Which radio is on the bench is a property of the bench.
    $t = [regex]::Replace($t, '\bFLEX-\d{4}[MR]?\b', '<radio>', 'IgnoreCase')
    $t = [regex]::Replace($t, '\bAU-5\d0M?\b', '<radio>', 'IgnoreCase')

    # Frequency, with and without a spoken unit.
    $t = [regex]::Replace($t, '\b\d{1,3}\.\d{1,6}\s*(megahertz|kilohertz|MHz|kHz)\b', '<freq>', 'IgnoreCase')
    $t = [regex]::Replace($t, '\b\d{1,3}\.\d{3,6}\b', '<freq>')

    # Slice letter. Which slice happens to be selected is bench state; that a
    # slice is named at all is the signal.
    $t = [regex]::Replace($t, '\b(slice)\s+([A-H])\b', '$1 <s>', 'IgnoreCase')

    # Callsign shape, as a standalone token. Deliberately after the radio model,
    # or AU-510 would be eaten as a call.
    $t = [regex]::Replace($t, '\b[A-Z]{1,2}\d[A-Z]{1,4}\b', '<station>')

    # "<something> is now available." is discovery announcing a station by a
    # name we cannot pattern-match. The phrase identifies the position.
    $t = [regex]::Replace($t, '^\s*\S+\s+is now available\.?\s*$', '<station> is now available.')

    # Bare counts. See the note above: erased from the diff, reported by
    # Get-VolatileValues so #555 stays visible.
    $t = [regex]::Replace($t, '(?<![\w<])\d+(?![\w>])', '<n>')

    $t = [regex]::Replace($t, '\s+', ' ')
    return $t.Trim()
}

function Get-VolatileValues {
    <#
    .SYNOPSIS
      Every utterance whose meaning depends on a value normalisation removed,
      grouped by the normalised form.
    .DESCRIPTION
      This is where a count that is wrong shows itself. #555's "3 listed, 1
      online" and "3 listed, 2 online" normalise to one line and vanish from
      the diff; here they sit side by side under that line, which is what lets
      a person see that the same action reported two different worlds.
    #>
    param([object[]] $Events, [string[]] $StationAlias = @())

    $map = @{}
    foreach ($e in $Events) {
        if ($e.Kind -ne 'SAID') { continue }
        $norm = ConvertTo-NormalizedUtterance -Text $e.Text -StationAlias $StationAlias
        if ($norm -eq $e.Text) { continue }
        if (-not $map.ContainsKey($norm)) { $map[$norm] = New-Object System.Collections.ArrayList }
        if (-not $map[$norm].Contains($e.Text)) { [void]$map[$norm].Add($e.Text) }
    }

    $out = New-Object System.Collections.ArrayList
    foreach ($k in ($map.Keys | Sort-Object)) {
        [void]$out.Add([pscustomobject]@{ Normalized = $k; Seen = @($map[$k].ToArray()) })
    }
    return , @($out.ToArray())
}

# ---------------------------------------------------------------------------
# Flags
# ---------------------------------------------------------------------------

function Get-StateClaim {
    <#
    .SYNOPSIS
      Classify an utterance as a claim about the connection, or as nothing.
    .DESCRIPTION
      Only utterances that ASSERT A STATE are classified. Everything else
      returns $null and takes no part in the contradiction rule, which keeps
      the rule small enough to argue about.
    #>
    param([string] $Text)

    if (-not $Text) { return $null }

    if ($Text -match '(?i)^\s*Connecting\b')                                  { return 'connecting' }
    if ($Text -match '(?i)^\s*Connected to\b')                                { return 'connected' }
    if ($Text -match '(?i)\bDisconnecting from\b|\bdisconnected from radio\b|\bgoodbye\b') { return 'disconnect-action' }
    if ($Text -match '(?i)\bno radio connected\b')                            { return 'no-radio-status' }
    if ($Text -match '(?i)\bPress Enter to connect\b')                        { return 'picker-open' }

    return $null
}

function Get-SpeechFlags {
    <#
    .SYNOPSIS
      Run the three rules over an event list.

    .DESCRIPTION
      BURST (!!) - utterances handed over within a few milliseconds of each
      other. #554 measured four lines in 4 ms that take roughly 13 seconds to
      speak, so the operator hears the first fragment and nothing else.
      Emission is not delivery, and this is that gap in one line of evidence.

      ECHO (==) - the same text twice within 100 ms. That is #550: the window
      and its focus each announced twice, 19 ms apart. Two genuine emissions,
      not a rescue, and it needs its own class or it is invisible.

      SALVAGE (@@) - THE RULE THAT WAS WRONG, AND THE REASON THIS TRACK EXISTS.
      The previous rule looked for identical text 400-1200 ms apart, measuring
      utterance to utterance. Run against #554's own capture it finds NOTHING:
      the gaps between the repeated blocks there are 4,083 ms and 1,284 ms,
      one far too wide and the other just outside the window. The 611 ms and
      606 ms in the register are measured FROM THE INTERRUPT, not from the
      previous utterance - which is the salvage mechanism itself: emit,
      something cancels, wait the settle window, decide it went unheard,
      re-speak. So the gap is measured from the interrupting gesture. An
      instrument that could not flag the defect it was built from was not
      finished, and this is the correction.

      REPEAT (~~) - identical text again within 10 seconds, not attributable to
      either of the above. Worth a look, claimed as nothing more.

      CONTRADICTION (XX) - two utterances asserting mutually exclusive states
      with no legitimate transition between them. #521's signature is exactly
      this: "Connected to FLEX-8600" spoken between two disconnect
      announcements. A diff of words alone cannot see it, because every one of
      those sentences is a sentence the app is supposed to be able to say.

      NOTE ON INTERRUPTS. NVDA records no cancellation at IO level - every
      CancellableSpeech in a measured 24,801-line log reads "still valid". An
      interrupt is therefore INFERRED from an input gesture between the two
      emissions. Where the operator's hands are still, a genuine salvage loop
      will not be flagged as SALVAGE and will surface as REPEAT instead. That
      limit is real and is stated rather than papered over.
    #>
    [CmdletBinding()]
    param(
        [object[]] $Events,
        [string[]] $StationAlias = @()
    )

    $findings = New-Object System.Collections.ArrayList
    if (-not $Events -or $Events.Count -eq 0) { return , @() }

    $said = @($Events | Where-Object { $_.Kind -eq 'SAID' })
    $keys = @($Events | Where-Object { $_.Kind -eq 'KEY' })

    # Normalise once. Identity for the repeat rules is the NORMALISED text, so a
    # rescue of "Connected to FLEX-8600, SmartLink, 4 slices." still matches
    # itself when the bench has a different radio in it.
    $norm = @{}
    foreach ($e in $said) {
        $norm[$e.Index] = ConvertTo-NormalizedUtterance -Text $e.Text -StationAlias $StationAlias
    }

    # ---- BURST ----
    for ($i = 1; $i -lt $said.Count; $i++) {
        $gap = $said[$i].Ms - $said[$i - 1].Ms
        if ($gap -le $script:BurstMs) {
            [void]$findings.Add([pscustomobject]@{
                Flag = 'BURST'; Mark = '!!'; Index = $said[$i].Index; Ts = $said[$i].Ts
                GapMs = $gap; Text = $said[$i].Text
                Note = "handed over $gap ms after the previous utterance"
            })
        }
    }

    # ---- ECHO / SALVAGE / REPEAT ----
    $lastSeen = @{}
    foreach ($e in $said) {
        $key = $norm[$e.Index]
        if ($lastSeen.ContainsKey($key)) {
            $prev = $lastSeen[$key]
            $gap = $e.Ms - $prev.Ms

            if ($gap -le $script:EchoMs) {
                [void]$findings.Add([pscustomobject]@{
                    Flag = 'ECHO'; Mark = '=='; Index = $e.Index; Ts = $e.Ts
                    GapMs = $gap; Text = $e.Text
                    Note = "said again $gap ms later - two emissions, not a rescue (#550)"
                })
            }
            else {
                # The interrupt is inferred: the LAST gesture strictly between
                # the two emissions. Measuring from there is what makes the
                # salvage window mean anything.
                $between = @($keys | Where-Object { $_.Ms -gt $prev.Ms -and $_.Ms -lt $e.Ms })
                $matched = $false
                if ($between.Count -gt 0) {
                    $interrupt = $between[$between.Count - 1]
                    $sinceInterrupt = $e.Ms - $interrupt.Ms
                    if ($sinceInterrupt -ge $script:SalvageMinMs -and $sinceInterrupt -le $script:SalvageMaxMs) {
                        $matched = $true
                        [void]$findings.Add([pscustomobject]@{
                            Flag = 'SALVAGE'; Mark = '@@'; Index = $e.Index; Ts = $e.Ts
                            GapMs = $sinceInterrupt; Text = $e.Text
                            Note = ("re-spoken $sinceInterrupt ms after '" + $interrupt.Text +
                                    "' at " + $interrupt.Ts + " - inside #503's settle window, " +
                                    "so the salvage rescued a block it could not know had been heard")
                        })
                    }
                }
                if (-not $matched -and $gap -le $script:RepeatWindowMs) {
                    [void]$findings.Add([pscustomobject]@{
                        Flag = 'REPEAT'; Mark = '~~'; Index = $e.Index; Ts = $e.Ts
                        GapMs = $gap; Text = $e.Text
                        Note = "said again $gap ms later, with no interrupt between to explain it"
                    })
                }
            }
        }
        $lastSeen[$key] = $e
    }

    # ---- CONTRADICTION ----
    $claims = New-Object System.Collections.ArrayList
    foreach ($e in $said) {
        $c = Get-StateClaim -Text $e.Text
        if ($c) { [void]$claims.Add([pscustomobject]@{ Ev = $e; Claim = $c }) }
    }

    for ($i = 1; $i -lt $claims.Count; $i++) {
        $cur = $claims[$i]
        $note = $null

        for ($j = $i - 1; $j -ge 0; $j--) {
            $prior = $claims[$j]
            if ($cur.Ev.Ms - $prior.Ev.Ms -gt $script:ContradictionMs) { break }

            # R1: connected asserted after a disconnect, with no connect attempt
            # between. #521 line 59 exactly - the original sentence, verbatim,
            # spoken after the disconnect it contradicts.
            if ($cur.Claim -eq 'connected' -and $prior.Claim -eq 'disconnect-action') {
                # PowerShell's range operator counts DOWN when the start is the
                # larger value, so an empty span has to be tested for, not
                # indexed - $claims[$i..($i-1)] silently returns two elements.
                $tween = @()
                if ($i - $j -gt 1) {
                    $tween = @($claims[($j + 1)..($i - 1)] | Where-Object { $_.Claim -eq 'connecting' })
                }
                if ($tween.Count -eq 0) {
                    $note = ("asserts a connection after '" + $prior.Ev.Text + "' at " +
                             $prior.Ev.Ts + ", with no connect attempt announced between them")
                }
                break
            }

            # R2: the picker inviting a connection that has already begun.
            # #521's second surface - "Press Enter to connect" spoken to an
            # operator who already did.
            if ($cur.Claim -eq 'picker-open' -and ($prior.Claim -eq 'connecting' -or $prior.Claim -eq 'connected')) {
                $note = ("invites a connection that '" + $prior.Ev.Text + "' at " +
                         $prior.Ev.Ts + " says has already begun")
                break
            }

            # R3: "no radio connected" while a connection was just announced.
            if ($cur.Claim -eq 'no-radio-status' -and $prior.Claim -eq 'connected') {
                $tween = @()
                if ($i - $j -gt 1) {
                    $tween = @($claims[($j + 1)..($i - 1)] | Where-Object { $_.Claim -eq 'disconnect-action' })
                }
                if ($tween.Count -eq 0) {
                    $note = ("reports no radio, though '" + $prior.Ev.Text + "' at " +
                             $prior.Ev.Ts + " reported one and nothing announced a disconnect")
                }
                break
            }

            if ($prior.Claim -ne $cur.Claim) { break }
        }

        if ($note) {
            [void]$findings.Add([pscustomobject]@{
                Flag = 'CONTRADICTION'; Mark = 'XX'; Index = $cur.Ev.Index; Ts = $cur.Ev.Ts
                GapMs = 0; Text = $cur.Ev.Text; Note = $note
            })
        }
    }

    return , @($findings.ToArray() | Sort-Object Index, Flag)
}

# ---------------------------------------------------------------------------
# Scenarios
# ---------------------------------------------------------------------------

function Get-SpeechScenario {
    <#
    .SYNOPSIS
      The named scenarios, and what the operator has to do for each.
    #>
    param([string] $Name)

    $all = @(
        [pscustomobject]@{
            Name = 'startup-to-picker'
            What = 'Launch the app. Stop as soon as the radio picker has focus. Do not connect.'
            Why  = '#551. Five utterances arrive before the operator can act, and three of them still carry wording that was ruled out. The COUNT is the measurement here, so the report leads with it.'
        }
        [pscustomobject]@{
            Name = 'connect'
            What = 'From the picker, select the radio and press Enter. Stop when Home has focus and speech has settled.'
            Why  = '#554. The connect summary leaves as four lines in 4 ms and takes about 13 seconds to say, and the salvage rescues it after every interrupt.'
        }
        [pscustomobject]@{
            Name = 'disconnect'
            What = 'From Home, disconnect from the radio. Stop when speech has settled.'
            Why  = 'The other half of the connect pair. A disconnect that is followed by a connection claim is #521.'
        }
        [pscustomobject]@{
            Name = 'close-relaunch-connect-close'
            What = 'Close the app. Relaunch it. Connect. Close it again. This is one continuous capture, not four.'
            Why  = "#521's own fixture. Its register entry says the reproduction is one capture and costs nothing, so any fix has a test. This is that test."
        }
        [pscustomobject]@{
            Name = 'open-audio-layer-and-escape'
            What = 'From Home, open the audio layer, then press Escape. Stop when speech has settled.'
            Why  = 'A short scenario with a dialog in it, to catch announcements that survive the dialog closing.'
        }
    )

    if ($Name) {
        $hit = @($all | Where-Object { $_.Name -eq $Name })
        if ($hit.Count -eq 0) {
            throw ("unknown scenario '$Name'. Known: " + (($all | ForEach-Object { $_.Name }) -join ', '))
        }
        return $hit[0]
    }
    return , $all
}

function New-ScenarioArtifact {
    <#
    .SYNOPSIS
      Turn an event list into the comparable artifact for a scenario.
    .DESCRIPTION
      The artifact is the NORMALISED utterance sequence and nothing else.

      Input gestures are deliberately NOT part of it. They are recorded, shown
      in the live report and used by the salvage rule, but a run where the
      operator pressed Tab four times instead of three is the same run, and a
      diff that says otherwise is a diff nobody reads. The gesture COUNT is
      carried in the header, so a wild difference is still visible.

      TYPED events never enter the artifact at all. They are the operator's own
      keystrokes and they exist here only as a count.
    #>
    param(
        [string]   $Scenario,
        [object[]] $Events,
        [string[]] $StationAlias = @()
    )

    $said = @($Events | Where-Object { $_.Kind -eq 'SAID' })
    $lines = New-Object System.Collections.ArrayList
    $t0 = 0
    if ($said.Count -gt 0) { $t0 = $said[0].Ms }

    foreach ($e in $said) {
        [void]$lines.Add([pscustomobject]@{
            Text       = ConvertTo-NormalizedUtterance -Text $e.Text -StationAlias $StationAlias
            RelativeMs = $e.Ms - $t0
            Ts         = $e.Ts
            Raw        = $e.Text
        })
    }

    $flags = Get-SpeechFlags -Events $Events -StationAlias $StationAlias

    return [pscustomobject]@{
        Scenario     = $Scenario
        Utterances   = @($lines.ToArray())
        GestureCount = @($Events | Where-Object { $_.Kind -eq 'KEY' }).Count
        TypedCount   = @($Events | Where-Object { $_.Kind -eq 'TYPED' }).Count
        Flags        = $flags
        FlagCounts   = (Get-FlagCounts -Findings $flags)
        Volatile     = (Get-VolatileValues -Events $Events -StationAlias $StationAlias)
    }
}

function Get-FlagCounts {
    param([object[]] $Findings)
    $c = [ordered]@{ BURST = 0; ECHO = 0; SALVAGE = 0; REPEAT = 0; CONTRADICTION = 0 }
    foreach ($f in $Findings) { $c[$f.Flag] = $c[$f.Flag] + 1 }
    return [pscustomobject]$c
}

# ---------------------------------------------------------------------------
# Baselines
# ---------------------------------------------------------------------------

function Get-BaselineDirectory {
    return (Join-Path $PSScriptRoot 'baselines')
}

function Get-BaselinePath {
    param([Parameter(Mandatory = $true)][string] $Scenario)
    return (Join-Path (Get-BaselineDirectory) ($Scenario + '.baseline.txt'))
}

function Export-ScenarioBaseline {
    <#
    .SYNOPSIS
      Write a scenario's baseline. Plain text, one utterance per line, so git
      diffs it usefully and a screen reader reads it as a list.
    #>
    param(
        [Parameter(Mandatory = $true)] $Artifact,
        [string] $Path
    )

    if (-not $Path) { $Path = Get-BaselinePath -Scenario $Artifact.Scenario }
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $fc = $Artifact.FlagCounts
    $out = New-Object System.Collections.ArrayList
    [void]$out.Add('# JJ Flexible speech scenario baseline')
    [void]$out.Add('# scenario: ' + $Artifact.Scenario)
    [void]$out.Add('# recorded: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ' machine time')
    [void]$out.Add('# utterances: ' + $Artifact.Utterances.Count)
    [void]$out.Add('# gestures: ' + $Artifact.GestureCount)
    [void]$out.Add(('# flags: burst {0}, echo {1}, salvage {2}, repeat {3}, contradiction {4}' -f `
                    $fc.BURST, $fc.ECHO, $fc.SALVAGE, $fc.REPEAT, $fc.CONTRADICTION))
    [void]$out.Add('#')
    [void]$out.Add('# Values in angle brackets were normalised out on purpose - see')
    [void]$out.Add('# tools/speech-transcript/SpeechTranscript.psm1 for which, and why.')
    [void]$out.Add('# A baseline is an ACCEPTED run. Recording one asserts that a person')
    [void]$out.Add('# listened to it and judged it correct.')
    [void]$out.Add('#')

    $n = 0
    foreach ($u in $Artifact.Utterances) {
        $n++
        [void]$out.Add(('{0,3}  {1}' -f $n, $u.Text))
    }

    Set-Content -Path $Path -Value ($out.ToArray()) -Encoding UTF8
    return $Path
}

function Import-ScenarioBaseline {
    <#
    .SYNOPSIS
      Read a baseline back. Returns $null when there is none - and the caller
      must treat that as "unknown", never as "matched".
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Scenario,
        [string] $Path
    )

    if (-not $Path) { $Path = Get-BaselinePath -Scenario $Scenario }
    if (-not (Test-Path $Path)) { return $null }

    $lines = @(Get-Content $Path)
    $utt = New-Object System.Collections.ArrayList
    $header = New-Object System.Collections.ArrayList
    foreach ($l in $lines) {
        if ($l -match '^\s*#') { [void]$header.Add($l); continue }
        if ($l -match '^\s*$') { continue }
        [void]$utt.Add(($l -replace '^\s*\d+\s\s', '').Trim())
    }

    return [pscustomobject]@{
        Scenario   = $Scenario
        Path       = $Path
        Header     = @($header.ToArray())
        Utterances = @($utt.ToArray())
    }
}

# ---------------------------------------------------------------------------
# Diff
# ---------------------------------------------------------------------------

function Get-LongestCommonSubsequence {
    param([string[]] $A, [string[]] $B)

    $n = $A.Count; $m = $B.Count
    if ($n -eq 0 -or $m -eq 0) { return , @() }

    # A FLAT table with explicit index arithmetic, deliberately, because two
    # different PowerShell traps live in the obvious spelling of this:
    #
    #   New-Object 'int[,]' a,b   quietly returns a ONE-dimensional Object[],
    #                             and $dp[$i,$j] on a 1-D array is not an error
    #                             - it is multiple-index SELECTION, so it hands
    #                             back an array.
    #   $dp[$i + 1, $j]           parses as $i + (1, $j). The comma builds an
    #                             array and the addition is attempted against
    #                             it, so even a correct [int[,]] table fails.
    #
    # Both failures are silent in the sense that matters: the table fills with
    # nothing, the walk misbehaves, and the loop spins. The first version of
    # this function produced 21 MB of op_Addition errors and never returned.
    $w = $m + 1
    $dp = [int[]]::new(($n + 1) * $w)
    for ($i = $n - 1; $i -ge 0; $i--) {
        for ($j = $m - 1; $j -ge 0; $j--) {
            $here = $i * $w + $j
            $down = $here + $w          # ($i + 1, $j)
            $right = $here + 1          # ($i, $j + 1)
            if ($A[$i] -ceq $B[$j]) { $dp[$here] = $dp[$down + 1] + 1 }
            elseif ($dp[$down] -ge $dp[$right]) { $dp[$here] = $dp[$down] }
            else { $dp[$here] = $dp[$right] }
        }
    }

    $pairs = New-Object System.Collections.ArrayList
    $i = 0; $j = 0
    # The walk advances i or j on every pass, so it terminates by construction -
    # but "by construction" is exactly what was believed about the table above.
    # The guard costs nothing and turns a hang into a diagnosis.
    $guard = $n + $m + 2
    while ($i -lt $n -and $j -lt $m) {
        $guard--
        if ($guard -lt 0) {
            throw "the subsequence walk failed to terminate - the comparison table is not what it should be"
        }
        $here = $i * $w + $j
        if ($A[$i] -ceq $B[$j]) {
            [void]$pairs.Add([pscustomobject]@{ A = $i; B = $j; Text = $A[$i] })
            $i++; $j++
        }
        elseif ($dp[$here + $w] -ge $dp[$here + 1]) { $i++ }
        else { $j++ }
    }
    return , @($pairs.ToArray())
}

function Compare-ScenarioArtifact {
    <#
    .SYNOPSIS
      Diff a run against its baseline, in the three terms that matter.

    .DESCRIPTION
      NOW SAID     - in this run, not in the baseline.
      NO LONGER SAID - in the baseline, not in this run.
      CHANGED ORDER  - in both, but not in the same place.

      Order is a first-class result rather than a footnote, because #521 is
      entirely about things arriving in the wrong sequence. Every sentence in
      its captured failure is a sentence the app is allowed to say; the defect
      is only visible as an ordering.

      The order result is computed as the complement of the longest common
      subsequence: everything the two runs share IN ORDER is the LCS, so
      anything present in both and outside it is what moved. That reports the
      smallest set of moves rather than cascading one insertion into "and
      everything after it also moved".
    #>
    param(
        [Parameter(Mandatory = $true)] $Baseline,
        [Parameter(Mandatory = $true)] $Artifact
    )

    $b = @($Baseline.Utterances)
    $c = @($Artifact.Utterances | ForEach-Object { $_.Text })

    $lcs = Get-LongestCommonSubsequence -A $b -B $c
    $inLcsB = @{}; $inLcsC = @{}
    foreach ($p in $lcs) { $inLcsB[$p.A] = $true; $inLcsC[$p.B] = $true }

    # Multiset accounting, so a line said twice where it used to be said once
    # is reported rather than absorbed.
    $bCount = @{}; foreach ($x in $b) { if ($bCount.ContainsKey($x)) { $bCount[$x]++ } else { $bCount[$x] = 1 } }
    $cCount = @{}; foreach ($x in $c) { if ($cCount.ContainsKey($x)) { $cCount[$x]++ } else { $cCount[$x] = 1 } }

    $added = New-Object System.Collections.ArrayList
    $removed = New-Object System.Collections.ArrayList
    $moved = New-Object System.Collections.ArrayList

    $seenC = @{}
    for ($i = 0; $i -lt $c.Count; $i++) {
        $x = $c[$i]
        if (-not $seenC.ContainsKey($x)) { $seenC[$x] = 0 }
        $seenC[$x]++
        $inB = 0; if ($bCount.ContainsKey($x)) { $inB = $bCount[$x] }
        if ($seenC[$x] -gt $inB) {
            [void]$added.Add([pscustomobject]@{ Position = $i + 1; Text = $x })
        }
        elseif (-not $inLcsC.ContainsKey($i)) {
            [void]$moved.Add([pscustomobject]@{ Position = $i + 1; Text = $x })
        }
    }

    $seenB = @{}
    for ($i = 0; $i -lt $b.Count; $i++) {
        $x = $b[$i]
        if (-not $seenB.ContainsKey($x)) { $seenB[$x] = 0 }
        $seenB[$x]++
        $inC = 0; if ($cCount.ContainsKey($x)) { $inC = $cCount[$x] }
        if ($seenB[$x] -gt $inC) {
            [void]$removed.Add([pscustomobject]@{ Position = $i + 1; Text = $x })
        }
    }

    # Flag regressions. A run whose words are right but which now bursts, or
    # rescues, or contradicts itself, is a regression - the whole point of
    # keeping the flags is that they are not visible in the words.
    $baseFlags = [ordered]@{ BURST = 0; ECHO = 0; SALVAGE = 0; REPEAT = 0; CONTRADICTION = 0 }
    foreach ($h in $Baseline.Header) {
        if ($h -match 'flags:\s*burst\s*(\d+),\s*echo\s*(\d+),\s*salvage\s*(\d+),\s*repeat\s*(\d+),\s*contradiction\s*(\d+)') {
            $baseFlags.BURST = [int]$Matches[1]; $baseFlags.ECHO = [int]$Matches[2]
            $baseFlags.SALVAGE = [int]$Matches[3]; $baseFlags.REPEAT = [int]$Matches[4]
            $baseFlags.CONTRADICTION = [int]$Matches[5]
        }
    }

    $worse = New-Object System.Collections.ArrayList
    foreach ($k in @('BURST', 'ECHO', 'SALVAGE', 'REPEAT', 'CONTRADICTION')) {
        $now = $Artifact.FlagCounts.$k
        $was = $baseFlags[$k]
        if ($now -gt $was) {
            [void]$worse.Add([pscustomobject]@{ Flag = $k; Was = $was; Now = $now })
        }
    }

    return [pscustomobject]@{
        Scenario       = $Artifact.Scenario
        BaselineCount  = $b.Count
        CurrentCount   = $c.Count
        Added          = @($added.ToArray())
        Removed        = @($removed.ToArray())
        Moved          = @($moved.ToArray())
        FlagRegression = @($worse.ToArray())
        Matches        = ($added.Count -eq 0 -and $removed.Count -eq 0 -and
                          $moved.Count -eq 0 -and $worse.Count -eq 0)
    }
}

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

function Format-SpeechTimeline {
    <#
    .SYNOPSIS
      The run as a person would step through it: everything in order, with the
      flags in the margin and the clock relative to the first utterance.
    #>
    param([object[]] $Events, [object[]] $Findings, [switch] $ShowRaw)

    $out = New-Object System.Collections.ArrayList
    if (-not $Events -or $Events.Count -eq 0) {
        [void]$out.Add('nothing captured')
        return , @($out.ToArray())
    }

    $byIndex = @{}
    foreach ($f in $Findings) {
        if (-not $byIndex.ContainsKey($f.Index)) { $byIndex[$f.Index] = New-Object System.Collections.ArrayList }
        [void]$byIndex[$f.Index].Add($f)
    }

    $t0 = $Events[0].Ms
    foreach ($e in $Events) {
        $mark = '  '
        if ($byIndex.ContainsKey($e.Index)) { $mark = $byIndex[$e.Index][0].Mark }
        $rel = $e.Ms - $t0
        [void]$out.Add(('{0} {1,7} ms  {2,-5} {3}' -f $mark, $rel, $e.Kind, $e.Text))
        if ($byIndex.ContainsKey($e.Index)) {
            foreach ($f in $byIndex[$e.Index]) {
                [void]$out.Add(('        ' + $f.Flag + ': ' + $f.Note))
            }
        }
    }
    return , @($out.ToArray())
}

Export-ModuleMember -Function `
    Read-NvdaTranscript, Get-SpokenTextFromPayload, ConvertFrom-PythonLiteral,
    ConvertTo-NormalizedUtterance, Get-VolatileValues,
    Get-StateClaim, Get-SpeechFlags, Get-FlagCounts,
    Get-SpeechScenario, New-ScenarioArtifact,
    Get-BaselineDirectory, Get-BaselinePath, Export-ScenarioBaseline, Import-ScenarioBaseline,
    Get-LongestCommonSubsequence, Compare-ScenarioArtifact,
    Format-SpeechTimeline, Get-SpeechTranscriptThresholds
