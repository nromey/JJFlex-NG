# Kerchunk Sidetone Pileup — the meters subsystem

**Status:** design captured 2026-08-14 from Noel, not started. Written before a
compaction so none of it lives only in a conversation.

---

## 1. The Live Meters tab cannot be navigated — and never could

Noel: *"The meters tab in the audio workshop can not be navigated."*

**Confirmed in code, and it is not a regression.** `MakeMeterLabel`
(`AudioWorkshopDialog.xaml.cs:2837`) builds each reading as a plain `TextBlock`
with an accessible name and `AutomationLiveSetting.Polite`. A `TextBlock` is not
focusable by default, and nothing sets `Focusable`. So **the entire Live Meters
tab contains zero tab stops.** Tab does nothing there because there is nothing
to land on.

The live-region setting means a screen reader may announce values as they
change, which is why the tab has seemed to "work" — but an operator can never
go and *ask* a meter what it says. The readings arrive when they arrive.

Two things follow, and they are separable:

- **The immediate fix** is the same idiom the Audio Devices page and the
  Workshop's own device and mic readings already use: read-only `TextBox`
  controls with proper labels. Focusable, arrowable at the operator's own pace,
  and readable on demand by the screen reader's own review commands. Consider
  whether the live-region setting should stay once the values are reachable —
  a polite live region on a value that changes twice a second is a lot of
  announcement for something the operator can now simply go and read. See
  `memory/feedback_speak_only_when_ui_does_not_convey.md`.
- **The larger question** is whether that tab is the right home at all. Noel:
  *"That's where you create different meters and manage them though that may
  need or want a menu I'm not sure."* Creation and management are a different
  activity from watching, and F6 already exists to move between sections. Decide
  deliberately rather than by accretion.

## 2. Much of what is being asked for already exists, unreachable

This is the important finding. **`MeterSlotConfig`
(`AudioOutputConfig.cs:418`) already carries per-meter:**

- `Source` — which meter this slot follows
- `Enabled`
- `Volume` — per-slot
- `Pan` — per-slot
- `PitchLow` / `PitchHigh` — the pitch range the value maps across
- `Waveform` — sine and others

And `MeterToneEngine` (`JJFlexWpf/MeterToneEngine.cs`) is the engine behind it.
`AudioOutputConfig` additionally persists `MeterSlots`, `MeterPreset`,
`MeterTonesEnabled`, `MeterMasterVolume`, `MeterSpeechEnabled`,
`MeterSpeechTimerActive`, `MeterSpeechIntervalSeconds`, `PeakWatcherEnabled` and
`AutoEnableOnTune`.

So Noel's *"we need to create some unique sounding tones for different meters so
that people can track them"* is **largely already modelled**: a distinct pitch
range plus a distinct waveform per slot is exactly what "unique sounding" means.

**The gap is the UI and the defaults, not the engine.** Same shape as the DSP
controls — see `memory/project_dsp_controls_design.md`, "engine COMPLETE, UI is
the whole gap." Before building anything, establish by running it:

- What the shipped default slots actually sound like, and whether two meters
  playing at once are genuinely tellable apart by ear.
- Whether the waveform choices are distinguishable at speech-adjacent volumes,
  or only in isolation.
- Whether pan alone carries enough separation, given many operators are on
  headphones and some have asymmetric hearing loss — Patrick is a tester on
  exactly that axis (`memory/patrick_bh_network_tester.md`).

**Design constraint for the defaults:** distinctness must not depend on stereo
separation alone, because mono listeners and single-sided-deaf operators lose it
entirely. Pitch range and waveform have to do the work; pan is an enhancement.

### Timbre is the identity axis, because pitch is already spoken for

Noel, 2026-08-14: *"I know there's waveforms but you can make some pretty spicy
sounds by adding different harmonics ... making it a tone that rings like a
rolling r, etc."*

This reframes the whole design, and the reason is architectural rather than
aesthetic.

**`PitchLow` / `PitchHigh` map the meter's VALUE across a range. So pitch is the
data, not the label** — it is moving all the time, by design, and it cannot also
be what tells you which meter you are hearing. Two meters can and will sit at
the same pitch.

**What is left to carry identity is timbre**, and timbre is the right tool for
it: telling a flute from a violin at the same pitch is effortless, and it stays
effortless when both play at once. That is precisely the task — tracking several
meters simultaneously.

The current `WaveformType` (`ContinuousToneSampleProvider.cs:10`) offers Sine,
Square, Sawtooth, SlowPulse, FastPulse and so on. That is a coarse ladder along
two axes at once: harmonic content (sine → square → sawtooth) and gross
on/off modulation (the pulses). Noel is pointing at the far richer space
underneath:

- **Additive harmonics with independent amplitudes.** Not "square = odd
  harmonics" but a specified partial series per meter — a hollow tone, a reedy
  one, a bell-like one. This is cheap to synthesise and enormously more
  distinguishable than three fixed waveforms.
- **Modulation as texture, not just on/off.** "Rings like a rolling r" is a
  trill — amplitude or frequency modulation at roughly 25-30 Hz, fast enough to
  read as *texture* rather than as pulsing. That is a whole dimension the
  current SlowPulse/FastPulse pair only gestures at, and it survives being heard
  underneath speech far better than a gap does, because it never goes silent.

**Two practical arguments for going harmonic rather than adding more fixed
waveforms:**

First, **a rich tone cuts through radio audio where a sine gets lost.** Band
noise is broadband; a pure sine sits in one bin and disappears into it. See
`memory/project_earcon_audibility_rf_environment.md` — this is the same problem
the earcons already had to solve.

Second, **it composes with pitch instead of fighting it.** A timbre stays
recognisable across the whole pitch range the value sweeps, so meter identity
survives the meter changing. A waveform-per-meter scheme has the same property
in principle, but three or four options run out immediately once there are more
than a handful of meters.

**Design rule that falls out: timbre identifies the meter, pitch carries its
value, pan enhances but is never load-bearing.** Write that down wherever the
slot model is documented, because it is the thing that keeps the vocabulary
coherent as meters get added.

**Evaluate by ear, with several playing at once.** A tone that is distinctive in
isolation is not the test — the test is four of them together while somebody is
talking. And it needs a human: this is the least inspectable design decision in
the app.

### Modulation is a second identity axis, and it is independent of pitch

Noel, 2026-08-14: *"You could also have tremolo type sounds for differentiation
... lot more than just changing waveform which is why I said it could be used in
our waterfall work."*

**The technically important property: modulation rate is perceptually orthogonal
to pitch.** A 6 Hz tremolo is recognisably the same 6 Hz tremolo on a low tone
and a high one. So like timbre, it survives the value sweeping the pitch range —
but it is a *separate* axis from timbre, not a variation of it.

That gives two independent identity dimensions on top of a pitch axis already
committed to carrying data:

- **Harmonic content** — hollow, reedy, bright, bell-like. What the tone is
  made of.
- **Modulation** — tremolo (amplitude) and vibrato (frequency), each with a rate
  and a depth. What the tone *does*. Slow throb, fast trill, the rolling-R
  buzz, dead steady.

They compose. Five distinguishable timbres against four distinguishable
modulations is twenty voices, which is far past what the meters need and
plausibly enough for the waterfall's categories. **Perceptual honesty about the
ceiling:** people reliably tell apart maybe five to seven modulation rates and a
similar number of timbre families — the space is combinatorially generous but
each individual axis is small. Design the alphabet, do not assume a continuum.

Also worth building on: **ham operators arrive with trained ears for tone
patterns.** A rolling-R trill is not far from a run of CW elements. This audience
has more relevant perceptual skill than a general one, and the vocabulary can be
more ambitious than it could be elsewhere.

### Why this matters more for the waterfall than for the meters

The meters problem is small: a handful of named quantities, each needing a
stable identity. The waterfall is qualitatively harder — a continuous spectrum
where things appear, move, strengthen and vanish, and the operator needs
position, strength, width and *kind* at once.

That is where the axes have to pay off:

- **Pitch** is the natural carrier of frequency position — the spectrum already
  is a pitch axis, so the mapping is nearly free and needs no teaching.
- **Amplitude** carries signal strength, equally naturally.
- **Timbre and modulation are what is left to carry KIND** — so an SSB voice, a
  CW signal and a digital transmission can sound categorically different rather
  than being three tones at different places.

**This is the whole reason to get the grammar right in the meters first.** The
meters are the small, safe place to invent a language that the signature feature
will then depend on. Inventing it twice, or inventing it under waterfall
pressure, is how the two end up incompatible and the operator has to learn both.

**Carry into the waterfall work.** Noel: *"may help with navigating the waterfall
as well."* The waterfall is the signature feature
(`memory/project_waterfall_signature_feature.md`) and it will need exactly this
vocabulary — a value mapped to pitch, tracked by ear, several at once. Whatever
sonification grammar the meters establish should be the same grammar the
waterfall speaks. Do not invent two.

## 3. The JJ key meter subsystem — `Ctrl+J`, then `M`

Noel's design, captured close to verbatim: *"basically a subsystem for meters
where you don't have to use the audio workshop."*

The intent is that meters become operable from the keyboard during operating,
not only configurable in a dialog. That is the right instinct — a meter you have
to open a window to consult is not a meter, it is a report.

**`M` enters meter mode.** Proposed sub-keys as given:

- **`M` then a number** — read that meter. Numbers index the meters.
- **`Ctrl+J R`, then numbers** — read instantly, without entering the mode
  first. The fast path for someone who already knows which meter is which.
- **`M T`** — tone. Turn tones on or off, change volume, change panning.
- **Something to read the names of each meter**, or name and value together, so
  an operator can learn the numbering rather than memorise it blind.
- **`M C`** — create, bringing up a creation modal.

**Open design questions, to settle before building:**

- Is meter mode *sticky* (press `M`, then bare numbers work until you leave) or
  *one-shot* (`M` then one number, then back)? Sticky is faster for riding
  several meters; one-shot is safer because bare number keys stop meaning
  anything else. The `Ctrl+J R` fast path suggests Noel wants both, with `R` as
  the one-shot.
- **How are numbers assigned, and are they stable?** A meter's number must not
  change because another meter was added or a mode changed — an operator builds
  muscle memory on those digits. Assign on creation order, persist per operator.
- What happens when a numbered meter is not currently available? Say so plainly;
  never silently read a different one.
- **`M C` opens a modal — from a leader chord, during operating.** That is a
  transmit-adjacent surface, so it must obey the Escape rule
  (`memory/project_dialog_escape_rule.md`) and must never be able to trap focus
  while the radio is keyed.
- Does the creation modal belong to the leader layer at all, or should `M C`
  simply take you to the Live Meters tab once that tab is navigable? Cheaper,
  one surface to maintain, and no second creation UI to drift.

**Two rules this must not break:**

- **No silent keystrokes** (`memory/project_no_silent_keystrokes_rule.md`).
  Every chord says what it did, including entering and leaving meter mode.
- **Prefer the leader over new flat hotkeys**
  (`memory/project_ctrl_j_leader_command_layer.md`) — which this proposal does
  correctly, and the layer is noted there as underused. This is a good use of it.

## 4. Suggested sequence

1. **Make the Live Meters tab navigable.** Small, unblocks everything else, and
   lets the tones actually be evaluated by someone who can reach the controls.
2. **Audit the existing tone defaults by ear** and fix distinctness before
   adding any UI for it. There is no point exposing controls for a vocabulary
   that does not yet distinguish anything.
3. **Decide creation's home** — Live Meters tab, or a modal, not both.
4. **Then the leader layer**, once the numbering and naming are settled, because
   the chords are worthless until a meter has a stable identity to address.

## 5. Not in scope here

The Peak Watcher, the meter speech timer, and `AutoEnableOnTune` all exist in
config already and are adjacent to this work. Note them, do not fold them in
until the above is settled.
