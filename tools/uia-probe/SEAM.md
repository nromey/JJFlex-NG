# The Tier 3 seam — Track B presses, Track C observes the radio

Sprint 33. Track B owns the driving half; Track C owns the radio half. This file
is Track B's side of the contract, written so the merge does not discover two
different ideas of it.

## Why the composition, and not two independent tests

A test tool that drives the radio on its own connection is a second MultiFlex
client inspecting its own per-client state. It would pass while proving nothing.
The only arrangement that proves the full chain is: **a key pressed in the real
running JJ Flexible, and the radio then asked whether it actually did the
thing.** Noel's framing — *"exercise a hotkey or action, see if the radio did
what it was supposed to."*

So neither half is a test on its own. Track B can prove a chord reached the
dispatcher and the app spoke; it cannot prove the radio changed. Track C can
prove the radio changed; it cannot prove a keystroke caused it.

## The primitive

```
jjprobe press --chord "Ctrl+J, N" --window "JJ Flexible" --json
```

One JSON object on stdout, nothing else. Non-zero exit means do not proceed to
the radio question.

Track C should shell out to it rather than link against it. The probe has no
FlexLib reference and Track C's observer does; keeping them in separate
processes means neither drags the other's dependency graph around, and the
contract survives either side being rewritten.

## The three-step protocol

1. **Track C reads the radio property BEFORE.** Not a cached value — a read.
2. **Track C calls `jjprobe press`** and waits for it to exit.
3. **Track C reads the radio property AFTER**, and asserts the change.

Step 2 blocks until the app has settled, which is the whole reason the
primitive exists. `settleMs` and `quiesced` in the result say how long that
took and whether the app went quiet on its own.

## What "settled" means, precisely

No UI Automation event from the target process **and** no new bytes in the app's
trace file, for `--quiet-ms` consecutive milliseconds (default 400), capped at
`--max-settle-ms` (default 2500).

This matters to Track C because the radio can only be asked after the app has
finished acting, and a fixed sleep either wastes run time or races the app. If
`quiesced` comes back **false** the app was still churning when the cap expired
— treat the radio reading that follows as unreliable rather than as a failure.

## Fields Track C should read

- `verdict` — `handled`, `unhandled`, `silent`, `not-sent`, `skipped`.
  - `handled` — proceed to the radio question.
  - `unhandled` — **the chord arrived and the dispatcher had no command for it.**
    Do not ask the radio; the answer is already known and it is a Track B bug.
  - `silent` — nothing observable at all. Ask the radio anyway: this is exactly
    the case where a radio-side change would prove the app is doing the work
    without telling anyone, which is its own finding.
  - `not-sent` / `skipped` — nothing happened. Do not ask the radio.
- `settleMs`, `quiesced` — timing, as above.
- `spoke` — what the app said. Worth recording next to the radio answer: an app
  that says "Noise Reduction on" while the radio says off is the most valuable
  single result this sprint can produce.
- `routed` — what the dispatcher logged.
- `sentAtUtc`, `settledAtUtc` — for correlating with radio-side timestamps.

## The transmit gate — Track C has to write the vouch

Any chord classified `transmits` is refused unless `--transmit-clearance FILE`
points at JSON that Track C wrote:

```json
{
  "issuedUtc": "2026-08-20T18:04:11.2Z",
  "ceilingWatts": 1,
  "measuredWatts": 1,
  "radio": "FLEX-8600 serial ...",
  "validForMs": 10000
}
```

`measuredWatts` must be **read back from the radio**, not the value Track C sent
to it. The probe checks freshness and refuses anything older than `validForMs`.

This exists because of Track G's finding on 2026-08-20:
`FlexBase.setupFromScratch()` sets `RFPower = 100` unconditionally. It only runs
when no saved global profile is found, so it will not fire on the current bench
radio — but a harness keying a radio that has been reset, or one it has never
seen before, can find itself at full power with nothing having asked for it.

The split is deliberate and neither half can route around the other: the probe
cannot see the radio, so it cannot issue its own clearance; Track C can see the
radio, but cannot press a key without going through the probe. **A ceiling you
set is a wish. A ceiling you read back immediately before keying is a ceiling.**

The eleven transmitting chords, so Track C knows the full list up front:
`Ctrl+Space`, `Shift+Space`, `Ctrl+J, G`, `Ctrl+Enter` (Audio Workshop), and
`Ctrl+1` through `Ctrl+7`.

## Things Track B needs from Track C

- **Confirmation that shelling out is acceptable**, or a request for an
  in-process API instead. Track B's preference is the process boundary, for the
  dependency reason above.
- **Which radio properties map to which chords.** Track B knows what each chord
  is supposed to do in English, from the inventory Description. Track C knows
  what to read. That mapping is the one artefact neither track can write alone.
- **Whether the clearance file shape above is workable**, and what `validForMs`
  should be in practice.

## What Track B is NOT doing

- Not connecting to the radio. Ever, in this tool.
- Not deciding which chords are worth composing. Track B can press any of the
  243 expanded chords; choosing the ones with a radio-observable consequence is
  Track C's call.
- Not fixing anything it finds. Findings are recorded and triaged afterwards.
