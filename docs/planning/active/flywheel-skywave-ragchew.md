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

## The constraint that decides the whole design: JAWS does not have "held"

Added 2026-08-25, from a Freight Fate bug report and the measurement that
followed it. This is not a detail to accommodate later — it invalidates the
obvious design, so it goes before the design.

**A JAWS user reported that holding Up did not make the truck go.** The cause,
measured with `tools/key_probe.py` in Freight Fate on 2026-08-24:

- **NVDA** passes a key it has no script for straight through, unadulterated. A
  held arrow is a real hold: one key-down, OS auto-repeat at roughly 33 ms, one
  key-up when you let go.
- **JAWS** does not. It synthesises **key-down/key-up PAIRS**. The first repeat
  pair arrives at the Windows delay — about 512 ms — and the rest roughly
  **250 ms apart** (measured 242 to 272), nowhere near the OS repeat rate. JAWS
  runs an arrow script that takes that long per key, and the repeats queue
  behind it.

So the same physical action — holding Up — reaches the application as a
continuous hold under one screen reader and as four discrete taps a second
under the other. **There is no "how long have you been holding" to read under
JAWS. It is always zero.**

Freight Fate's fix is worth understanding because we may need the same trick:
it learns the spacing from the pairs themselves (from the second repeat on,
synthetic pairs only, the largest of the last eight) and sizes each synthetic
repeat's pulse to that plus grace, so a queue of taps reads as one hold. Until
it has learned a spacing, repeats get the fresh pulse, so the first hold of a
session cannot stutter either. **The price is that letting go reads about a
third of a second late under JAWS**, and their changelog says so out loud.

**Do NOT hardcode 250 ms.** That is one machine, one JAWS version, one script
speed, on one day. Learn it or measure it; the number is evidence, not a
constant. Same lesson as every other number in this project.

### What this means for SilkTune

The keyboard design below said "hold accelerates toward a cap." That is a
design that works on NVDA and fails on JAWS, and we would have shipped it
without knowing.

**Noel's answer, 2026-08-25, and it is better than the thing it replaces:**
drive the flywheel from PRESSES, not from hold duration.

- **Each press of Up adds velocity.** Press it repeatedly to spin up. Under
  NVDA the OS repeat supplies the presses; under JAWS the synthetic pairs do.
  Neither path is asked how long anything was held, so both behave the same.
- **A brake key sheds velocity fast** — Shift (either one), or Space, or
  whatever the operator binds. This is the part that makes it controllable:
  when the sweep is running away, you need a way to stop it that is quicker
  than waiting for the coast.
- **Releasing everything coasts to zero** on the damping coefficient, as
  before. The brake is a shortcut through the coast, not a replacement for it.

Freight Fate has the same shape — accelerator, and a brake that stops you
sooner than lifting off — which is why the comparison Noel drew at the start of
this plan turned out to be load-bearing rather than decorative.

**The general rule, worth stating because it will come up again:** an input
design that asks *how long has this been held* works on one screen reader. One
that asks *how many times has this been pressed* works on both. For an
accessibility-first application that is not a trade-off to weigh, it is a rule
to follow.

### Where else this bites, already found

`MainWindow_PreviewKeyUp` releases push-to-talk on Space up. Under JAWS the
synthetic pairs mean a held Ctrl+Space would key and unkey roughly four times a
second, and the first spurious key-up would unkey an operator who is still
speaking. That is a transmit fault rather than a feel fault, so it is tracked
separately — but it is the same root cause, and it is live today.

## Part 1 — Flywheel physics

Cheapest, delivers most of the value, needs no DSP whatsoever. Build first.

Angular velocity with a damping coefficient. Input impulses add torque. Input in
the opposite direction applies a **braking torque** rather than reversing
instantly — that is how you stop a real flywheel VFO, and it is what makes the
control feel like an object rather than a variable.

- **Keyboard:** each PRESS of the tune key adds velocity toward a cap, the brake
  key sheds it quickly, releasing everything coasts to zero, and tap-opposite
  also brakes. Press-driven rather than hold-driven, for the JAWS reason above —
  this is the one line of this plan that the 2026-08-25 finding rewrote.
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

### Correction: FlexLib has no knob support

Verified 2026-08-23, with a positive control on the search (59 files in
`FlexLib_API/` match "slice", so the grep works). Searching the vendored tree for
`flexcontrol`, `knob` or `SerialPort` returns exactly one file — `ComPortPTT`, a
serial-PTT sample unrelated to the knob.

**Everything we have for the knob is Jim's own work**, including the protocol:
`JJFlexControl/Serial.cs` opens a `SerialPort` at 9600 baud and decodes the
device's event bytes itself. There is no vendor layer underneath to fall back on,
which matters for scoping any rewrite.

### Noel's ruling, 2026-08-23: rewrite the abstraction

> "I think it really should be re-written from the ground up... the sky's the
> limit as far as what each button does, what the presses do and how JJ Flex
> interacts. JJ didn't have an app that was very interactive. He had basically
> one form with an edit box for tuning and settings... I think we could take some
> of the features but write a better flex knob abstraction for accessibility and
> then just simply implement it better."

**The framing that makes this right rather than merely preferred:** Jim's knob
design was correctly scoped to Jim's application — a single form with an edit box
for tuning and settings. A flat list of twelve actions is a good design for an app
with one surface. It is the wrong design for an app with modes, dialogs, a leader
layer, Home regions and a Command Finder. The constraint changed, not the quality
of the original judgement.

#### What to keep

**`Serial.cs` and the device event decode.** It reads bytes off a COM port and
produces fourteen discrete events. That is protocol, the device speaks what it
speaks, and it is the one part that definitely works. Rewriting it buys nothing
and risks the only solid ground we have.

**The concepts** from `Action_t`: named, described, remappable actions with a
value-readback delegate. Those ideas are sound. The implementation is not what
survives — the shape of the idea is.

#### What to replace, and the reason it is not just cleanup

`FlexKnob.vb`, the flat action list, and the WinForms configuration dialogs.

**The load-bearing architectural point: Jim's knob owns its own action registry,
separate from the application's command system.** The app already has a command
registry behind the keyboard, the leader layer, the Command Finder and F1 help.
A knob with a parallel registry means every command must be registered twice, and
the two lists will drift — which is this project's dominant defect class, invited
in by design.

**The rewrite should make the knob a fourth input ROUTE into the existing command
registry, not a separate action system.** Do that and the knob inherits Command
Finder discoverability, F1 help, the leader-layer vocabulary and the keyboard
audit machinery for free, instead of needing parallel versions of each. It also
lands directly on #185, which is about testing every action by every route a user
can reach it; the knob becomes one more route rather than an untested island.

### The hole this replaces, precisely located — this is #198

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

- **SilkTune** is the name, chosen by Noel 2026-08-25. It names the feel, it is
  ours, and it is short enough to be a menu item and a setting label without
  being cut down.
- **Flywheel** stays as the word for the PHYSICS inside it. Every ham who has
  touched a Collins or a Drake knows precisely what it means, so it is the right
  word in help text and in the plan — but SilkTune is what the operator turns
  on.
- **Smooth Tune** for the audio layer, on by default, because it improves
  ordinary slow tuning too and is not only a sweep feature.
- **Personality goes in the preset names, not the mode name.** "Smooth Operator"
  is a great preset and a poor menu item. Presets are also where rig-feel
  homages belong.

## Open questions

- ~~Does Freight Fate have a physics core worth lifting, or at least design
  knowledge about making momentum legible through audio?~~ **ANSWERED
  2026-08-25, and not where anyone expected.** What Freight Fate had worth
  lifting was not physics — it was INPUT: the discovery that JAWS does not
  deliver held keys at all, and the accelerator/brake shape that works without
  them. See the JAWS section above. The physics question stands open and is now
  the smaller half.
- The radio's command rate limit. Measure it.
- Whether CW should get smoothing, a different smoothing, or none.
