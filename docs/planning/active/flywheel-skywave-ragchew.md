# Flywheel tuning, smooth tune, and the knob

**Filed:** 2026-08-23, from Noel's idea the same night.
**Tasks:** #198 (knob is silent), #199 (flywheel + smooth tune), #200 (knob leader layer).
**Sequencing, ruled by Noel:** after the test harness is real and after Don has a
build that tells him what his radio is doing. Not before. #198 is the exception —
it is small, it needs only the knob on the desk, and nothing else here is worth
judging until it is done.

---

## The problem, stated properly

Tuning an SDR is choppy, and it is slow to search.

The choppiness is the obvious half. The search speed is the half that matters.
A sighted operator lost band-sweeping when SDRs arrived too — but got a waterfall
in exchange. A blind operator got nothing. Every fast-search technique an analog
operator had lived in the wrist: fling the VFO, hear something go by, brake, back
up. That is a motor skill, it was never replaced, and the industry optimised it
away because the people specifying the software could see.

That is the actual opportunity here, and it is why this is worth building rather
than being a nicety.

## Prior art, and what is genuinely new

Worth being precise, because it changes which parts are risky.

**Continuous tuning is near-universal in PC SDR applications — but nobody built
it.** SDR#, HDSDR, SDR Console, GQRX, CubicSDR, SDRuno, Quisk and the
PowerSDR/Thetis lineage all receive a wide IQ stream and perform the final tuning
as a software NCO inside the captured window. Within that window there is no
hardware retune, so there is no discontinuity to smooth. It is an emergent
property of the architecture, not a feature anyone implemented. Push outside the
window and those applications glitch exactly as we do.

**Our architecture is the one that has the problem.** The normal slice path
delivers demodulated audio over the network and the radio does the tuning. Every
step is a hardware retune and an audio discontinuity. That is why the choppiness
is conspicuous here and invisible to someone running a dongle on SDR#.

So the three parts carry very different risk:

- **The IQ path is proven, not speculative.** It is what the rest of the SDR
  world already does. There is prior art to learn from rather than invent.
- **Smoothing a hardware-stepped tune over an audio path** is the part with no
  precedent I can find. It is also the only version that can work over SmartLink,
  where IQ bandwidth is impractical — so it is the version Don gets.
- **A momentum or flywheel physics model** — configurable inertia, coast,
  braking — appears in no SDR application I am aware of. Mouse-wheel acceleration
  exists in several (faster spin, bigger steps), but that is adaptive step size,
  not momentum. This is where the differentiation lives, and it happens to be the
  cheapest part to build.

That claim is from knowledge, not from a survey. Worth an hour of real research
before it goes anywhere near marketing copy.

---

## Part 1 — Flywheel physics

Cheapest, delivers most of the value, needs no DSP whatsoever. Build first.

Angular velocity with a damping coefficient. Input impulses add torque. Input in
the opposite direction applies a **braking torque** rather than reversing
instantly — that is how you stop a real flywheel VFO, and it is what makes the
control feel like an object rather than a variable.

- **Keyboard:** hold accelerates toward a cap, release coasts, tap-opposite brakes.
- **Physical knob:** the radio has *already* moved by the time we see the event.
  So estimate rate from the event stream and keep tuning past where the knob
  stopped, decaying. That is the flywheel, and it falls out naturally.

Two extensions to design in from the start rather than bolt on later:

- **Band edges as hard stops with a distinct sound.** You physically cannot coast
  out of band. A safety feature wearing a feel feature's clothes.
- **A friction change when crossing an occupied channel**, so a sweep is felt as
  well as heard.

## Part 2 — Adaptive step size

Structural, not a detail. High angular velocity **requires** coarse steps,
because the radio will not accept an unbounded command rate.

**Measure the command rate limit rather than designing the curve around a
guess.** A bench task, and it gates the physics tuning.

Coarse steps then require Part 3 to not sound terrible. That is the dependency
chain: physics needs adaptive steps, adaptive steps need smoothing.

## Part 3 — Smooth tune, the audio continuity

Formulate as an **error signal**, not as crossfading. That is what makes it
tractable rather than hand-wavy.

Let the *virtual* frequency be where a continuous VFO would be right now, and the
*actual* frequency be the radio's staircase. Their difference is a sawtooth
**bounded by half a step**. Apply a continuously-varying single-sideband
frequency shift equal to its negative: the signal slides smoothly, and at each
step the error and the applied shift both reset by exactly one step, so the ear
hears a clean ramp.

**The consequence that makes this cheap:** the shift ever applied is bounded by
half a step — plus or minus 50 Hz at 100 Hz steps. That is a trivial Hilbert or
Weaver translation, the same operation the radio's own down-converter performs.
It is emphatically **not** time-stretch pitch shifting, which is where all the
ugly artifacts live.

A crossfade is still required, but only for the **content** change: at each step
a sliver of new spectrum enters one edge of the passband and a sliver leaves the
other. With a 2.4 kHz passband and a 100 Hz step, 96 percent of the content is
identical, so a 20 to 30 ms overlap covers it. That overlap is the added latency,
and it is noise beside the 50 to 200 ms SmartLink already costs.

**Higher-fidelity path, local network only:** take a wide IQ stream, do the last
few kHz of tuning in our own NCO, and park the radio. Tuning becomes genuinely
continuous rather than smoothed, and the radio retunes only at window edges —
every few kHz instead of every step. Pairs with #10 (receiver simulation on IQ
playback) and #57 (low-resolution DAX IQ). Noel's point stands: doing this buys
IQ-manipulation experience those tasks need anyway, so it is not a detour.

### Gating that is mandatory, not polish

- **Hard off for data modes.** FT8 and its relatives would fail to decode against
  a moving frequency reference, and that failure would be silent and baffling —
  the worst shape of bug this project produces.
- **CW needs its own ear test.** A shifting pitch on a CW note is far more
  audible than on voice. It may be lovely. It may be seasick-making. A question
  for ears, not for analysis.

---

## The knob

### What already exists, verified 2026-08-23

Better than expected. `JJFlexControl/` is a full project, referenced from
`JJFlexRadio.vbproj`, talking to the device over a virtual COM port.

- **Fourteen discrete events** (`FlexControl.cs`, `ValidKeys`): knob down, knob
  up, knob press short/double/long, and three buttons times short/double/long.
- `Action_t` is a **named, described, remappable** action with persistence
  (`ConfiguredActions`) and a setup dialog.
- Twelve actions are registered in `FlexKnob.vb`; ten gestures are mapped, so
  **nine button gestures exist and several are free.** The budget for modal
  layers is already there.

### The hole, precisely located — this is #198

`Action_t` carries a delegate whose documented purpose is *"Provides the current
value."* Its **only consumer in the codebase** is `ShowKeysAndActions.cs:53`,
setting `ValueBox.Text` — a text box, visible only while that dialog is open.
And `FlexKnob.vb` contains **zero** speech or earcon calls.

Four of the twelve actions supply a value function at all. The other eight have
nothing to report even visually.

So the knob silently changes radio parameters. The readback hook was designed and
wired to a screen. For a blind operator it does nothing.

**This is why the knob reads as low-utility.** It is not a judgement about knobs;
it is a control with no feedback path, which is the anti-pattern this project
exists to avoid, arriving through hardware instead of software. The fix is a
wire, not a design: route the value delegate through the speech pipeline, add
value functions to the eight actions lacking them, earcon on mode change.

Mind the rate. Knob turns are high-frequency, so frequency announcements must not
queue — the identical defect to #182 in CW notifications.

### A leader layer on the knob — this is #200

This is the Ctrl+J leader layer, in hardware. Press a button to enter a mode; the
knob's meaning changes; another press cycles which parameter within the mode;
long-press exits. Same architecture, same discoverability problem, same solution
— so it must reuse the leader-layer vocabulary and help machinery rather than
growing a parallel one.

Noel's worked example: a button enters filter mode, another steps through
lower-edge and upper-edge adjust, and the knob then drags that edge.

**The genuinely novel part:** apply Part 3's treatment to filter edges. A filter
skirt that slides continuously rather than stepping is something a blind operator
can tune by ear the way a sighted one does by eye. Passband tuning becomes an
audible gradient instead of a series of jumps.

Also required: the current parameter must be speakable on demand **without
changing it**. Otherwise the operator has to move a control to discover which
control they are holding.

---

## Naming

- **Flywheel** for the mode. Every ham who has touched a Collins or a Drake knows
  precisely what it means, and it names the feel rather than the implementation.
- **Smooth Tune** for the audio layer, on by default, because it improves
  ordinary slow tuning too and is not only a sweep feature.
- **Personality goes in the preset names, not the mode name.** "Smooth Operator"
  is a great preset and a poor menu item. Presets are also where rig-feel
  homages belong.

## Open questions

- Does Freight Fate have a physics core worth lifting, or at least design
  knowledge about making momentum legible through audio? Noel drew the comparison
  himself, and if "how does a blind operator perceive inertia" is already solved
  over there, the expensive half is already paid for.
- The radio's command rate limit. Measure it.
- Whether CW should get smoothing, a different smoothing, or none.
