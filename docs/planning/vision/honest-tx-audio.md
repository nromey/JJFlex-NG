# Honest transmit audio (codename: splatter-coach-keydown)

**Status:** arc consolidated 2026-08-11 from the two-day PC-audio TX investigation
and Noel's design conversation the same day. Root cause SOLVED and the first
fixes SHIPPED; the rest is a build queue. This file is the organizing document —
detailed design capture lives in `docs/planning/active/research-queue.md` (the
2026-08-10/11 audio entries), which stays the source of record for rationale.
This file says **what we are building, in what order, and what is already done.**

**Branch state at time of writing:** the three verified fixes are on
`origin/main` and `origin/track/flexlib-4220`. Two further commits
(`b4bd721f`, `a60f54e7`) are on `honest-tx-audio` only and are compile-verified
but **not radio-tested**.

All file references verified on `honest-tx-audio` 2026-08-11. Line numbers
drift — grep the symbol, don't trust the number.

---

## 1. Why this arc exists

Don (FLEX-6300, remote over SmartLink, radio living at Tony's) could not
transmit PC audio. Two days of hunting followed: packet captures, an Opus
decode harness, a decompile of shipping SmartSDR, a read of the AetherSDR
source, and a byte-for-byte wire diff. Every one of those said our client was
clean.

The thing that cracked it was Noel's question — **"did we verify RF actually
went out with the 700 Hz tone?"** Nobody had. Forward power jumped 0 → ~30 dBm
the instant we keyed, and a DAX IQ capture showed a rock-steady +703 Hz spike
58 dB above the noise floor. **PC-audio transmit was working the whole time.**

What was broken was the instrument. JJ Flexible was reading a meter that is
blind to PC audio, so the app told the operator "Check microphone" while the
radio was cheerfully putting his voice on the air. Don's *actual* problem was
separate and mundane: his saved input device was a dead WDM-KS kernel pin, and
the lying meter made that undiagnosable.

**Two things follow from that, and they are the whole arc:**

1. **Honesty** — the app must measure what is really happening and say so. This
   is the defect half, now largely fixed.
2. **Coaching** — a sighted operator sets mic gain by watching the ALC bar. A
   blind operator cannot. The same meter plumbing that stops the lying can tell
   an operator "coming in hot" / "just right" / "turn it up" — which is a
   genuine accessibility differentiator, not a consolation prize. This is the
   feature half, and it is most of what remains.

## 2. The finding that everything rests on: which meters tell the truth

Measured on the bench 8600 with diag build 719, comparing a PC-audio tone
against analog-mic voice. Stated as prose because it must read aloud cleanly:

- **`SC_MIC`** responds to **both** sources — about −10 dBFS on the PC tone, and
  it tracks voice on the analog mic. It sits downstream of mic *selection*, so
  it is source-blind in the good way: no PC-versus-analog branching needed
  anywhere in our code.
- **`CODEC output`** responds to both.
- **`SW ALC`** (meter name `ALC`) responds to both. This is real transmit drive.
- **`COD-/MIC`** — the meter JJ Flexible historically read as `MicData`, whose
  radio-side description is "MIC output in CODEC", meaning the *analog* mic's
  ADC path — sits at **−120 for PC audio** and responds only to the analog mic.
  **This is the meter that lied.**
- **`HWALC`** — which JJ Flexible mislabeled as "ALC" — is the external-amplifier
  ALC feedback on the rear RCA jack. Dead for both audio sources, because it is
  not an audio meter at all.

So the app was reading **the only two source-blind-in-the-bad-way meters** it
could have picked. Correcting this is small and unlocks the coaching work.

**Do not remove HWALC.** Noel's call, 2026-08-11: older amplifiers without
digital or network control legitimately use the RCA ALC line for overdrive
protection, so those operators need it surfaced. The fix is to stop
*mislabeling* it — SW ALC is transmit drive, HWALC is amplifier ALC, and they
are two different readings with two different names.

**FlexLib exposes no dedicated events** for `SC_MIC` / `CODEC` / `ALC`. The
route is the existing public `Radio.FindMeterByName(name)` plus a subscription
to that meter's `DataReady`.

## 3. Done and shipped (verified at the radio)

On `main` and `track/flexlib-4220`:

- **`c4d80e8b` — the meter fix.** `Radios/FlexBase.cs` gained `ScMicDb`,
  `ScMicMaxDb` (whole-transmit peak, reset at key-down), `ScMicRecentDb`
  (rolling ~1.5 s so it follows a level back *down*), `SwAlcDb`, and
  `ResetScMicMax()`, all fed by a lazily-hooked `hookTxMeters()`.
  `JJFlexWpf/PttSafetyController.cs` now judges "Check microphone" on
  `ScMicMaxDb < −45 dBFS` and hot-drive on `SwAlcDb > −0.3 dBFS`. **Peak-hold is
  load-bearing:** SC_MIC dips to −150 in the gaps between spoken words, so an
  instantaneous read false-alarms on every breath.
  - **Bonus fix riding along:** the ALC auto-release compared a dBFS meter
    against a linear threshold, so the 60-second phantom auto-unkey fired on
    healthy transmissions. Now `SwAlcDb < −50`.
  - The `ALC` property doc was corrected to say it is HWALC / amplifier ALC.
- **`396b1514` — the picker fix.** `JJPortaudio/JJPortaudio/Devices.cs` hides
  WDM-KS kernel pins behind `ShowAdvancedDevices` (default off) and logs every
  device by name. KS pins carry the *prettiest* full-length names in the list
  while MME truncates honest endpoints at 31 characters mid-parenthesis — which
  is precisely why both Don's input and Noel's output picks went wrong. Kernel
  pins also bypass mixing and volume and **can seize a device exclusively,
  which could silence NVDA if taken on the speech card.**
- **`4528e3cf` — Audio Workshop honesty and the spoken verdict.** The workshop
  now reports TX drive from SW ALC, amplifier ALC separately, and mic audio from
  SC_MIC with a plain-English verdict. `MicAudioVerdict()` is the shared
  judgment function: below −30 dBFS "turn it up", above −6 dBFS "coming in hot",
  otherwise "just right". The unkey summary speaks the verdict and the peak.

**Field-verified on the ms-02, 2026-08-11:** workshop spoke "just right", zero
false "Check microphone" warnings. Noel's own comment: *"my mic was just right,
what do you know."*

## 4. Done but NOT radio-tested

On `honest-tx-audio` only — **needs a keyed verification pass before it merges:**

- **`b4bd721f`** — three things from Noel's live test:
  - **Live verdict.** Speak Transmit Status (Alt+Shift+S) now appends the
    running mic-audio verdict and peak while transmitting, driven by
    `ScMicRecentDb`. This exists because the unkey-only verdict forced you to
    *stop* an audio check to hear how you were doing, which is backwards when
    you are adjusting mic gain and want to hear the effect.
  - **Audio Workshop on the Audio menu** — discoverability. Don has to be able
    to find it.
  - **Disconnect wording** — Chatty "JJ Flexible Access disconnected from
    radio", Terse "JJ Flexible disconnected".
- **`a60f54e7`** — the design capture this document organizes.

## 5. The build queue

Ordered by what unblocks what, not by size. Tracks A and B are the Don-enabling
work; C and D are the differentiators; E is cleanup that rides along.

### Track A — the audio hub: menu, expander, leader key

**Why first:** it is the discoverability layer for everything already built, it
is pure UI over existing engine properties, and it is what lets Don actually
*find* the controls. Nothing else depends on it, so it can run in parallel with
everything.

**The problem it fixes.** The Audio menu's volume items adjust the *radio*
(headphone, line-out, mutes). A PC-audio operator who bumps "Headphone Level"
hears no change whatsoever — and the PC-output volume, the one they actually
want, **is not in the menu at all.**

**Shape (Noel, 2026-08-11):** relabel and group, do not hide.

- An **"On-radio outputs"** group: "On-radio headphone volume up/down",
  "On-radio line-out", the mutes.
- A **"PC audio"** group: PC output volume (currently missing) and mic level.
- Optionally a toggle to hide the on-radio group when PC audio is on — but
  labels alone solve the core confusion, and hiding breaks hybrid and
  at-the-radio monitoring, so it is an option and not the default.

**Surface it in threes.** Per the "JJ Flexible in threes" pattern, every one of
these lands in the **menu** (discovery), the **Home audio expander** as
arrow-navigable fields (in-context), and a **hotkey** (fast). The coaching
verdict gets the same treatment: hotkey (done), a readout field in the expander,
and a menu entry.

**The hotkey is a layered sub-mode, not four new flat bindings.** Extend the
existing **Ctrl+J leader** — the "JJ key", already implemented in
`JJFlexWpf/KeyCommands.cs` with no timeout and Escape-cancels:

- `Ctrl+J` → `V` enters volume mode; then a target letter (H = on-radio
  headphone, P = PC output, M = mic level, L = on-radio line-out) with Up/Down
  to adjust, each announcing its value, Escape to exit. This is JAWS/NVDA
  layered-keystroke muscle memory. Implementation is a nested sub-mode in
  `DoLeaderCommand` that persists on targets and arrows until Escape.
- **Also add the TX-processing controls to the leader:** speech processor
  on/off and level, compander on/off and level. A frequent operator rides these
  in three keys mid-QSO instead of digging through menus.
- Coherent framing for the whole leader: **"adjust how I sound and what I
  hear."** It already holds the RX DSP toggles.
- **Standing reminder (Noel, 2026-08-11):** the leader is a *general* command
  surface and is currently pigeonholed into audio and DSP. Reach for it before
  adding any new top-level chord, for any feature family. See
  `memory/project_ctrl_j_leader_command_layer.md`.

**Keyboard audit applies** — every new leader binding needs a line in
`docs/help/md/keyboard-reference.md` and Command Finder entries.

### Track B — coaching and JJSmartAudio

**Why:** this is the accessibility differentiator, and it is the positive-side
twin of the same meter plumbing Track A's readouts use.

- **Upgrade the metric to LUFS** (ITU-R BS.1770 / EBU R128 — K-weighted,
  gated), computed PC-side on the raw mic float samples already sitting in
  JJPortaudio's input callback, pre-Opus and pristine. **The gating natively
  drops the silent gaps between words**, which dissolves the peak-hold
  false-alarm problem with no custom logic. Momentary (400 ms) for live
  coaching, short-term (3 s) for the query and the auto-set target, integrated
  for calibration.
- **Keep radio ALC as a hard guardrail on top.** LUFS says "you sound right";
  ALC says "you are not overdriving the transmitter". Two different failure
  modes, both needed.
- **"Set my mic level" one-button calibration** — operator talks a few seconds,
  we measure and inch `MicLevel` until loudness hits target, then announce where
  we set it. Operator keeps final say. Flexibility principle in miniature.
- **Continuous coaching mode** — the same engine left running, speaking only on
  drift.
- **Raw values for the geeks (Noel: "for geeks, they should be able to read the
  values as well").** Live LUFS momentary and short-term, ALC dBFS, SC_MIC,
  forward power, as a screen-reader-navigable read-only readout, plus a verbose
  tier on the query that speaks the figures. Plain language by default, full
  detail on request — fits the verbosity architecture and the Sprint 29
  diagnostics tab.
- **Live-verdict output configurable:** plain English, dBFS, or both.
- **Measurement-point caveat:** PC-side LUFS is clean for the PC-audio path;
  the analog-at-the-radio path has no PC-side samples and falls back to the
  SC_MIC/ALC meters. First cut targets PC audio — the remote operators who most
  need coaching.

### Track C — the built-in tone generator

**Why it is not a nicety:** the ms-02 has no microphone, so testing today needs
an external tone player piped through a virtual audio cable. A built-in
generator removes that rig entirely, and it is how we calibrate the verdict
thresholds in the first place.

- Default **440 Hz** (A440, the media-business reference), adjustable to 1 kHz
  and elsewhere, with adjustable level.
- **Replace the mic, do not mix** — mute the real input while the tone runs so
  only the clean tone transmits with no room bleed. Insert at the input stage
  ahead of the pipeline.
- **Configurable local monitor** — the operator chooses whether they hear it
  locally. Both answers are legitimate: confirm by ear, or keep quiet.
- Lives in the Audio Workshop.

### Track D — the input-rescue TX pipeline

**Why this is a real feature and not radio-DSP duplication:** a Flex has TX EQ
and a speech processor, but **no noise gate and no way to repair a bad source
mic.** Radio-native processing shapes a *good* signal. This pipeline makes a
*bad* signal good **upstream** of it. Anchor use case: someone remote with only
a laptop internal mic — tinny, hissy, fan and room noise — made armchair-copy.

- **Architecture:** a `TxAudioPipeline` mirroring the existing `RxAudioPipeline`,
  living in JJPortaudio's input callback between mic capture and Opus encode —
  the same place LUFS metering lives.
- **Chain order** (from Noel's audio-pro friend, and it is the standard vocal
  chain — clean before you shape): vocal isolation / **RNNoise first** →
  **expander/gate** → **parametric EQ** → **compression** last. Isolate the
  voice from the room before you EQ and compress, so you are not shaping the
  noise. **Order must be dynamically reorderable** by operator or profile;
  this is the default, not a hardcoding.
- **The unlock: use stock RNNoise, no training needed.** The standard
  pre-trained model handles room noise fine, and `RxAudioPipeline` already runs
  the RNNoise/Xiph `.rnnn` lineage — so the TX denoise stage reuses existing
  infrastructure and ships now. **Custom-trained models are a quality upgrade,
  not a prerequisite.** The pipeline is not blocked on the ML project.
- **EQ-stacking coordination.** Flex's native TX EQ is an 8-band *graphic* EQ
  (fixed centers), not parametric. When our PC-side parametric EQ is active,
  flatten the radio's onboard TX EQ so the two do not fight, and restore it when
  ours is switched off. General principle: **when a PC-side stage duplicates a
  radio-native one, the pipeline neutralizes the radio's copy.**
- **Starter profiles** — "laptop mic", "headset", and so on, getting an operator
  90% there in one selection. Mirrors the RX noise profiles and JJSmartAudio's
  get-you-close philosophy.
- **UI is progressive disclosure.** Hide the individual knobs (gate threshold,
  EQ bands) by default — pick a profile and you are done — behind a "Show
  advanced controls" toggle. Same simple-default-full-detail-on-request shape as
  the coaching readout.
- **Also on the list:** IR/convolution mic correction (apply an impulse response
  to steer a cheap mic toward an expensive one — mic modeling).
- **Strategic framing, corrected by Noel:** many hams build TX chains with
  Reaper plus VST plugins plus virtual cables, and **DAW accessibility is fine
  these days** — OSARA makes Reaper genuinely usable blind. So the pitch is *not*
  "the only accessible option". The value is **integrated and zero-setup**: no
  second app, no virtual cables, no plugin chain to license and maintain, and it
  travels with JJ Flexible.
- **Neural tier — deliberately deferred.** Training our own Clarity-VX-class
  voice model is a real ML workstream sharing tooling and the ms-02 NVIDIA host
  with the CW neural decode arc. Conventional DSP gets most of the way now.

### Track E — adjacent fixes that ride this arc

Small, independent, surfaced during the same investigation:

- **Mode reachability (Don's "can't get to SAM").** `RigCaps.ModeTable` has all
  ten modes, but F10/F11 cycling uses a hardcoded `CommonModes` subset
  (`MainWindow.xaml.cs`, USB/LSB/CW/DIGU/DIGL/AM/FM) and the Alt accelerators in
  `NativeMenuBar.cs` cover the same seven — so SAM, NFM and DFM are unreachable
  by the fast keyboard routes, and nothing says the cycle is a subset. Fix per
  flexibility principle: a Settings list of which modes join the cycle,
  defaulting to the current seven; document the subset in the keyboard
  reference; add per-mode Command Finder entries. Confirm with Don which route
  he used.
- **Slice list ordering.** The Slice A–C list reads bottom-to-top, so you arrow
  *up* to go A→B→C. Make it top-to-bottom by default (down-arrow walks A→B→C)
  **with a setting to reverse it** for anyone who prefers the current behavior.
  Confirm which control Noel means before touching — candidate is
  `MultiFlexDialog` or the Home slice display.
- **Speak GPS Status should include frequency error in parts-per-billion.** The
  spoken readout omits the oscillator discipline number. See
  `memory/project_gps_gnss_oscillator_facts.md` for the fields.
- **Device picker completion** (the rest of the audio-picker work beyond the
  shipped KS hiding): display WASAPI entries with full names and accurate
  default flags, open WASAPI shared with MME fallback for 44.1 kHz-configured
  devices; **channel-count filter becomes a capability adapter** (accept mono
  and 4-channel, downmix/upmix — never hide a device for its channel count,
  which is what hid Noel's real internal mic); **device change must hot-apply or
  announce "takes effect on reconnect"**; **Escape must announce "changes
  discarded"** instead of silently throwing away arrowed selections; **announce
  the transmit microphone by name at audio start** — the single check that would
  have collapsed Don's two-day mystery into one sentence.
- **PC Audio preflight coaching** — walk the audio chain at connect and at first
  transmit; fix what we own (re-assert radio-side `MicInput`), coach on what we
  do not (Windows mic privacy, which silences desktop apps while Store apps like
  Sound Recorder keep working — the trap that made Don's testing prove the wrong
  thing). Never an error; always a configuration state anyone can land in.
- **Held-PTT health gap:** "Check microphone" never fires during held Ctrl+Space
  PTT because `AlcTimerTick` stops the timer in `PttState.PttHold` — the health
  monitor only covers the transmit-lock path.
- **Audio troubleshooting help page**, house voice, with Don's case as the
  template narrative.

### Track F — test infrastructure

- **Full-duplex and half-duplex DAX IQ capture** as a permanent instrument, not
  a one-off script. This is what proved RF was going out, and the half-duplex
  variant matters because it means **every Flex including 1-SCU radios can get
  ground truth** — PC-side demodulation carries TX through the transmit mute
  (detune-proven 2026-08-09). Notes in `docs/planning/active/audio-workshop-plan.md`
  §4e–4h; that IQ tier **supersedes** the earlier loopback design.
- Practical gotchas already learned: the probe needs its own panadapter (free
  one with `display panafall remove`), full duplex gets clobbered by the global
  profile load at connect, and rxant must equal txant for internal coupling.

## 6. Execution order

- **Now, in parallel:** Track A (audio hub) and Track C (tone generator). A is
  discoverability for what already exists; C unblocks mic-less bench testing and
  threshold calibration. Neither depends on the other.
- **Then:** Track B (LUFS coaching), which benefits from the tone generator for
  calibrating thresholds and from Track A for its readout surfaces.
- **Then:** Track D (input-rescue pipeline) — the largest piece, and the one
  whose design most rewards the earlier tracks being settled.
- **Anytime, independently:** Track E items are individually small and share no
  files with each other; they suit background agents well.
- **Before any of it merges to main:** radio-test `b4bd721f`'s live verdict.

## 7. Verification still owed

- **The live verdict (`b4bd721f`) has never been exercised at a radio.** Key up,
  press Alt+Shift+S while transmitting, confirm it speaks a sensible verdict and
  peak.
- **PC-audio VOICE test.** The tone is proven; speech is not. Swap tone for
  voice, demodulate the IQ capture to WAV, listen for intelligibility.
- **Over-the-air confirmation.** The bench 8600 has no antenna on any of its six
  ports. Don's radio does — but **Don's radio is his production station and is
  never a test target.** Route is an SDR or a contact.
- **Don's own SmartLink run** once he is reachable: walk the device picker
  (listening for the spoken receipt), then key and confirm.

## 8. Open design questions

- Exact letters for the leader sub-mode targets and the TX-processing toggles —
  work out collisions during implementation.
- Whether the radio-side `MicInput` re-assert is automatic-and-announced or
  announced-then-done.
- Whether "hide on-radio outputs when PC audio is on" ships at all, given labels
  alone solve the confusion.
- Verdict thresholds are currently −30 / −6 dBFS by judgment; the tone generator
  is how we calibrate them honestly.
- For never-configured users, whether audio devices default to
  follow-Windows-default on both directions. Existing users keep pinned devices
  untouched either way.

## 9. Related documents

- `docs/planning/active/research-queue.md` — the detailed capture and rationale
  for every item above; source of record.
- `docs/planning/active/audio-workshop-plan.md` — the workshop's own design,
  including the IQ tier that supersedes the loopback.
- `docs/planning/vision/moonbounce-mixer-handshake.md` — transverter profiles;
  shares the drive-level and port-binding machinery.
- `memory/project_ctrl_j_leader_command_layer.md` — the leader key as a general
  command surface.
- `memory/project_dsp_controls_design.md` — the RX-side pipeline this TX
  pipeline mirrors, whose engine is done and whose UI is the same kind of gap.
