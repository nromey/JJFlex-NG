# Tester Onboarding Strategy

**Status:** Multi-radio Phase 8 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to architecture synthesis (Phase 9); informs sprint UX
**Builds on:** Phases 4, 5, 6 (`IRadioBackend`, per-radio config, audio routing)

---

## What this document is

The architecture is only worth shipping if real testers can get their radios
onto JJF and operating successfully. This document walks through each
named tester's setup path end-to-end: install JJF, add their radio,
configure connection params, route audio, verify functionality, and start
operating. Per `feedback_accessibility_is_end_to_end.md`, every step must be
accessible — install + configure + use + maintain.

Five tester profiles, five paths. For each: the artefacts JJF presents, the
decisions the user makes, the failure modes to anticipate, and what "good" looks
like at each step.

---

## 1. Don — FLEX-6300 via SmartLink (existing path, no change)

**Status:** Working today. Documented for completeness; nothing needs to
change in the multi-radio sprint for Don.

**Setup steps:**

1. Install JJF via NSIS installer (existing path).
2. First-run: enter SmartLink credentials via Auth0 / WebView2 dialog.
3. JJF discovers Don's radio via SmartLink directory.
4. Don picks `6300inshack` from the list.
5. JJF connects; full Flex UI renders.

**Artefacts:**
- `radios\flex-<don's-serial>\connection.json` — backend = FlexLib, no port
  config (network)
- SmartLink session credentials in user-scope config
- Per-radio config files populated as Don customizes Home, defines antennas, etc.

**Risks:** None new to multi-radio. Existing SmartLink path well-tested.

**What does NOT change:** Don's day-to-day operation. The refactor preserves
his exact UX. Multi-radio architecture is invisible to him (as it should be).

---

## 2. Justin — FLEX-8400 via SmartLink (existing path, no change)

Same as Don but on Mac, connecting inbound. JJF on his (Windows) test
machine sees his 8400 via SmartLink.

**Status:** Working today. Same status as Don — existing path preserved.

**Risks:** Justin's Mac-side operation goes through Dogpark or
SmartSDR-for-Mac for direct operation; JJF testing is from his Windows test
rig.

---

## 3. Noel — Kenwood TS-2000 (new path; primary cross-class testbed)

**Status:** New work. This is the first non-Flex backend ever in JJF.
Noel's TS-2000 is the conformance testbed (Phase 7).

### 3.1 Hardware prerequisites

Before software touches the radio, Noel has:

- TS-2000 powered on, in the shack
- A USB-to-serial adapter connected from PC to TS-2000's serial port
  (TS-2000 has a 9-pin DIN serial port; needs a USB-RS-232 adapter)
- Audio interface: SignaLink USB (or equivalent) connected to TS-2000's
  ACC port and to PC USB
- Driver for the serial adapter installed (Windows usually does this
  automatically; FTDI chipsets work cleanly)

**Accessibility note:** the physical hardware setup (plugging cables in)
isn't something JJF can solve. But all the *software* configuration must be
accessible. Noel handles physical setup himself or with sighted help where
needed; once cables are plugged in, JJF takes over.

### 3.2 Software path

1. **Install JJF.** Same NSIS installer; bundles `libhamlib-5.dll`. No
   separate Hamlib install required. No SmartScreen friction (signed
   binaries — code-signing milestone).

2. **First run, no Flex radios on network.** JJF's first-run flow detects
   no Flex on LAN. Instead of forcing SmartLink credential entry, the UI
   surfaces:

   > "No Flex radio found on your network. JJ Flexible also supports
   > Kenwood, Yaesu, Icom, and many other radios. Would you like to add a
   > radio now, or skip and add one later?"

   Noel picks "Add a radio."

3. **Pick model.** "Add a Radio" dialog opens. Tabbed sections:
   - "FlexRadio (network)"
   - "Other radios (CAT)"

   Noel picks the second tab. List of Hamlib-supported radios, alphabetized
   by manufacturer, then model. Search box at top:

   > Search: [Ts-2000_____]

   Filters to "Kenwood TS-2000." Noel selects it.

4. **Connection config.** Next dialog:

   > Configure connection to: Kenwood TS-2000
   >
   > Nickname for this radio: [TS-2000 Shack_____]
   > Serial port: [▼ COM3 (USB Serial Port)]
   > Baud rate: [▼ 9600 (TS-2000 default)]
   > Data bits: [▼ 8]
   > Stop bits: [▼ 1]
   > Parity: [▼ None]
   > Hardware flow control: [☐ off]
   >
   > Advanced: [▶ Show advanced options ◀]
   >   - Use Hamlib transceive: [▼ Default (radio-specified)]
   >   - Connection retry attempts: [▼ 3]
   >
   > [ Test connection ]    [ Save and continue ]    [ Cancel ]

   The serial port dropdown enumerates available COM ports. Noel knows COM3
   is his adapter (he set it up). The defaults match TS-2000 manual.

   **"Test connection"** button does a `rig_init` + `rig_open` and reads
   the model ID via `ID;`. If success, announces "Connected to Kenwood
   TS-2000 firmware version X.YY." If failure, announces specific reason
   (port busy, no response, model mismatch, etc.).

5. **Audio config dialog (Phase 6).** Detected: Noel hasn't configured
   audio for this radio. Audio dialog opens:

   > Audio for: TS-2000 Shack
   >
   > Receive (radio audio coming in to PC):
   > [▼ Microphone (USB Audio CODEC)]
   >   Test Receive: [Listen for 5 seconds]
   >
   > Transmit (PC audio going out to radio):
   > [▼ Speakers (USB Audio CODEC)]
   >   Test Transmit: [Send 1 kHz tone for 1 second]
   >
   > Optional data port: [☐ This radio uses a data port]
   >
   > [Save and connect]

   Noel picks the SignaLink (whatever Windows calls it). "Test Receive"
   audibly confirms. "Test Transmit" — this needs Noel to have radio
   listening; the 1 kHz tone shows up as RX audio if the test loops back
   correctly.

6. **Connect.** With config saved, JJF connects fully. Capability
   introspection runs. UI renders:
   - Frequency / mode / VFO controls
   - PTT button
   - S-meter
   - Memory channel selector
   - Satellite mode toggle (because TS-2000 supports it)
   - VFO B (because TS-2000 has main + sub)
   - ATU controls (because TS-2000 has built-in ATU)

   Sections that DON'T render (capability flags absent):
   - SmartLink-related panels
   - Multi-slice management
   - Panadapter / waterfall
   - DAX channel routing
   - Diversity (TS-2000 doesn't have it)

7. **Operate.** Noel can now tune, send CW, transmit, recall memories — all
   the standard operations. Per Phase 7 conformance, ~80 specific behaviors
   verified.

### 3.3 The audio walkthrough

Noel's first connection has audio configured but not yet tested under
operating conditions. The expected experience:

- Tune to 14.250 USB during a daytime band opening
- Hear voice come through PC speakers (via NR pipeline if enabled — same
  pipeline as Flex 6300 audio uses)
- Press PTT (CAT command from JJF, audio from PC mic via SignaLink)
- Voice goes out
- Operator response heard back

If audio isn't flowing one direction, the symptom is silence; the
"Test Receive" / "Test Transmit" diagnostic in the audio dialog tells the
user which side is broken.

### 3.4 Risks for Noel's onboarding

- **USB-to-serial adapter timing.** TS-2000 doesn't require hardware flow
  control, but timing on cheap adapters varies. The retry/backoff in
  HamlibBackend handles transient failures.
- **Audio device naming.** "Microphone (USB Audio CODEC)" is generic. If
  Noel has multiple USB audio devices, he picks the right one. If wrong,
  "Test Receive" fails clearly.
- **Hamlib transceive bugs.** If TS-2000's transceive is flaky, JJF auto-
  falls-back to polling. The per-radio config flag `useHamlibTransceive`
  lets the user explicitly disable transceive if needed.
- **JJF UI doesn't yet know "VFO B" semantics for non-Flex radios.** If
  Noel's UI mental model assumes "VFO B = sub receiver" (as on TS-2000)
  but JJF surfaces it as "slice 1," the terminology might confuse him.
  Document explicitly: "On TS-2000, slice 0 is main, slice 1 is sub."

### 3.5 Onboarding flow quality bar

Noel's first connection should take **less than 10 minutes** from "open
JJF" to "operating on the air with screen-reader feedback." If it takes
longer, the friction-tax principle is being violated.

If a step is silent (NVDA doesn't say what's happening), that's a bug,
not a UX choice — fix it.

---

## 4. Doug — Kenwood TM-V71A (new path; VHF/UHF FM mobile class)

Doug's onboarding is similar to Noel's but with VHF/UHF mobile-class
specifics.

### 4.1 Hardware prerequisites

- TM-V71A connected via mini-DIN 8 cable to USB-serial adapter
- USB adapter must support hardware RTS/CTS flow control, OR cable is
  modified with the RTS-to-CTS loopback workaround per Doug's research
- Audio interface: SignaLink USB or equivalent on TM-V71A's data port for
  packet/digital, plus speaker audio either through PC or radio's speaker

### 4.2 Software path

Steps 1-3 same as Noel — install, first-run, pick model. Doug picks
"Kenwood TM-V71A" from the list.

**Step 4 (connection config) differs:**

> Configure connection to: Kenwood TM-V71A
>
> Nickname: [TM-V71A Mobile_____]
> Serial port: [▼ COM4]
> Baud rate: [▼ 57600 (TM-V71A default)]
> ...
> Hardware flow control: [☑ on (REQUIRED for TM-V71A)]
>   ⚠ Without flow control, your radio will accept commands but never respond.
>     If your USB-serial adapter doesn't support hardware flow control, you'll
>     need a cable modification (RTS-to-CTS loopback). Check your adapter
>     before proceeding.

The "Hardware flow control" checkbox is checked by default for TM-V71A
because Doug's research confirmed it's required. The radio-specific
guidance text appears because the per-radio default (in JJF's static known-
quirks table) flags it.

If Doug's adapter doesn't support flow control AND he hasn't done the
loopback, the connection silently fails — no response from radio. JJF's
"Test connection" surfaces this with the message:

> No response from radio. This is most often caused by missing hardware flow
> control. Check your USB-serial adapter and cable. (See: TM-V71A flow
> control help.)

The "help" link opens a JJF help window with the loopback workaround
documented (text + cable diagram description for screen reader).

**Step 5 (audio):** Doug's TM-V71A primary use is voice on the radio's
speaker, with the PC connected via the data port for digital modes (packet,
APRS). Audio dialog defaults the "Optional data port" checkbox ON for
TM-V71A class, with explanatory text:

> TM-V71A's data port is typically used for packet and APRS. Configure
> data port audio if you plan to run digital modes through PC software. If
> you only need voice operation through the radio's speaker, skip this
> section.

Doug picks the device for data port audio if relevant; voice operation works
without it.

**Step 6 (connect, capability introspection, UI render):**

UI sections that DO render for TM-V71A:
- Band A frequency / mode (FM / NFM / AM)
- Band B frequency / mode
- PTT (selectable: TX from Band A or Band B per `BC` command shape)
- CTRL band selection
- Cross-band repeat toggle (if `TY` reports it as supported)
- Memory channel selector with TM-V71A's 1000-channel range
- Group names (TM-V71A has 8 named channel groups)
- CTCSS / DCS tone selectors (by index, with frequency labels)
- Built-in TNC controls (enable/disable, 1200 vs 9600 baud)
- Squelch level
- Power preset (High / Mid / Low)

UI sections that do NOT render:
- HF-class controls (no HF on TM-V71A)
- SSB / CW (radio doesn't support)
- ATU (no built-in ATU)
- Multi-slice (capability absent)
- Diversity / panadapter / SmartLink (Flex-only)

### 4.3 Risks for Doug's onboarding

- **Hardware flow control trap.** Documented above; the connection-test
  failure must surface this clearly.
- **Memory channel write speed.** 1000 channels via CAT (`ME` per channel)
  is slow. Bulk operations via programming-mode binary protocol would be
  faster but Hamlib's `tmd710.c` doesn't expose programming mode. Document
  expected times: full memory dump might take 5+ minutes.
- **Region differences.** Doug's research notes TM-V71A US version vs EU
  version have different default DCS table ordering. Hamlib handles this
  internally; JJF shouldn't need to. But verify.
- **Doug operates from a moving vehicle sometimes.** JJF audio routing must
  not assume always-connected mic; auto-recovery on USB device hot-plug
  matters.

### 4.4 Doug-specific UX considerations

- TM-V71A has channel "groups" (PA repeaters, calling, etc.) — JJF should
  surface these as a navigation level above individual channels. Per-radio
  config file `groups-names.json` (Phase 5).
- APRS beacon trigger (`BE` command) is a one-shot button — clearly labeled
  in UI when capability flag `AprsBuiltIn` is true.
- Memory naming length is 6 chars on TM-V71A vs 8 on TM-D710. Per-radio
  config schema knows; UI input field caps at 6 chars with a "user label
  can be longer in JJF" alternative.

---

## 5. Mark — Kenwood TS-590G (new path; HF transceiver class with neuropathy considerations)

Mark's onboarding has **two accessibility axes**: blindness AND neuropathy
(reduced fine-motor accuracy). Setup must accommodate both.

### 5.1 Hardware prerequisites

- TS-590G connected via USB cable (TS-590SG has built-in USB; TS-590S has
  serial only, requires adapter)
- For TS-590SG: USB driver auto-installs; appears as "Silicon Labs
  CP210x" device + "USB Audio CODEC" combo
- For TS-590S: serial adapter + external sound interface

### 5.2 Software path

Steps 1-3 same. Mark picks "Kenwood TS-590SG" or "Kenwood TS-590S" depending
on his radio.

**Step 4 (connection config) differs slightly per variant:**

For TS-590SG (USB):
> Serial port: [▼ COM5 (Silicon Labs CP210x)]
> Baud rate: [▼ 115200 (TS-590SG USB default)]
> Hardware flow control: [☐ off (TS-590SG doesn't require it)]

For TS-590S (serial adapter):
> Serial port: [▼ COM3]
> Baud rate: [▼ 9600 or 38400 — check TS-590 menu 38]

Defaults are correct for the most common config. Hamlib's
`rigs/kenwood/ts590.c` handles both variants via the `rig_model_t`.

**Step 5 (audio):**

For TS-590SG with USB CODEC:
> Receive: [▼ Microphone (USB Audio CODEC) — TS-590SG]
> Transmit: [▼ Speakers (USB Audio CODEC) — TS-590SG]

The radio's USB audio is auto-detected; the dropdown highlights "TS-590SG"
as the most likely match. Mark picks it; "Test Receive" confirms.

For TS-590S with external sound interface:
> Receive: [▼ Microphone (SignaLink)]
> Transmit: [▼ Speakers (SignaLink)]

**Step 6 (connect):** UI renders all HF-class controls — frequency, mode
(SSB / CW / AM / FM / digital), passband, RIT, XIT, ATU, RX antenna,
notch, NR, NB, etc. Memory channels, AGC mode, mic gain, etc.

### 5.3 Neuropathy-specific UX

JJF design rules for Mark (per `project_kenwood_590g_commitment.md`):

- **Adjacent-key forgiveness:** keystroke deconflict already in JJF — same-
  key-same-action across Home fields. So Mark's mis-tabs are recoverable.
- **Adjustments are small-increment and reversible:** frequency tuning by
  step rather than wide jumps. RIT clear is one keystroke (`Ctrl+Shift+R`
  or whatever).
- **Destructive actions confirmed:** deleting a memory channel, factory
  reset, connection reset all confirmed via dialog.
- **Preferences preserved across sessions:** Mark configures once; settings
  stick.
- **Larger fonts available** for sighted users with macular degeneration
  or related visual issues — JJF's font/zoom controls (existing).

### 5.4 Risks for Mark's onboarding

- **Hamlib version mismatch.** If Mark's PC has an older Hamlib (or a
  conflicting install), the bundled DLL must take precedence. JJF loads
  from its install directory explicitly; OS-side installs are ignored.
- **Audio device naming on TS-590SG.** Windows often names the radio's USB
  audio "USB Audio CODEC" without the radio name — confusing if Mark has
  multiple USB audio devices. The audio test step is critical.
- **Mark migrating from BlindCat.** He has BlindCat installed; might be
  used to BlindCat's quirks. JJF's UX intentionally differs (per the
  anti-pattern checklist `project_anti_patterns_from_blindcat.md`). Walk
  Mark through the differences gently:
  - JJF doesn't power off the radio at exit (BlindCat does)
  - JJF's settings dialog is reachable always, not just first-run
  - JJF logs everything for remote diagnosis
  - JJF announces every key it processes (no silent keystrokes)

### 5.5 The "moment of joy" for Mark

The first time Mark tunes to 14.250 USB, hears a QSO, and can identify the
station via screen reader announcement of frequency + mode + S-meter — that's
the moment that justifies the friction-tax investment. Every subsequent
session is normal operation; that first one is the proof.

---

## 6. Cross-tester risks and shared concerns

### 6.1 Hamlib bundled-DLL versioning

All non-Flex testers depend on JJF's bundled Hamlib. If we ship 4.7.4 today
and 4.8.0 fixes a TM-V71A regression, Doug needs the update. JJF's update
mechanism (Sprint 29 updater vision) handles this — the bundled DLL is part
of the install payload, updated via the standard updater.

### 6.2 USB serial adapter quality

Cheap adapters (Prolific PL2303 clones) are flaky. FTDI chips work cleanly.
JJF can't fix bad adapters but should diagnose:

- Connection-test timeout → "Try a different USB-serial adapter (FTDI-based
  recommended)"
- Erratic responses → log the error pattern in trace; user can share with
  support

### 6.3 Multiple radios, one user

Noel has Flex 6300, future 8600, AND TS-2000. JJF handles this naturally:

- Each radio in its own `radios\<radio-id>\` config folder
- Picker shows all three; user picks active radio per session
- (Future) tabbed multi-active UI lets all three be connected simultaneously

The architecture doesn't add complexity for Noel beyond "more radios in the
list."

### 6.4 Tester support escalation

When a tester hits an issue:

1. JJF's trace files capture what happened (Sprint 29 trace persistence)
2. Tester opens "Send support email" (Sprint 29 crash-reporter)
3. Trace + crash report flow to Noel
4. Noel diagnoses; if a JJF bug, fixes; if a Hamlib bug, patches/upstreams

The friction-tax of "tester reports a problem" must be near-zero — single
button to package and send. Existing Sprint 29 work ships this.

---

## 7. Onboarding documentation we need to write

The architecture's success depends on tester-facing docs:

### 7.1 Per-radio quick-start guides

For each radio class, a short guide:
- Required hardware (cables, adapters, audio interfaces)
- Driver install steps
- JJF's connection wizard walkthrough
- Audio config walkthrough
- First-operation checklist
- Troubleshooting common issues

### 7.2 Class-level reference

For each class (HF transceiver, all-mode all-band, FM mobile), a reference
that documents the JJF UI surface for that class and any class-specific
concepts (slices vs VFOs, satellite mode operation, cross-band repeat,
etc.).

### 7.3 Migration guides for incoming JJ Radio users

JJ Radio's existing user base will eventually move to JJ Flexible. Docs
that explain:
- How JJ Flexible is different (and why)
- How to import settings from JJ Radio (where applicable)
- What's better and what's different

This is forward-looking — when JJ Radio's user base is told "JJ Flex now
supports your radio," the migration path needs to be smooth.

### 7.4 The accessibility commitment in user-facing docs

Per `project_anti_patterns_from_blindcat.md`, the project's tone matters.
Docs should:
- Acknowledge accessibility as a first-class commitment, not "a feature"
- Speak to the user as a peer, not a charity case
- Be honest about limitations ("This radio's full programming-mode bulk
  memory ops aren't supported yet — we're working on it")

---

## 8. Sequencing tester onboarding within the multi-radio arc

The order in which testers come online:

1. **Noel + TS-2000** — primary testbed. Onboarding pass during Phase 7
   conformance work.
2. **Mark + TS-590G** — second test, validates HF transceiver class. After
   TS-2000 conformance passes.
3. **Doug + TM-V71A** — third test, validates VHF/UHF FM mobile class.
   After Mark's TS-590 is steady.

Don and Justin (Flex testers) continue through the existing path; they
provide regression coverage for the FlexLibBackend refactor.

This sequencing matches the tester-class breadth Phase 2 enumerated.

---

## 9. Open questions for Noel

1. **Hardware acquisition for testers.** Mark and Doug provide their own
   radios, adapters, audio interfaces. JJF doesn't ship hardware. Confirm
   each tester has the cables and interfaces needed before sprint kickoff.

2. **Travel/in-person setup.** If onboarding falters via remote support,
   would in-person setup help? Noel's TS-2000 is at his QTH, Mark and Doug
   are remote. Default to remote only; reserve in-person as escalation
   if the friction tax surfaces despite our design.

3. **Tester-cohort expansion.** When JJ Radio's user base starts migrating,
   how do new testers come online? Recommendation: a "request testing
   access" form on the website, vetted by Noel before adding to the tester
   roster. Avoids overwhelming Noel-as-support-team.

4. **Onboarding-doc authoring cadence.** Each new radio class added needs a
   quick-start guide. Recommendation: write the guide AS the implementation
   sprint for that class lands, not after; it's the user-facing test of
   "does this work end-to-end accessibly?" If the guide is too hard to
   write, the UX needs more polish before ship.

---

## 10. Forward references

This document feeds:

- **Phase 9 (Architecture synthesis)** — tester onboarding is the human-side
  validation of the technical architecture
- **Future implementation sprint plans** — onboarding milestones become exit
  criteria for the implementation sprints

---

## Document Status

**Phase 8 of 10 complete.** Five tester profiles with end-to-end onboarding
walkthroughs, accessibility callouts at every step, neuropathy-specific
considerations for Mark, Hamlib-specific quirks for TM-V71A and TS-2000.

**Estimated read time:** 18-22 minutes for full review; 10 minutes for
sections 3 (TS-2000) and 5 (Mark's TS-590) alone.
