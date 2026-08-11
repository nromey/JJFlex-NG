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

### Track A-2 — levels dialogs, and retire the old menu items

**Field feedback, Noel at the radio 2026-08-11, after Track A merged.** PC audio
plays, PC Output Volume genuinely changes it — the core deliverable works. Two
problems with the *shape*:

- **A menu is the wrong instrument for repeated adjustment.** The control is two
  menus deep and the menu closes after each activation, so nudging a level five
  times means opening it five times. Menus are for **discovery and one-shot
  acts**, not for riding a value.
- **The old audio volume items still exist alongside the new groups** —
  duplicates that need retiring. Known gap from Track A, not a surprise.

**Shape (Noel's proposal, refined):**

- **Two dialogs — "PC Audio Levels" and "On-Radio Levels"** — each with its
  controls together and Up/Down to ride them, so repeated adjustment happens
  in one place with the dialog staying open. **Keep them as two, not one
  combined dialog:** the entire point of this track is that these are two
  different things on two sides of the wire, and merging them back into one
  surface would blur exactly the distinction the labels just established.
- **The menu entry becomes a single item that opens the dialog**, replacing the
  up/down *pairs*. That fixes the depth complaint and removes the "multiple
  audio up and down things" clutter in one move — the menu goes back to being a
  door rather than a control.
- **The old duplicate items get deleted** in the same pass.
- The Home expander and `Ctrl+J, V` are unchanged — they stay the in-context and
  fast paths.

**Open question to settle by trying it: does `Ctrl+J, V` already cover this
need?** It is a persistent mode built precisely for riding values with arrows
without leaving Home, so it may already be the answer to "hard to change
multiple times." If so, the dialogs' remaining value is **seeing and comparing
all levels at once** plus discoverability for operators who do not memorise
modes — still worth building, but a different justification and possibly a
smaller job. Test the leader mode before scoping the dialogs.

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
- **DROP THE TRANSMIT-STATUS PREAMBLE WHILE TRANSMITTING (Noel, field use
  2026-08-11: "I really don't need to hear my TX before mic if we're going to
  use this to monitor audio").** `Alt+Shift+S` currently speaks
  `GetPttStatusText()` and *then* appends the mic verdict, so riding mic gain
  means hearing "Transmitting on 14.100…" before every reading. **The principle:
  transmit state is only information when the operator does not already know it
  — and they just keyed the radio.** While receiving, the status is the entire
  point; while transmitting, it is a preamble in front of the one thing they
  lack. Make the command **context-aware**: lead with verdict and peak while
  keyed, keep today's behaviour while receiving.
- **THE BETTER PRIMARY: a read-only EDIT field carrying the live reading,
  placed immediately before the Start Test button in tab order (Noel,
  2026-08-11).** Shift+Tab from Start Test lands on it. Two reasons this beats a
  spoken command as the main route:
  - **A read-only *edit* is focusable and review-readable; a label is not.**
    Static text gets skipped by tab order and is awkward to re-read on demand.
    This is the screen-reader-native way to expose a live value, and it is worth
    treating as a house pattern rather than a one-off.
  - **It means the app does not need a "speak my level" command at all — the
    screen reader already has one.** Sitting on the field, NVDA's read-current-
    control command polls the value as often as the operator wants, with no
    binding to remember, no chord to collide with, and no speech firing when
    they did not ask.
  - **Update mechanics, learned from Track A's expander field:** keep the text
    current continuously so review reads fresh, but **do not fire live-region or
    name-change notifications**, or a value that moves twice a second floods
    NVDA. Fresh on demand, silent otherwise.
- **`Ctrl+S` for Save Preset** — Noel's suggestion, and it resolves the specific
  collision: moving Save off the `Alt+Shift+S` mnemonic frees that chord inside
  the Workshop. Worth doing regardless of the general routing fix, since it is
  the conventional Save key and the current binding was accidental.
- **A hotkey to start the test**, so the adjust-and-hear loop does not require
  finding a button.
- **INITIAL FOCUS LANDS ON START TEST, AND THE READOUT SITS BEHIND IT (Noel,
  2026-08-11).** Open the surface and focus is already on the primary action —
  if you are set up, or you just loaded a profile, **you press Enter and you are
  running.** Zero navigation for the common case. Then **Shift+Tab reaches the
  levels readout**, Tab returns.
  - **Why the readout belongs *before* the button rather than after:** the
    reading only means anything once the test is running, so the tab order
    matches the temporal flow — **forward tab does things, backward tab inspects
    what just happened.** Shift+Tab reads naturally as "look at the result."
  - **Generalise it: initial focus is a budget, and it should be spent on the
    most likely next action, not on whatever is first in visual order.** For a
    keyboard-and-speech operator, focus placement literally sets the keystroke
    cost of the common task. Same principle as a confirmation dialog focusing
    its default button. Worth auditing other dialogs against — a candidate for
    the BlindCat anti-patterns checklist.
- **`Ctrl+O` to load a profile** (Noel, 2026-08-11), pairing with `Ctrl+S` to
  save. Standard document verbs — open and save — applied to presets. Learnable
  because they are universal rather than app-invented, which is the opposite of
  the accidental `Alt+Shift+S` binding they replace.
- **Add a dedicated mic-audio query on the `Ctrl+J` leader** that speaks *only*
  verdict and level — the secondary route, for operators who prefer speech to
  navigation, and **context-aware per Noel (2026-08-11)**. That is the binding an operator rides while adjusting gain,
  it costs no new flat hotkey, and it fits the leader-as-audio-hub framing.
  **It also works where `Alt+Shift+S` currently does not:** the Audio Workshop
  swallows that chord as a Save Preset mnemonic (see the global-routing defect),
  and the Workshop is precisely where someone sits while adjusting mic gain.
  Leader chords are not mnemonics, so this route is unaffected.
- **Measurement-point caveat:** PC-side LUFS is clean for the PC-audio path;
  the analog-at-the-radio path has no PC-side samples and falls back to the
  SC_MIC/ALC meters. First cut targets PC audio — the remote operators who most
  need coaching.

### Track C — the built-in tone generator

**Why it is not a nicety:** the ms-02 has no microphone, so testing today needs
an external tone player piped through a virtual audio cable. A built-in
generator removes that rig entirely, and it is how we calibrate the verdict
thresholds in the first place.

- Default **440 Hz** (A440, the media-business reference), with adjustable level.
- **Frequency is adjustable as an ACCESSIBILITY requirement, not a convenience
  (Noel, 2026-08-11).** Hearing varies — age-related loss, noise-induced loss,
  asymmetric loss — and **a test tone the operator cannot hear is useless for
  the thing the tone is for**: confirming the check is actually running and
  hearing what your transmit chain does to it. 440 Hz is a good default, not a
  universally good choice. The operator picks; we suggest.
  - **This setting is per-operator, not per-radio.** Hearing does not change
    when you switch rigs, so it belongs in app settings and not the
    serial-keyed per-radio config. It persists across sessions — nobody should
    re-dial it every time.
  - **Named presets plus free entry**, per the progressive-disclosure pattern
    used elsewhere in this arc: a short list (440 Hz reference, 1 kHz standard
    test, 700 Hz CW-like) with a raw frequency field for anyone who wants it.
  - **Constrain or warn against frequencies outside the TX filter passband.**
    This is the trap: SSB transmit filters typically run roughly 100–2900 Hz,
    so an operator who moves the tone to where *they* hear best can land
    outside the passband and **transmit nothing at all** — silently, while
    believing they are testing. The app already knows the TX filter low and
    high (the Audio Workshop surfaces them with a width readout), so either
    clamp the range to the live passband or speak a plain warning: "that tone
    is outside your transmit filter — nothing will go out." Never let this fail
    quietly; it is the same class of defect as the meter that lied.
  - **Relevant reviewer — with a caveat that shapes how we use him.** Patrick
    (BHN) is the hearing-loss axis in the tester pool *and* an audio
    professional, so he is the right person to judge the default and the preset
    list. But **he has no radio** (Noel, 2026-08-11), so he can review the
    design and listen to captured audio, and cannot bench-test anything that
    needs a keyed transceiver until JJ Flexible Connect can share one with him.
    Route design questions to him now; hold radio-seat verification. See
    `memory/patrick_bh_network_tester.md`.
- **Replace the mic, do not mix** — mute the real input while the tone runs so
  only the clean tone transmits with no room bleed. Insert at the input stage
  ahead of the pipeline.
- **Configurable local monitor** — the operator chooses whether they hear it
  locally. Both answers are legitimate: confirm by ear, or keep quiet.
- Lives in the Audio Workshop.

### Track C-2 — the Audio Check should not transmit by default

**Noel at the radio, 2026-08-11, seeing the safety line: "you have it at 10
watts. If you have no antenna, that's a bit high."** Correct, and the fix is
mostly wiring something that already exists.

**Current behaviour, verified:** `AudioWorkshopDialog.xaml.cs:1745-1750` reads
`if (_lowPower && currentPower > 10) rig.XmitPower = 10`. So **10 W is a
ceiling, not a setting** — it only ever lowers power, never raises it, and it
**cannot override dummy load** because 0 is not greater than 10. That is better
than it first appears, but 10 W is still the wrong ceiling for a bench with no
antenna, and there is no way to choose the value.

**`DummyLoadMode` already exists and is already on the menu** —
`FlexBase.cs:9825` zeroes both `XmitPower` and `TunePower` and restores them on
disable, and `PttSafetyController.cs:464` already skips the ALC auto-release
check while it is active (correct — no ALC at zero power must not read as a
dead mic). It is simply not wired into the Audio Check.

**The design point, which is stronger than "lower the number": an audio check
does not need RF at all.** Every meter the coaching depends on — `SC_MIC`,
SW `ALC`, and the LUFS metering in Track B — sits **upstream of the power
amplifier**, so the measurement is identical at 0 W and at 100 W. And the risk
is not only the finals: **with a tone armed, a 10 W audio check transmits a
steady 440 Hz carrier onto whatever frequency the operator is tuned to** —
possibly an occupied one. That is a courtesy and licensing problem, not just an
SWR one, and Track C makes it materially more likely by making tones easy.

**So:**

- **Default the Audio Check to dummy load (0 W).** Not an option — the default.
- **Offer a settable low-power value** (Noel: "a low power output with a combo
  you can change so I can change it to 1 if I need to") for the separate,
  deliberate act of confirming RF actually leaves the radio.
- **Say which mode you are in, out loud, at key-down — and note the safety line
  currently cannot.** `AudioWorkshopDialog.xaml.cs:1812` builds
  *"Transmitting on {freq}, {effectivePower} watts, audio from {source}"* from
  `rig.XmitPower` with **no awareness of `DummyLoadMode` whatsoever.** With
  dummy load engaged that renders as **"Transmitting on 14.100, 0 watts"** —
  technically true and genuinely confusing, since "transmitting at zero watts"
  invites the operator to wonder what failed. It must name the *mode*:
  "Audio check, dummy load, no RF" versus "Audio check, transmitting at 1 watt".
  That is the difference between a reading and an explanation, and it is the
  same honesty standard the rest of this arc is built on.
- **Field note confirming the ceiling is milder than it looks (Noel,
  2026-08-11):** his check announced **1 watt** despite the hardcoded 10,
  because his radio had been sitting at 1 W since the 2026-08-09 DAX IQ tone
  runs and `currentPower > 10` never fired. The announcement was honest. The 10
  is a cap that catches a 100 W station, not a level the check imposes — so the
  fix is about the *default* and the *choice*, not about the check secretly
  raising power.
- Treat *measuring your audio* and *confirming you are on the air* as two
  separate tests, because they are.

### Track D — the input-rescue TX pipeline

**Why this is a real feature and not radio-DSP duplication:** a Flex has TX EQ
and a speech processor, but **no noise gate and no way to repair a bad source
mic.** Radio-native processing shapes a *good* signal. This pipeline makes a
*bad* signal good **upstream** of it. Anchor use case: someone remote with only
a laptop internal mic — tinny, hissy, fan and room noise — made armchair-copy.

- **Architecture:** a `TxAudioPipeline` mirroring the existing `RxAudioPipeline`,
  living in JJPortaudio's input callback between mic capture and Opus encode —
  the same place LUFS metering lives.
- **Chain order** (from **Patrick**, the BHN infrastructure operator and audio
  professional in the tester pool, via Mastodon 2026-08-11 — and it is the
  standard vocal chain, clean before you shape): vocal isolation / **RNNoise
  first** →
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

**RECEIVER SIMULATION ON PLAYBACK (Noel, 2026-08-11) — the part that makes this
an audio instrument rather than an RF instrument.**

Play the captured IQ back **through a simulated receiver** — AGC, receive
filter, and a noise floor — instead of as a clean demodulation.

**Why this is not a garnish.** Over-processing is the single TX audio fault an
operator structurally *cannot* self-diagnose, because **AGC pumping is an
emergent artifact of your compression meeting somebody else's AGC.** Neither
half produces it alone. A clean demod cannot show it, and the radio's own TX
monitor cannot either — the monitor tap is pre-AGC by construction. So the
operator who has slammed the speech processor hears "loud and punchy" on every
surface available to them, while the receiving station hears breathing and
pumping. This closes the last blind spot in the honest-transmit-audio story:
we now measure level honestly (SC_MIC), loudness honestly (LUFS), and RF
honestly (IQ) — but nothing yet tells the operator **what they sound like at
the far end.**

**What has to be in the chain, minimally:**

- **AGC, mirroring the operator's own rig.** `AGCSpeed` (the AGCMode enum),
  `AGCThreshold` (0–100) and `AGCOffLevel` are all already exposed on
  `FlexBase` (9104, 9120, and the slice copy at 7816) — so "simulate my radio
  as it is set up right now" costs nothing to wire and needs zero
  configuration. Attack/decay/hang behaviour per mode is the modelling work.
- **A receive filter**, since the far end is usually listening through
  something narrower than you are transmitting.
- **A selectable noise floor — and this one is non-obvious but essential.** The
  IQ capture is of your own signal through internal coupling, so it arrives at
  effectively infinite signal-to-noise. Fed to an AGC, that just pins the gain
  and **never pumps** — the simulator would show nothing and quietly imply
  everything is fine. Pumping is loudest *near the noise floor*, where the AGC
  rides up in the gaps between words and drags the band noise up with it. That
  **"noise breathing between words" is the classic tell**, and it only exists
  if we put noise there. A signal-to-noise slider is therefore part of the
  minimum viable feature, not a later refinement.
- **Optional: slow QSB**, which is what really exercises an AGC. Nice to have,
  not required for the pumping verdict.

**Whose receiver are we simulating?** The operator's own, by default, from its
live settings. That is deliberate and it is the stronger choice: you already
know what every other station sounds like through your receiver, so your own
voice through that same receiver is **directly comparable against a reference
you have been building for years.** Simulating a hypothetical average station
would be less honest and less useful.

**Mirror mode versus manual mode (Noel, 2026-08-11).** Mirroring is the default
and the conservative choice, but **turning the mirror off unlocks the AGC
controls so the operator can sweep them** — off, slow, medium, fast, different
thresholds — and hear how the same transmission lands on receivers that are not
set up like theirs. Noel framed this as a "how do I sound to people" curiosity,
and it is that, but it is also genuinely diagnostic:

- **Different operators run different AGC, so "how do I sound to people" is
  plural.** Audio that behaves through slow AGC can pump badly through fast.
  There is no single far end.
- **AGC off is the analytical control condition, and that is the sharpest part
  of this idea.** With AGC off you hear your processed audio as you actually
  sent it. If it already pumps there, **that is your compressor's makeup gain
  lifting room noise in the pauses** — your fault, your fix, in the pipeline.
  If it is clean with AGC off but pumps with AGC on, you handed the far end's
  AGC too much to chase — still your fault, but a *different* fix. One toggle
  separates two faults that sound identical from the operating chair.
- **Audition mode rather than re-dial-and-replay.** Play the same clip through
  off / slow / medium / fast back to back, announcing each before it plays.
  Manually re-dialling and re-triggering four times is exactly the kind of
  friction a keyboard-and-speech operator should not have to absorb to make a
  comparison, and the comparison is the whole point.
- **Replaying a recording is what makes this a controlled experiment.** Because
  the capture is played back rather than re-transmitted, every AGC setting hears
  **the identical source audio.** If the operator had to key and speak again for
  each setting, their voice would differ each time and the variable would not be
  isolated. This is a real methodological advantage of the replay design, and it
  is worth stating in the help text.
- **The AGC and signal-to-noise controls interact**, so the UI should make it
  easy to hold one and sweep the other rather than moving both at once.

**Hard requirement: these controls must never touch the radio.** They apply to
playback only. An AGC control that looks like *the* AGC control, in an app that
also has a real one, is a genuine confusion hazard — and for a speech-first
operator the label is the entire user interface. Name them unambiguously
(simulated / playback AGC), speak that framing on entry, and never write an
`AGCSpeed` or `AGCThreshold` back to the rig from this surface.

**State the limits plainly (Noel already did: "it wouldn't be perfect").** This
is a **comparative instrument, not an absolute one.** It does not model
propagation, the far operator's DSP, or their filter choices. Its real power is
A/B: record with processing off, record with processing on, listen to both
through the identical simulated receiver. **The delta is trustworthy even where
the absolute is approximate.** The help text and the UI must say so — an
instrument that overclaims is exactly the failure this whole arc exists to
correct.

**This is also the evaluation harness for Track D, and that changes its
priority.** "How does this sound to a receiving station" is precisely the
metric for tuning the gate/EQ/compressor chain and its starter profiles — and
later for evaluating the neural TX model. Without it, Track D's presets get
tuned by ear on a monitor path that cannot reveal the very artifact
over-processing produces. So Track F stops being optional infrastructure and
becomes **a soft dependency of Track D's tuning work**: D can be *built* without
it, but D's profiles cannot be *honestly tuned* without it.

## 6. Execution order

- **Now, in parallel:** Track A (audio hub) and Track C (tone generator). A is
  discoverability for what already exists; C unblocks mic-less bench testing and
  threshold calibration. Neither depends on the other.
- **Then:** Track B (LUFS coaching), which benefits from the tone generator for
  calibrating thresholds and from Track A for its readout surfaces.
- **Then, and before D is tuned:** Track F's receiver simulation. It is the only
  surface that can reveal AGC pumping, which is the artifact over-processing
  produces — so it is the measuring instrument D's presets get tuned against.
  Build D's chain in parallel if convenient, but do not call its profiles
  finished before this exists.
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
- Whether the tone generator **clamps** its frequency range to the live TX
  filter passband or **allows-and-warns**. Clamping cannot mislead but silently
  removes a choice; warning respects the operator but relies on them hearing it.
  Leaning allow-and-warn, on the flexibility principle — but the warning has to
  be unmissable, because the failure mode is transmitting nothing while
  believing otherwise.
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
