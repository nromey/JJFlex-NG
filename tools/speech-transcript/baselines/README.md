# Scenario baselines

One file per scenario. Plain text, one normalised utterance per line, so git
diffs it usefully and a screen reader reads it as a list.

## None are recorded yet, and that is not an omission

**A baseline is an ACCEPTED run.** Recording one asserts that a person listened
to it and judged it correct. Nothing else can establish that — not a build, not
a test, and not the agent that wrote this harness, which is forbidden to launch
the application or restart NVDA precisely because both take the machine away
from the operator.

So the baselines are recorded at the bench, once, per scenario:

    speech-scenario.ps1 -Scenario startup-to-picker -Live -Record
    speech-scenario.ps1 -Scenario connect -Live -Record
    speech-scenario.ps1 -Scenario disconnect -Live -Record
    speech-scenario.ps1 -Scenario close-relaunch-connect-close -Live -Record
    speech-scenario.ps1 -Scenario open-audio-layer-and-escape -Live -Record

**Do not record a baseline from a build with a known speech defect in it.** As
of Sprint 46 that means #554's salvage loop, #551's three ruled-out startup
lines and #550's doubled window announcement are all still live, so a baseline
taken today would enshrine them and a later fix would read as a regression.
Record after the speech tracks land, not before.

Until a scenario has one, `speech-scenario.ps1` exits **3** and says the run is
UNKNOWN. It never reports a pass, because a missing baseline that read as a pass
is how an instrument gives a clean bill of health forever.

## The flag counts in the header are part of the baseline

The header line reads, for example:

    # flags: burst 1, echo 0, salvage 0, repeat 0, contradiction 0

A run whose words match exactly but which picked up a flag the baseline did not
have is a regression, and is reported as one. That is the point of keeping the
flags: none of them is visible in the words.

## They are safe to commit, deliberately

Every value that could identify a station, an operator or a machine —
callsigns, station names, radio models, frequencies, build versions and bare
counts — is replaced with a placeholder before a baseline is written. What is
left is JJ Flexible's own wording and the order it arrives in, which is exactly
what the test is about.
