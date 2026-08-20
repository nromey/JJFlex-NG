# Sprint 33 Track D — the analyzer's fact layer, against a live radio

**Worktree:** `C:\dev\jjflex-33d` · **Branch:** `sprint33/track-d`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A. Starts when Track C reports its snapshot helper committed.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Why this is its own track

**The analyzer is what Don will actually use to find out why his transmit audio
is broken, and a confidently wrong answer is worse than no answer at all.** He is
remote, at a radio that lives at someone else's house, with limited ability to
iterate. If the analyzer tells him stage 7 is fine when stage 7 is where it dies,
it has actively cost him.

## The gap, stated precisely

**The rules engine is already built and genuinely well tested.**
`Radios/ChainChecks/` holds `ChainAnalyzer.cs`, `DiagnosticFact.cs`,
`DiagnosticRules.cs`, `RuleSetLoader.cs`, `TxChainFacts.cs` and the shipped
ruleset `tx-chain-rules.txt`. `Radios.Tests/ChainAnalyzerTests.cs` carries about
30 tests covering exactly the property that matters — three-state honesty:

- `A_check_that_could_not_be_made_is_never_counted_as_one_that_passed`
- `An_unreadable_condition_beats_a_false_one_within_the_same_rule`
- `An_absent_meter_does_not_fire_the_rule_written_for_a_silent_one`
- `A_healthy_radio_is_never_given_a_clean_bill_of_health_while_stages_are_unseen`

**None of that touches fact collection.** `TxChainFacts.cs` reads `rig.MicGain`,
`rig.ForwardPowerWatts`, `rig.SWR`, `rig.MicSource`, `rig.TXSliceLetter`,
`rig.TXMode`, `rig.XmitPower`, `rig.PttSourceName`, `rig.CurrentMicProfileName`
and others off `FlexBase`. The engine is provably correct GIVEN those facts.

**Whether each of those wrappers returns the truth on a live 8600 has never been
checked by anyone.** That is your job.

## What to do

**For every fact the analyzer collects, verify it against the radio's actual
state.** Change the thing on the radio, confirm the fact moves with it, confirm
it moves to the RIGHT value and in the right units.

Start by enumerating the full fact list from `TxChainFacts.cs` — do not work from
this document's partial list, read the file. Then for each one, decide how to
prove it, and prove it.

**A fact that reads plausibly but is wired to the wrong source produces a
confident wrong diagnosis, and nothing downstream can catch it.** Task #139
already suspects precisely this: that the TX Peak Watcher may be reading the
amplifier-jack ALC rather than the real transmit drive. If that suspicion is
correct, the analyzer inherits the same wrong number. **Settle #139 as part of
this track** — it is the same question about the same meter.

**Also verify the three-state honesty survives contact with hardware.** Over a
local connection some facts are readable that are not readable over SmartLink,
and the analyzer's whole credibility rests on saying NOT OBSERVABLE rather than
guessing. Confirm that a fact which genuinely cannot be read comes through as
unreadable and not as a plausible default — a `0.0` that means "no reading" and a
`0.0` that means "actually zero" are the same bits and completely different
diagnoses.

`JJFlexWpf/TxChainPcFacts.cs` collects the PC-side facts. Same treatment.

## WHOSE FACT IS IT? — the MultiFlex trap that would invalidate this whole track

If you connect your own `FlexBase` to the 8600 to read facts, **you are a second
MultiFlex client with your own `ClientHandle`**, and that changes what your reads
mean.

Some of the facts in `TxChainFacts.cs` are **GLOBAL station state** — mic gain,
mic source, mic profile, compander, transmit power, forward power, SWR. Reading
those from your own connection is legitimate: there is only one of them and it
belongs to the radio.

Some are **PER-CLIENT** — most obviously `rig.TXSliceLetter` and `rig.TXMode`,
which describe *which slice is yours*. Read from your own connection, those
describe YOUR client, not JJ Flexible's. **The fact would come back plausible,
consistent and completely irrelevant to what the analyzer will report for a real
operator.** Nothing downstream can catch that, and it looks exactly like success.

**So classify every fact before you verify it: global or per-client.** Track C is
producing the same classification for radio state generally — share one list, do
not build two. For per-client facts, the only honest verification observes the
APPLICATION's client, which means composing with Track B rather than reading from
a connection of your own.

This is the operator-versus-station-state distinction one layer down: it governs
a test client exactly as it governs a guest operator.

## Reuse Track C's snapshot helper — and the rule about that

Track C owns snapshot-and-restore for radio state. **Use its helper. Do not grow
a second one.**

**If you conclude that helper should move, be renamed, or change signature,
REPORT IT — do not do it.** Sprint 32 lost a build to exactly this failure: one
track was told to reuse a symbol, another track moved it, both merged with ZERO
textual conflict, and the result would not compile. Git cannot see that class of
collision and will not warn you.

Track C commits the helper early and reports its exact signature. Work from that.

## Coordinate on the radio

Track C is exercising the non-transmitting surface on the same radio. Noel may
also be operating it. Anything that changes radio state collides — coordinate
before a run that takes the radio.

Noel is blind and at the keyboard. Full stop and ask before a run; full stop and
report when it finishes. One authorisation covers one run.

## Transmitting

**Only what is explicitly sanctioned.** No antenna is connected. 1 watt is
acceptable for a smoke test, used sparingly. Several analyzer facts only exist
while transmitting — `rig.ForwardPowerWatts`, `rig.SWR`, the mic level the radio
actually hears. Those are exactly the interesting ones, so plan the minimum set
of short low-power keyings that verifies them, ask once, and get them all in a
single run rather than keying repeatedly.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Do not touch files outside your worktree.
- **Do not fix what you find** beyond wiring corrections you can prove. A wrong
  meter source, proven wrong, is a finding worth fixing; a suspicion is a task.

## Commits

`Sprint 33 Track D: <description>`.

## Completion report

State: every fact, and whether it was verified true, verified WRONG, or left
unverified with the reason. The #139 verdict. Whether unreadable facts genuinely
arrive as unreadable rather than as plausible zeros. And anything you concluded
should change outside your worktree — reported, not done.

---

## AUTHORISATION IS BROKERED — do NOT ask Noel directly

**Decided by Noel, 2026-08-20.** Five tracks want either the radio or the live
desktop, and five agents interrupting him independently would be worse than the
collision the handshake exists to prevent.

**So: when you are ready for a run that needs the radio or drives the UI, STOP
and report "ready for a radio run" (or "ready for a UI run") to the orchestrator,
with exactly what you intend to do and roughly how long it takes.** Do not ask
Noel. Do not proceed on your own initiative.

The orchestrator batches ready tracks, asks Noel once, runs them back to back,
and reports done. You will be told when your run is authorised and when it is
over.

**Priority when tracks contend for the 8600: G first** — a build going to Don
depends on its answer — then C, then D, then K.

**While you wait, keep working.** Do everything that does not need hardware:
build the harness, write the code, reason it through. Arrive at your run with the
maximum settled in advance, because run time is the scarce resource, not compute.
