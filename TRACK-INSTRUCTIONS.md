# Track B — Telemetry honesty

**Worktree:** `C:\dev\jjflex-b` · **Branch:** `bsr/track-b` · **Model:** Sonnet

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, section
"Track B — Telemetry honesty" including "Bench results 2026-08-16". Everything
below is specified there in more detail, including verified measurements.

## Theme

Every item is a readout that lies or is missing. **None of this is a feature** —
it is the instrument panel being wrong, which is what made a debugging session on
2026-08-14 so expensive.

## The work

**1. The trace flood — already fixed, verify it stayed fixed.** The fix landed
2026-08-16: `startOpusInputChannel`'s `TraceLine` moved below the already-started
guard. Confirm it is still correct.

**The open question that remains:** should `remoteAudioProc`'s main loop be paced
at all? It polls for Opus RX data, so a blind `Thread.Sleep` buys trace quiet at
the cost of receive latency. The loop already carries a fix of exactly this shape
in its `Disconnecting` branch. **Investigate and recommend; do not guess.**

**2. `hookTxMeters` re-subscribes on every call — a real latent bug.** It sets
`_txMetersHooked` only when it finds **both** SC_MIC and ALC. If a radio reports
one and not the other, the found one gets `DataReady +=` on **every subsequent
mic-meter event**, forever. Handlers accumulate without bound and every event
fires N times.

Measured on an 8600: both arrive together, two "NOT FOUND" passes then
"found, found". So it does not bite here — **it will on any radio reporting one
and not the other.** Fix by tracking the two subscriptions independently.

**3. Forward power rounds sub-watt RF to zero. THE HEADLINE.**

`SMeter` returns `int`; during transmit it converts dBm to watts and truncates.
Measured on the 8600 with the radio's power set to its **default of zero**:

- 17.0 dBm = 50 mW → displays **0 watts**
- 22.4 dBm = 174 mW → displays **0 watts**
- 18.7 dBm = 74 mW → displays **0 watts**

Real RF, every time, reading as *not transmitting*. `_PowerDBM` is a `float`, so
there is no integer-division bug — the `(int)` return is the whole defect.

**Fix:** `SMeter` is dual-purpose (watts on TX, S-units on RX) and its S-unit
callers legitimately want integers. **Add a separate `ForwardPowerWatts` (float)
rather than changing `SMeter`'s contract**, and switch the transmit display path
to it. Format with precision following magnitude — sub-watt gets decimals because
that is the entire point; a hundred watts does not.

**This gates the transverter bench session**, which lives at sub-watt drive.

**4. GPS status leads with the wrong fact.** Oscillator lock is load-bearing and
can disagree with the fix text during acquisition. Lead with lock; add the PPB
figure. **Read `memory/project_gps_gnss_oscillator_facts.md` first** — it
corrects an earlier wrong reading of the presence flags.

**5. Assert `mic_selection=PC` while PC TX audio runs**, and warn on divergence.
The one-shot set at opus-output start can be silently reverted by a later profile
load, and nothing re-asserts or warns. This is the arc's thesis: never stream TX
audio into a closed gate without saying so.

**6. Promote the meter-inventory diagnostic.** `FlexBase.traceMeterInventory`
currently reaches FlexLib's private meter list by **reflection** — right for a
diagnostic, wrong as a permanent fixture. Review it, keep it working, and record
in the plan what a real accessor would require (likely a documented FlexLib patch
noted in `MIGRATION.md`; `FindMeterByName` is public but the list is not).

## Papercuts you own

Any wording or trace-message papercut in the files you are already in. **A track
is not done until its papercuts are done.**

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.** Two tracks once merged with zero
  textual conflict and the build failed because one moved a method the other was
  told to reuse. Git cannot see that.
- `FlexBase.cs` is shared with Tracks A and C in **disjoint regions**. Additive,
  local edits are fine. Structural change is not yours to make here.
- Build from this worktree:
  `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track B: <description>`. Commit as you go.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. Forward power reports real sub-watt figures. The handler leak is
gone. GPS leads with lock and carries PPB. Mic-selection divergence warns. You
have written a recommendation on the `remoteAudioProc` pacing question rather
than silently changing it. Report what you did and anything you found but did not
fix.
