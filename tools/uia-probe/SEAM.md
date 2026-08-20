# The Tier 3 seam — Track B presses, Track C observes the radio

Sprint 33. Track B owns the driving half; Track C owns the radio half.

**Status: Track C proposed the seam, and Track B CONFIRMS it.** No counter.
What follows records the agreement, plus four details that need a number rather
than a principle.

## Why the composition, and not two independent tests

A test tool that drives the radio on its own connection is a second MultiFlex
client inspecting its own per-client state. It would pass while proving nothing.
The only arrangement that proves the full chain is a key pressed in the real
running JJ Flexible, and the radio then asked whether it actually did the thing.

Track C's `--owner` filter is the part that makes this real rather than
decorative: it attributes each radio-side change to a client handle or a
fragment of the registered program name. Without it a change observed on Track
C's own connection cannot be distinguished from one it caused itself.

## Agreed: the whole-sweep correlation mode is the primary path

Track C prefers `RigSurface surface watch --seconds N --out radio-trace.txt`
across the entire key sweep on one connection, correlating offline. Track B
agrees, and it costs Track B nothing to support, because the sweep already emits
what correlation needs.

Every press in the sweep's JSON report carries:

- `chord` — what was pressed.
- `sentAtUtc` — immediately before the first keystroke leaves.
- `settledAtUtc` — when the app stopped reacting.
- `settleMs`, `quiesced` — how long that took, and whether it went quiet on its
  own or was still churning at the cap.
- `context` — which Home field the caret was on, and how it got there.
- `spoke`, `routed`, `uiChanges`, `verdict` — the app-side answer.

So the offline join is `sentAtUtc` against Track C's millisecond timestamps.

### Four details that need numbers, not principles

1. **300 seconds is too short.** The full sweep at `--risk safe,mutates` is 199
   chords and runs roughly 8 to 10 minutes, plus about 40 seconds of Home-layout
   walking before it starts. Ask for **900** to be safe, or run
   `--risk safe` first, which is 41 chords and finishes inside 2 minutes.

2. **Clocks have to agree.** Track B stamps UTC, ISO-8601 round-trip format
   (`DateTime.UtcNow.ToString("O")`), so `2026-08-20T18:04:11.2340000Z`. If
   `RigSurface` logs local time the join silently slips by the UTC offset and
   every correlation is wrong by hours while looking perfectly plausible.
   Confirm which Track C writes.

3. **A quiet window between presses.** Track B sleeps 150 ms between chords by
   default (`--between-ms`). If that is too tight to separate two radio changes,
   say what it should be and Track B will raise it — this is a one-flag change,
   not a redesign.

4. **`quiesced: false` means discard, not fail.** It says the app was still
   doing something when the settle cap expired, so the radio reading that
   follows may belong to the previous press. Rare, but it must not be scored as
   a mismatch.

## Also agreed, for smaller jobs

- **Per-key `mark` then `diff`** works directly with `jjprobe press --json`,
  which blocks until the app has settled and then exits. Sequence:
  `RigSurface surface mark --out before.json`, `jjprobe press --chord "..."
  --json`, `RigSurface surface diff --since before.json --owner JJFlex`. Roughly
  twice the wall-clock cost per key, so it earns its place on a short targeted
  list rather than a sweep.
- **`await --field ... --equals ... --timeout`** is useful but not required by
  Track B, because `jjprobe press` already blocks until settled. Reach for it
  when the radio-side change is expected to lag the app's own settling.

## What "settled" means, precisely

No UI Automation event from the target process **and** no new bytes in the app's
trace file, for `--quiet-ms` consecutive milliseconds (default 400), capped at
`--max-settle-ms` (default 2500).

That definition is why the primitive exists: the radio can only be asked after
the app has finished acting, and a fixed sleep either wastes run time or races
the app.

## Reading `verdict`

- `handled` — the app did something observable. Ask the radio.
- `unhandled` — the chord ARRIVED and the dispatcher logged that it had no
  command for it. Do not ask the radio; the answer is known and it is a
  Track B-side bug.
- `silent` — nothing observable at all. **Ask the radio anyway.** This is
  exactly the case where a radio-side change would prove the app is doing the
  work without telling anyone, which is its own finding and arguably the most
  interesting result available this sprint.
- `not-sent` / `skipped` — nothing happened. Do not ask the radio.

## Transmitting chords — Track B's gate, and Track C is not obliged by it

`jjprobe` refuses to press any chord classified `transmits` unless given
`--transmit-clearance FILE`: JSON carrying `issuedUtc`, `ceilingWatts`,
`measuredWatts` and `validForMs`, refused if stale or over ceiling.

**Track C is not being asked to write these.** The gate was built on a
coordinator message that turned out to be misrouted, and it is being kept
because it is cheap and defensible on its own terms, not because anything was
agreed. It is a local refusal, not a protocol obligation: if Track C wants to
compose a transmitting chord it can write the file, and if it would rather own
the safety question entirely, Track B will drop the gate on request.

Track B has no radio connection by design and cannot issue its own clearance, so
without a file those eleven chords are simply never pressed. They are:
`Ctrl+Space`, `Shift+Space`, `Ctrl+J, G`, `Ctrl+Enter` (Audio Workshop), and
`Ctrl+1` through `Ctrl+7`.

Related fact from another track, recorded here because it lands squarely on any
transmit composition: **there is no MOX status key on the wire.** Transmit state
has to be synthesised from `interlock state`. Anything waiting on `mox` waits
forever and then concludes the radio never transmitted.

## What Track B still needs back

- Which timestamp format `RigSurface` writes (detail 2 above).
- **Which radio properties map to which chords.** Track B knows what each chord
  is supposed to do in English, from the inventory `Description`; Track C knows
  what to read. That mapping is the one artefact neither track can write alone,
  and it is the only remaining blocker on the composed test.
- Whether 150 ms between presses is enough separation.

## What Track B is not doing

- Not connecting to the radio. Ever, in this tool.
- Not choosing which chords are worth composing. Track B can press any of the
  243 expanded chords; picking the ones with a radio-observable consequence is
  Track C's call.
- Not fixing anything it finds. Findings are recorded and triaged afterwards.
