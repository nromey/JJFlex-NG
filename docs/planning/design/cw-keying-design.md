# CW Keying — Engine Design

**Status:** Implementation landing with this doc (prosign engine). On-air and input-adapter sections describe future work.
**Owner:** Noel / Claude
**Created:** 2026-04-15

---

## Problem

The MorseNotifier shipped in Sprint 25 for CW prosign notifications (AS on slow connect, BT on connect, SK on close). On first real listen, dits sounded weak and dahs sounded short. Root cause, on inspection:

1. **Envelope misuse.** The previous implementation fed each tone through `FadeInOutSampleProvider` and immediately called both `BeginFadeIn(~10 ms)` and `BeginFadeOut(durationMs − ~10 ms)`. NAudio interprets that `BeginFadeOut` argument as the fade-*out duration*, not a start-time, so the tone ramped down for roughly 90% of its supposed length with almost no sustain. Dits (60 ms at 20 WPM) were effectively a 6 ms rise followed by a 54 ms dying tone.
2. **Timing jitter.** Element sequencing used `await Task.Delay(ms)` between tones. Windows timer granularity is ~15 ms by default, which is enough to corrupt dit spacing at ~12 WPM and above.
3. **No single design point for reuse.** A future code-practice tutor or on-air PC-keyer would want the same clean tone shape. Nothing about Sprint 25's element-by-element interface was set up to be shared.

Noel's additional goals surfaced during design:

- **Reuse one engine across all CW audio contexts** (prosigns, practice, on-air). Don't scatter inconsistent quality.
- **Sine wave, broadcast-quality keying.** No clicks at element boundaries.
- **On-air CW eventually** via PC-generated audio pushed to radio TX, with latency compensated by a measured round-trip.
- **Haptic/visual output paths**, e.g. an iPhone that vibrates prosigns for deaf-blind users.

## Scope

### In

- **CW audio engine** — PC-side synthesis of shaped sine CW at correct PARIS timing.
- **Prosign notifier** — AS / BT / SK plus arbitrary text (mode names, digits).
- **Envelope shape** — raised-cosine, with configurable rise/fall time.
- **Output abstraction** — `ICwNotificationOutput` batch API that audio, haptic, and visual implementations can share.
- **Latency-compensated on-air path (future, sprint TBD).** PC generates audio, streams to radio TX via DAX, compensates for SmartLink round-trip. Radio mode: SSB with a 700-ish Hz tone. See "On-air" below.
- **Input adapters (future)** — keyboard typing, gamepad/touchscreen paddles, iambic logic.
- **Farnsworth timing and weighting (future)** — learning-mode concerns; same element builder extends naturally.

### Out

- **Radio's internal CW keying** (when the user sends via CWX text). That's Flex firmware's responsibility; we don't control its sidetone quality.
- **True RF-close-loop iambic keying** (per-element timing over SmartLink). The FlexLib `CWX` interface is string-based, and CW mode on FlexRadio does not accept audio input. See external reference below. On-air CW from PC audio must run via SSB-with-tone, not CW mode.

## Timing

Follows the PARIS standard. At N WPM:

- dot length: `1200 / N` ms
- dash: `3 × dot`
- intra-character gap (between elements of one character): `1 × dot`
- inter-character gap: `3 × dot`
- inter-word gap: `7 × dot`

**Farnsworth timing** — element speed fixed at the character speed, inter-character and inter-word gaps stretched to slow overall words-per-minute. Used in ARRL code practice. Not needed for prosigns; pluggable later via a separate `InterCharMs` / `InterWordMs` override on `MorseNotifier`.

**Weighting** — dot/dash ratio adjusted from the standard 3:1 to e.g. 2.8:1 or 3.3:1 to match an operator's preferred fist. Also pluggable later.

## Envelope

Raised-cosine attack and release, flat sustain in between:

```
attack(t)  = 0.5 · (1 − cos(π · t / riseTime))     for t ∈ [0, riseTime)
sustain    = 1.0                                    for the middle
release(t) = 0.5 · (1 + cos(π · t / fallTime))     for t ∈ [0, fallTime)
```

This is the community-standard minimum-click shape, equivalent to the raised cosine produced by QRP Labs' RC1 keyer chip and the ARRL Handbook's reference. Any other smooth monotonic ramp (Gaussian, half-sine, Blackman) would also eliminate clicks; raised cosine is chosen for simplicity and good spectral containment.

**Rise/fall time default: 5 ms.** This is the ARRL 10%–90% recommendation for keying speeds up to 30 WPM. Shorter (3 ms) preserves crisp feel and works fine for PC sidetone where spectral width is irrelevant. Longer (10 ms) is appropriate for on-air use where tight sidebands matter.

**Why not hard keying (no envelope)?** Audibly clicky. Spectrally ugly. Doesn't sound like a quality transmitter.

**Why not a steeper exponential decay?** Faster transient response, but click-y in practice. Raised cosine is the sweet spot.

## Implementation — prosign engine

### Class surface

```
JJFlexWpf/
  CwToneSampleProvider.cs     — single element (sine + raised-cosine envelope)
  ICwNotificationOutput.cs    — batch element-sequence interface, plus CwElement / CwElementType
  EarconCwOutput.cs           — audio implementation (mixer-submitted ConcatenatingSampleProvider)
  MorseNotifier.cs            — PARIS timing, element builder, public play API
  EarconPlayer.cs             — exposes MixerSampleRate + SubmitCwSequence for the audio path
```

### Element-batch interface

```csharp
public interface ICwNotificationOutput
{
    Task PlayElementsAsync(
        IReadOnlyList<CwElement> elements,
        int sidetoneHz, float volume, int riseFallMs,
        CancellationToken ct);

    void Cancel();
}

public readonly struct CwElement {
    public CwElementType Type { get; }   // Mark | Gap
    public int DurationMs { get; }
}
```

Output implementations decide how to render the sequence. The audio implementation builds one `ConcatenatingSampleProvider` of `CwToneSampleProvider` marks and `SilenceProvider.Take(ms)` gaps, submits to the alert mixer in a single call, and returns a `Task` that completes after the total duration. The audio engine drives inter-element timing — sample-accurate, no Windows timer jitter.

Future haptic or visual implementations get the same element list and render on their schedule.

### Element provider

`CwToneSampleProvider` is an `ISampleProvider` that, for a given duration, writes:

- Samples in `[0, riseSamples)` through the attack ramp.
- Samples in `[riseSamples, riseSamples + sustainSamples)` at amplitude 1.0.
- Samples in `[..., totalSamples)` through the release ramp.

Phase continuity across a single element is preserved by a running `_phase` field; across elements a new provider starts at phase 0, which is audibly fine because there's always a gap (silence) between marks of the same character.

### Cancellation — and why the next revision replaces it with a queue

MorseNotifier.Cancel() → EarconCwOutput.Cancel() → disposes the `CancellableCwProvider` wrapper, which makes its next `Read()` return 0 and the mixer drops it.

**Known defect (BUG-057 in `JJFlex-TODO.md`):** the cancellation happens eagerly whenever a new prosign fires, and the new engine has a ~50 ms mixer-buffer window before any audio is actually produced. If a second event (mode-change Morse, another prosign, etc.) fires during that window, the first event gets cancelled before it plays. In practice only SK reliably fires — it's the only event where nothing fires after it.

**Correct design is a queue**, not cancellation. Conceptually simple: one `Channel<CwSequence>` or equivalent per output, a single consumer loop that plays sequences to completion and dequeues the next. Incoming events Enqueue; the player serializes. This also sets up every future CW audio feature cleanly:

- **On-air CW message send** — typed characters Enqueue individual character-sequences; the queue naturally plays them in order at PARIS timing.
- **Iambic keyer** — paddle presses Enqueue individual elements; held paddles Enqueue alternating element streams. Same queue drains at wire speed.
- **Code practice** — whole lessons Enqueue once; queue drains at configured WPM.
- **Prosigns** — Enqueue on each notification event; queue plays in arrival order without clobbering.

Priority semantics stay simple: one queue, FIFO. If a future feature needs **preemption** ("urgent — kill what's playing and jump to this"), add a priority flag that causes Clear() before Enqueue. Default is never preempt — rapid events play in sequence.

Action item carried into the next session: replace the Cancel-and-Replace path in `EarconCwOutput` and `MorseNotifier` with a single-consumer queue. See BUG-057 for the three fix options; option (a) is this queue design.

## On-air CW (future)

**Architecture:** PC generates the audio using the same `CwToneSampleProvider` engine. Audio goes to the radio's TX input via a DAX TX audio stream. Radio is in SSB with a ~700 Hz tone (CW mode on FlexRadio does not accept audio — see reference). The transmission sounds like CW on the air and is legal. Operator hears sidetone from the PC in real time; the receiver on the far end gets it with whatever SmartLink-plus-internet latency adds.

**Latency compensation.** Round-trip to each radio measured by the Session Latency Service (see `session-latency.md`). Used to:

- Extend PTT-hold / VOX tail so the last element's audio clears before unkey.
- Give the operator a "connection quality" readout for calibration ("round trip 52 ms, jitter 3 ms — good for CW").

**Inputs.** Same engine, different input drivers:

- **Keyboard text** — user types; app sends via CWX (radio keys) or via PC-audio path. Either works.
- **Keyboard iambic emulation** — dedicated keys as left/right paddles, PC runs the iambic state machine, generates element sequence, feeds the engine.
- **Gamepad / touchscreen paddle** — thumbsticks or touch buttons as paddle inputs.
- **Physical paddle via USB HID** — same path.

**Iambic logic.**

- **Mode A** — squeezing both paddles alternates elements; releasing stops after the element in progress.
- **Mode B** — same, but releasing mid-last-element adds one more element of the opposite type (the "dit memory" / "dash memory").

Pluggable as an `IIambicKeyer` interface producing a stream of `CwElement`s from paddle events.

**Scope caveat.** This is Sprint 28+ territory at earliest, probably later. The engine is ready to support it; the input adapters and the latency service are the additional work.

## Code practice mode (future bonus)

Same engine, no radio involvement. UI picks text source (random letter/character groups, real-word practice, imported text file), picks WPM and Farnsworth ratio, plays. Useful accessibility feature for blind hams learning CW — there is very little accessible learning software in this space.

## Test plan

### Listen tests (manual)

- **Element timing** at 20 WPM: dit = 60 ms, dah = 180 ms, intra = 60 ms. Verify by ear (fluent CW op). Recording + spectral view confirms.
- **No clicks** at element boundaries. Dedicated listener (headphones). Spectral analyzer plot should show a clean envelope, no hard edges.
- **Prosign recognition** — AS / BT / SK all immediately recognizable to a CW-literate listener at 20 WPM.

### Timing verification (automated-ish)

- Record 10 seconds of a known pattern ("PARIS " repeated at 20 WPM). Total duration must be within 1% of 50 × 60 ms = 3000 ms for PARIS plus its word gap.
- Element durations measured from waveform zero-crossings should match PARIS ratios within ±2 ms at 20 WPM.

### Regression guards

- Verify `FadeInOutSampleProvider` is not reintroduced in the CW path (grep). Raised-cosine envelope is baked into `CwToneSampleProvider`; any future CW work should use it, not the fade provider.

## References

- [Key-clicks and CW Waveform shaping (ivarc.org.uk, PDF)](https://www.ivarc.org.uk/uploads/1/2/3/8/12380834/keyclicks_version_1.pdf) — ARRL-style analysis of envelope shape vs. bandwidth.
- [Raised Cosine Keyer (QRP Labs RC1)](https://shop.qrp-labs.com/rc1) — hardware reference that implements exactly the envelope used here.
- [ICOM 756PRO CW Envelope Adjustments](http://www.seed-solutions.com/gregordy/Amateur%20Radio/Experimentation/CWShape.htm) — practical envelope-shape measurements.
- [FlexRadio Community: Manually keying the transmitter in DAX mode](https://community.flexradio.com/discussion/8027031/manually-keying-the-transmitter-in-dax-mode) — confirms CW mode on FlexRadio doesn't accept audio input; remote keying goes through CWX (text) or audio-CW in SSB mode.
- [FlexRadio Community: How do I use Iambic Paddles remotely over SmartLink?](https://community.flexradio.com/discussion/8023326/how-do-i-use-iambic-paddles-remotely-over-smartlink) — community consensus that per-element remote paddle keying is not practical via SmartLink; text-mode (CWX) or local-hardware-keyer-with-RKI is the realistic path.
- [Continuous wave — Wikipedia](https://en.wikipedia.org/wiki/Continuous_wave) — background on CW keying and bandwidth.

## Living decisions log

- **2026-04-15** — Replaced tone-by-tone + Task.Delay design with element-batch + single mixer submission. Root cause of audibly wrong dits/dahs was `FadeInOutSampleProvider` misuse, not missing sine waves. Raised-cosine envelope implemented directly in `CwToneSampleProvider` rather than using NAudio's fade helper.
- **2026-04-15** — Declined to adopt `libcw` / other GPL reference libraries. NAudio-native C# implementation is small, MIT-compatible with the rest of the project, and easier to evolve.
- **2026-04-15** — On-air path will use SSB+tone (not CW mode) because FlexRadio's CW mode doesn't accept audio input. Verified against Flex community docs.
- **2026-04-15** — Farnsworth and weighting deferred to a future learning-mode sprint. Element builder designed so both plug in via a configurable `ditMs` / `dahMs` / `intraMs` / `interCharMs` / `interWordMs` set.
