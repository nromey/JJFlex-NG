# Track I — Transmit audio conditioning

**Worktree:** `C:\dev\jjflex-i` · **Branch:** `bsr/track-i` · **Model:** Fable

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, "Track I —
transmit audio conditioning", and the "OUR DSP is better instrumented than the
radio's" subsection under Track D.

## What this is

**PC-side noise reduction and a podcast-style noise gate in the transmit path.**
Noel called it important; a named tester operates from a noisy New York
apartment.

**Smaller than "build TX DSP", because the hard parts largely exist:**

- **The insertion point is built.** `AudioStream` exposes `InputToneSource` (the
  tone generator injecting into the transmit callback) and `InputLufsMeter`
  (measuring what is actually encoded). The callback is **already structured for
  things to sit in it** — one injects, one observes. **A processor is a third
  thing in the same place, and it modifies.**
- **Order:** mic → tone injection → **processor** → LUFS meter → Opus. That
  preserves the property the meter was built for: measuring what genuinely goes
  out.
- **The NR engine exists** — `NoiseReductionProvider`, `NoiseProfiles`, a
  profiles dialog — on the receive pipeline. **The algorithm does not care which
  direction audio flows.** **YOUR FIRST QUESTION: does it take a float buffer, or
  is it welded to `RxAudioPipeline`?** That answer sizes this track. Report it
  early.
- **A gate is genuinely simple DSP** — threshold, attack, hold, release, range.

## Why this is worth building rather than using the radio's

The radio already has a good EQ, compander and speech processor; duplicating
those wins nothing. **What the radio cannot do is clean the room before its chain
ever sees the audio** — a fan, a computer, an air conditioner. So this is
**capture, clean, then sculpt**: one step past
`memory/project_capture_then_sculpt.md`.

## The gate — three things that decide whether it is loved or switched off

- **Attack must be fast, a few milliseconds.** Slow attack eats the front of
  words. This is the commonest complaint about every gate ever shipped.
- **Hold** is what stops it chattering during natural mid-sentence pauses.
  100–250 ms.
- **Do NOT gate to silence.** Full closure is fine in a podcast; **on SSB it can
  make the other operator think you dropped.** Attenuate 20–30 dB so there is a
  natural floor reading as "still here, not talking."

**Noise reduction goes BEFORE the gate.** NR lowers the floor, which makes the
threshold easier to set and less likely to clip quiet speech. Reversed, you are
gating against a noisy signal.

## The threshold is DERIVED, not a constant

**Do not ship a fixed dB threshold.** The app already detects the noise floor
(shipped with the Microphone Check). The recommended threshold is **floor + 6 to
10 dB**, computed for *this* room.

**That is something a podcast plugin cannot do**, because it does not know your
floor and makes you find it by ear. It also stays right when conditions change —
a quiet room at 3 AM and the same room with the air conditioner on are different
numbers.

## The residual monitor — the reason this track is buildable at all

**For the radio's DSP we get levels only. For OURS we own the audio at both ends,
so the residual is a subtraction:** `removed = input − output`, played to a
monitor.

**You can literally listen to what the noise reduction took out.** That answers
the question listening to the *output* never can: NR eating your voice sounds
"processed" but the missing parts are not there to hear. **Hear speech in the
residual and it is over-reducing.**

**It also proves the pathway is live.** Processing that is enabled but silently
bypassed sounds exactly like processing that is on and gentle — both clean, both
uninformative. **Bypassed produces actual silence in the residual**, not
something quiet. No reference, no calibration, usable while transmitting.

**Monitor the OUTPUT as well as the residual**, switchable between output,
residual and both — turning strength down until no voice appears in the residual
can leave far too much noise in the output. The right setting is a trade-off.

**And the remedy sits beside the diagnostic: the strength control must be LIVE
while monitoring.** Hear voice in the residual, turn it down, hear it again, gone.
Not apply-and-retest. This is what makes transmit-audio adjustment self-service,
which today it is not — currently the only way to learn you are over-processed is
someone on the other end telling you.

## UI shape

**Advanced mode exposes the parameters; basic mode recommends.**

- **Basic mode still needs a control** — advanced-only means someone in a noisy
  room cannot reach the gate at all. On/off plus at most a single strength that
  maps to a sensible combination underneath.
- **Each advanced control explains its own default** — *"attack is fast so it
  does not clip the start of your words."* This is what stops people changing
  values at random and then wondering why they sound odd.
- **"Recommended" must be restorable** — a reset action.

## Coordination

**Gate and NR settings belong to Track F's microphone profile**, not to the app.
A gate tuned for a headset in a quiet room is wrong for a desk mic in a noisy
one, and actively wrong when operating someone else's radio through Connect.
**Agree the shape with F rather than inventing your own store.**

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track I: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. A processor hook exists in the transmit callback in the right
position. A gate works with a floor-derived threshold and does not clip word
onsets. The existing NR runs on the transmit path, or you have reported precisely
why it cannot. The residual monitor plays what was removed, with a live strength
control beside it. Report the NR-reusability answer early — it sizes everything
else.
