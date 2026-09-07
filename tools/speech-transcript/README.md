# The speech transcript harness

An NVDA log at Input/Output level records every utterance, every input gesture
and every braille write. On 2026-09-05 one such capture settled a question four
rounds of asking the operator had failed to settle, killed a confident wrong
hypothesis, and surfaced five defects nobody was hunting. That is what seal step
3e made standing practice.

This turns a capture into a **repeatable test**: perform a named scenario, get a
normalised utterance sequence, and diff it against the run that was accepted.

## The four commands

**`start-speech-capture.ps1`** restarts NVDA at IO level, proves it is capturing
by reading real utterances back, and marks the byte offset.

**`read-speech-capture.ps1`** shows what happened after the mark, with the flags
in the margin. The raw eyeball tool.

**`speech-scenario.ps1`** runs a named scenario and compares it to its baseline.

**`archive-nvda-logs.ps1`** dates and rotates the logs before NVDA loses them.

All four call into `SpeechTranscript.psm1`, which is the only implementation of
the parsing, the normalising, the flags and the diff.

## Running a scenario

    speech-scenario.ps1 -List
    speech-scenario.ps1 -Scenario connect -Live
    speech-scenario.ps1 -Scenario connect -Live -Record

`-Live` marks the log where it stands, waits while you perform the scenario, and
reads back from the mark. **It never restarts NVDA** — that takes the operator's
screen reader away for several seconds and it is his call, not a script's. If
NVDA is not already logging speech it refuses rather than capturing nothing.

`-Record` writes the run as the baseline. **Recording a baseline asserts that a
person listened to the run and judged it correct.** Nothing else can establish
that, so nothing else should record one.

Exit codes: 0 matched, 2 differed, 3 no baseline yet, 1 the harness could not
run. **Three is unknown, never a pass.**

## What it flags, and why each one is here

**Burst** — utterances handed over within a few milliseconds. The connect
summary leaves as four lines in 4 ms and takes about 13 seconds to speak, so the
operator hears the first fragment and nothing else. Emission is not delivery.

**Echo** — the same text twice within 100 ms. Two genuine emissions, not a
rescue. #550 measured 19 ms.

**Salvage** — the same text re-spoken inside #503's settle window *after an
interrupt*. The rescue cannot know the first attempt was heard, so it can never
stop. #554 measured 611 ms and 606 ms.

**Repeat** — said again within ten seconds with nothing to explain it.

**Contradiction** — asserts a state that an utterance just before it denies.
#521's signature is a connection claimed between two disconnect announcements,
and every sentence involved is one the app is allowed to say — so a diff of
words alone cannot see it. Order is the defect.

**A regression in a flag is a regression even when every word matches.** The
diff reports that separately.

## What it cannot see

**Interrupts.** NVDA records no cancellation at IO level — every
`CancellableSpeech` in a measured 24,801-line log reads `still valid`, because
that is a snapshot taken at emission. An interrupt is therefore *inferred* from
an input gesture landing between two emissions. With the operator's hands still,
a real salvage loop is reported as a repeat rather than a salvage.

**Whether anything was actually heard.** That is #521's whole subject and it
needs NVDA's completion channel, not a log.

**Braille**, unless a display is attached. The stream is real and simply empty
on a machine with none.

## Normalisation

Two correct runs of the same scenario differ in the callsign, the radio, the
frequencies, the slice letters, the build version and how many discovery
messages arrived. All of that is normalised to placeholders, because a diff that
flags it is a diff nobody reads. The rules and the reasoning are in the module.

**Values that are normalised out are still reported**, under "values normalised
out of the diff". A count that is wrong lives entirely in its number, so
#555's "3 listed, 1 online" would vanish otherwise.

## Baselines

`baselines/` holds one file per scenario, plain text, one utterance per line, so
git diffs it usefully and a screen reader reads it as a list.

**They can only be recorded at the bench**, by a person who has listened to the
run. See `baselines/README.md` for which exist.

## Test data is hand-authored, always

An NVDA log holds everything NVDA spoke in **every** application and echoes typed
words. It is personal data by construction, and this repository is public. Live
captures go to `C:\Users\nrome\JJFlex-private\nvda-logs` or the NAS.

Every file in `fixtures/` is hand-authored and read line by line, and
`SpeechTranscriptHarnessTests` fails if an undeclared one appears there. The
harness also refuses to put a typed word into any artifact — typed events carry
a character count and never the characters.
