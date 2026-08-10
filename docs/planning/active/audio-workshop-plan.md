# Audio Workshop hear-yourself plan (codename: mox-parrot-sidetone)

**Date:** 2026-08-07. **Status:** design ratified by Noel in the parallel audio-workshop session (Fable window); this file is the deliverable the orchestrator's queue entry points at (`research-queue.md` "Audio Workshop design conversation" → cut an implementation track from here). **Driver:** Don is adjusting his transmit audio right now and the adjust-and-hear loop is not one surface.

**Operating context (from the orchestrator's queue entry, ratified):** Don sends TX audio from his computer, not the radio mic; Noel will do the same; live testing likely runs against Don's radio because Noel has no dummy load. This shapes the whole design — see sections 3 and 6.

All file/line references verified on `track/flexlib-4220` on 2026-08-07.

---

## 1. Decisions ratified (Noel, 2026-08-07)

- **MOX comes to the Audio Workshop.** The TX Audio tab gets a keying control with guardrails so adjust-and-hear is genuinely one surface.
- **Quick record/playback gets exposed.** FlexLib's slice-level record/play (`Slice.RecordOn`, `Slice.PlayOn`) is unexposed in JJFlex and becomes the DAF-free way to hear your own audio (talk first, listen after). Noel also wants this seeding a broader future feature: recording QSOs, both transmit and receive, to files on the PC — bundling codecs is acceptable. That PC-side recorder is explicitly **future**, own design round (section 8).
- **The monitor section becomes mode-aware.** CW mode shows the CW monitor gain; phone modes show the sideband gain/pan. Cheap, same slice.
- **Don supplies his transverter-port procedure.** Questions doc: `docs/planning/for-don/2026-08-07-transverter-listening-trick.md`, already copied to his Dropbox folder (`JJFlexRadio\don\`). No loopback UI gets built until his answers and an on-radio verification session agree on the mechanism.
- **Scope boundary:** the Settings → Audio work is queue-burn **Track B** (Radio Outputs group, PC Audio checkbox, device picker rebuild, audio-troubleshooting help) and the audio audit hooks (Lineout `!PCAudio` gate, dead `LocalAudioMute`) are **Track A** — this plan touches none of them. Deconfliction notes in section 9.
- **The audio-workshop help page gets rewritten.** It currently describes output-device routing and independent multi-output levels that the dialog has never done (`docs/help/md/audio-workshop.md:3, 17-19`; confirmed by `radio-audio-settings-research.md` section 3). Audio **routing** as a real feature is wanted eventually but explicitly **not now** (Noel, 2026-08-07).
- **Worktree execution** per house SOP, as part of the queue-burn track set.

## 2. Current state, verified

The Audio Workshop (`Ctrl+Shift+W`, non-modal singleton, `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml.cs`) has three tabs. The TX Audio tab already holds the full sculpting chain in one tab order: mic gain/boost/bias, compander + level, speech processor + mode, TX filter low/high with width readout, and a TX Monitor section (enable, level, pan; lines 241-281) polling at 2 Hz. Every change speaks.

Underneath, in `Radios/FlexBase.cs`:

- `Monitor` → `Radio.TXMonitor` (7927-7935) — the master monitor enable, `transmit set mon=` on the wire.
- `SBMonitorLevel` → `TXSBMonitorGain` (7940), `SBMonitorPan` → `TXSBMonitorPan` (7952) — phone-mode monitor.
- `CWMonitorGain` → `TXCWMonitorGain` (7713) and internal `MonitorPan` → `TXCWMonitorPan` (7828) — CW-mode monitor, wrapped but not surfaced in the workshop. A `#if CWMonitor` local-sidetone subsystem also exists (enabled at line 7).
- `Transmit` → `Radio.Mox` (setter ~6183, readback 4926-4930) — software keying already exists.
- TX antenna list comes from the radio's own `Slice.TXAntList` (6441); internal string `TXAntenna` setter at 7019-7032 is a clean pass-through.
- `XmitPower` → `Radio.RFPower` with an existing save/restore pattern (`_savedRFPower`, 7963-7984) — precedent for low-power-during-checks.

**Found during verification prep (2026-08-07, changes the design): the keying guardrails already exist.** `JJFlexWpf/PttSafetyController.cs` is a full PTT safety subsystem: Ctrl+Space = TX while held, Shift+Space = TX lock, escalating warning states (Warning1/Warning2/OhCrap beeps), a license-aware `CanTransmitHereCheck` lockout, and a non-configurable 15-minute hard kill. `MainWindow.xaml.cs:2663` routes toggles through it. The Audio Check session must ride this controller, not grow its own timer stack — see section 3. The Transmit Controls dialog, by contrast, is rear-panel keying lines only (RCA/ACC, TX1/2/3 delays, hardware ALC) — no MOX or RF power surface exists in any dialog today.

In vendor FlexLib:

- `Slice.RecordOn` (Slice.cs:1603-1615, `slice set N record=`), `Slice.PlayOn` (1621-1637), `Slice.PlayEnabled` (1639-1645) — SmartSDR's Quick Record/Play, never exposed by JJFlex.
- `Radio.FullDuplexEnabled` (Radio.cs:10167-10176) — settable, for gating the loopback tier.
- `Xvtr.cs` — transverter band objects, available if the full trick needs a defined XVTR band.

History note: Jim's original code had exactly one transverter reference — hardcoding `"XVTR"` into the TX antenna list (`Flex6300Filters.cs:701` at import commit `e68dabc5`), deleted with the dead WinForms files in Sprint 11 (`074b2c78`). So "JJ tried this" = the XVTR TX-antenna route existed in Jim's UI and vanished in the WPF migration. Today's antenna plumbing reads the radio-reported list, so XVTR should reappear wherever the radio reports it — verify on hardware (section 6).

## 3. The core design: the Audio Check session

The thing Noel asked for by name: an easy way to say "start transmit," then, in easy tab order while transmitting, adjust settings and listen — by TX monitor or by the transverter loopback.

**Entry.** A "Start Audio Check" button at the top of the TX Audio tab, plus a Command Finder registration ("check my transmit audio"; keywords: audio, check, monitor, hear, myself, transmit, tx, test). No new global key binding in v1 (keeps the keyboard audit to the Command Finder keyword item).

**Listen-method choice** (a cycle field remembered per radio, conservative default = Monitor):

- **Monitor** — tier 1, works on every model, instant, colored (post-DSP, pre-RF tap). Session start ensures `TXMonitor` on and restores its prior state on stop.
- **Loopback** — tier 2, real RF path in-radio via the transverter port at milliwatt level. **Not built until Don's answers + on-radio verification** (sections 6-7). When it exists, it is also the "practice mode": talk without putting a signal on the air — doubly valuable given Noel has no dummy load.
- **Record and play back** — tier 3 in UI position but the recommended path over remote: no simultaneous talk-and-listen, so no delayed-auditory-feedback problem.

**TX-source awareness (new, from the operating context).** Don and Noel both transmit PC-sourced audio over the PC-audio path, not the radio's mic jack. The radio-side DSP chain (compander, processor, TX filter, monitor) applies regardless of source, but **Mic Boost and Mic Bias are physical-jack controls** and whether `MicGain` acts on network-sourced audio needs verification (section 6). Design: the workshop reads the TX input source and annotates or de-emphasizes jack-only controls when TX audio comes from the PC ("Mic Boost — radio mic jack only, not in use"), keeping them out of the adjust loop's mental model without hiding state. The session-start speech names the source: "Transmitting, audio from this computer."

**Session flow.**

- Start speaks the safety line first: "Transmitting on 14.250 megahertz, 100 watts. Escape stops." (frequency + power always; over the Monitor method this is a real on-air signal). Then `Transmit = on`, focus lands on the first relevant control, and the existing tab order *is* the adjust ring — every control already speaks on change. No new focus machinery; the win is auto-focus plus keying, not a new widget set.
- **Low-power-during-checks option, default ON** (flexibility principle: togglable, conservative default): session start drops RF power to a floor value (10W or the radio minimum), restores on stop, and says so ("power reduced to 10 watts for the check"). Reuse the existing `_savedRFPower` save/restore pattern (`FlexBase.cs:7963-7984`). No dummy load in Noel's shack and none assumed in anyone's — an audio check should not blast full power by default.
- **Keying rides `PttSafetyController`, not a new timer stack.** The controller already owns hold-to-talk (Ctrl+Space), TX lock (Shift+Space), the escalating warning-beep ladder, the license-aware `CanTransmitHereCheck`, and the 15-minute hard kill. The session's Start button drives the controller's lock path so every existing safeguard applies unchanged; the session adds only its own spoken elapsed reminders and a shorter default soft timeout appropriate to a check (3 minutes, configurable later).
- **Escape is two-stage: first press unkeys** ("Transmit off") and stays in the dialog; second press closes it. Escape never leaves you transmitting — this extends the house Escape rule rather than bending it.
- Unkey unconditionally on: dialog close, radio disconnect, session teardown, timeout — all through the controller so state stays coherent. The session restores whatever state it changed (monitor enable, RF power, and later the loopback's antenna/slice arrangement).
- Remote awareness: when `RemoteRig`, session start adds "over remote, monitor audio arrives delayed — record and play back is recommended," because monitor audio rides the compressed RX stream back and delayed self-hearing actively disrupts speech. This is Don's actual situation and the likely test configuration (his radio, remote).

**Record-and-play flow** (pending semantics verification, section 6):

- "Record a test transmission" → speaks the safety line → keys and starts slice record → operator talks → press again (or 15-second cap) → unkey, stop record, auto-start playback → operator hears their actual audio → adjust → repeat. One button, a loop a tester can lean on.
- FlexBase grows thin wrappers for the active slice's `RecordOn`/`PlayOn`/`PlayEnabled`, same command-queue pattern as the monitor properties (~line 7940 region).

**Mode-aware monitor section.** In CW mode the section shows "CW Monitor Gain" (`CWMonitorGain`, and promote `MonitorPan` to public for CW pan); in phone modes the existing SB fields. Section header names the mode so the screen reader user knows which knob family they're on.

**TX-source semantics and the mic source picker (added from live testing, 2026-08-07).** Discovered at the radio: the transmit source FOLLOWS THE KEYING METHOD. Software keying (Shift+Space lock / Ctrl+Space hold) transmits from the radio's mic-in setting — which JJFlex silently forces to "PC" when PC audio enables (`FlexBase.cs:9147`); the hand mic's own PTT button overrides the setting radio-side and uses the hand mic. Nobody documented this anywhere and it read as chaos until decoded. Work items:

- **Mic source picker** in the workshop's Microphone section ("Transmit audio from:"), fed by radio-reported `MicInputList`/`MicInput` (Radio.cs:8864-8890) — no JJFlex surface exposes this today. Directly enables Noel's ask: TX lock + hand mic as source, both hands free.
- **Key-up speaks the source** ("Transmitting from hand mic" / "from PC audio") every time, both software keying and detected PTT.
- **PTT reachability:** Shift+Space is registered Global (`KeyCommands.cs:1069` region) but modal dialogs swallow it silently — works only in the tuning/slice area (and, verified, the non-modal workshop). Violates no-silent-keystrokes; for remote operators "can't key while a dialog is open" is real. Decide: forward PTT chords from modal dialogs, or announce unavailability.
- **Hold-to-talk discoverability:** Ctrl+Space (TX while held) exists in PttSafetyController and Noel — the project's own operator — did not know. Verify it's in `keyboard-reference.md` and say it in the workshop help.

**PTT reachability tiers (RATIFIED by Noel 2026-08-07, with his amendments).** Why Shift+Space is trapped today: Space is a loaded key (slice toggle in the grid, control activation, literal typing in text fields, heavy screen reader use) — only the tuning grid's handler processes the chord. The design:

- **Hold, app-wide:** Ctrl+Space (TX while held) via one preview-key handler on the `JJFlexDialog` base class + main window + workshop. Types nothing in a TextBox, so it works everywhere inside JJFlex as-is.
- **Lock, app-wide: Ctrl+Shift+Space — Noel's pick, and the friction is the point.** A lock chord "needs to not be too easy to do"; deliberate awkwardness prevents accidental latch even with the timer ladder behind it. Never types in any control, so it works in text fields too. Open sub-question for Noel: retire the tuning grid's Shift+Space lock so there is exactly ONE lock chord everywhere (recommended — uniform system, changelog heads-up for the key removal per keyboard-audit rules), or keep it as a grid-local alias.
- **System-global (outside the app — the remote operator's only keying): user agency over safety paternalism.** Noel's ruling: "we can't protect people from themselves — tell them the risk, they're in control" (flexibility principle applied to safety UX). So the global tier offers BOTH modes as separately configurable, individually optional bindings — e.g., F12 = PTT while held, Shift+F12 = latch toggle — each off by default, each user-remappable. Choosing the latch binding gets informed-consent warning copy (invisible stuck-TX risk with focus elsewhere), then respects the choice. Both ride the PttSafetyController ladder + release watchdog (hold mode: can't confirm key down → unkey); every event speaks (down = "Transmitting from <source>", up = "Standby", periodic still-keyed reminders on latch).
- **Future socket:** hardware PTT (USB footswitch / serial DTR) enters the same controller — hold semantics by nature.

**The Global Radio Layer / Invisible Interface (Noel's vision, 2026-08-07, expanded — "operate your radio from any freaking where"; own design round, flagship candidate).** A system-global layered keyboard interface, the pattern blind users already know from JAWS layered keystrokes (Insert+Space, then a letter), NVDA add-on command layers, and the invisible interfaces of blind-first Mastodon clients (TweeseCake, Fastsm) — **two keymaps over one capability set: the windowed/visible interface and the invisible one, toggleable.**

- **Architecture (the load-bearing decision): the layer is a SECOND KEYMAP over the existing KeyCommands registry, not a hardcoded command list.** Any registered `CommandValues` command becomes mappable into the invisible interface by configuration — "we can always add more" is true by construction, and user remapping rides the existing binding system. Engineering shape: a new global scope/keymap concept beside today's `KeyScope`, entered via the global "JJ key."
- **Not a minimal set** (Noel's explicit correction): ship defaults covering what you use while operating or LISTENING — PTT/latch, tune entry (T → type a frequency), fine tune up/down, slice up/down, slice select, slice mute, frequency announce, volume, "focus JJ Flexible" (summon the app window), exit. No chord monsters ("no Ctrl+Windows+Alt+Numlock+S+T+L") — the JJ key enters the layer, single keys do the work.
- **The listener is a first-class user:** JJ Flexible Connect will let non-hams receive on a Flex to experience what Flexes are like — those users may live in the invisible interface entirely, listening while doing other things. Listening commands (slice select/mute, volume, tuning) are not secondary to TX commands; for the Connect audience they ARE the interface.
- **Engineering notes for the design round:** keyboard capture while the layer is active (low-level hook swallowing keys before the focused app AND coexisting with NVDA/JAWS's own hooks — needs a half-day spike; hook ordering is LIFO and screen readers hook too; fallback = registered hotkeys per mapped command), auto-exit policy, entry-chord choice ("the JJ key," configurable).
- The tier-3 PTT bindings above are the FOUNDATION SLICE — same hook infrastructure; build tier 3 with the layer in mind. Route to the orchestrator as its own future plan file when ratified into the roadmap.

## 4. The transverter loopback — VERIFIED LIVE on the 8600 (2026-08-07)

**It works, and Noel heard exactly how he sounds** ("you can totally tell exactly how you sound — amazing"): his actual transmitted signal through the real DSP chain (processing audibly present), modulated, demodulated on a second slice, inside one radio with zero antennas connected. Slight delay inherent to the path; fine for listening.

**The verified recipe (8600):**

1. `Radio.FullDuplexEnabled = 1` — **the gate.** With it off (factory default), keying mutes every receiver and the loopback is impossible. JJFlex exposes it nowhere; for the test it was set via raw LAN command (`radio set full_duplex_enabled=1`, `R1|0|` success). The automation sets and restores it.
2. TX antenna = XVT A (per-slice, on the TX slice).
3. Second slice, same frequency, same mode, **receive antenna = XVT A — same port worked**; no XVT B fallback needed at this power.
4. RF power = 1 watt (0 was never tested for the loop; 1W confirmed).
5. **TX Monitor OFF — part of the recipe.** Monitor on stacks the instant DSP tap over the delayed loopback into an echo; off yields the clean real signal. (Noel's live refinement — he first reached for slice muting, then found monitor-off is the whole fix; no slice muting needed since the TX slice's own RX antenna isn't the XVT port.)
6. Key (hand-mic PTT in the test; software keying equivalent once the mic source picker exists) and talk.

**Automation spec for the "Loopback check" button:** snapshot FDX flag + TX antenna + monitor state + RF power + slice roster → set all six recipe steps → announce "Loopback check ready — transmitting at one watt into the transverter port; you'll hear your actual signal" → teardown restores every saved value and removes the ears-slice. Gate on: 2 SCUs (capability check), `AvailableSlices >= 1`, XVT port present in the radio-reported TX antenna list. Feature Availability tab explains absence on radios that can't.

**Still open (Don + follow-ups):**

- Don's 1-SCU 6300: `FullDuplexEnabled` semantics there, and whether his procedure differs — his answers still gate the 6300/8400-class story (the 2-SCU recipe above is proven for 6600/6700/8600-class). **PROVENANCE CORRECTED (Noel, 2026-08-07 afternoon): Don has NEVER done the trick on his 6300 — he learned it from a YouTube video, likely of a 6600 (2-SCU).** No evidence exists anywhere for 1-SCU loopback; the 1-SCU case rests entirely on this morning's FDX-off simulation on the 8600 until a real 6300 test runs (Don's radio, with his blessing, instrumented build or guided session — he shifts from explaining the procedure to BEING the experiment). ~~Ask Don for the YouTube link — the canonical 6600 recipe and its level settings are useful prior art.~~ **CORRECTED AGAIN by Don's own answers (2026-08-08, section 4c): there is no video and no link — it was an in-person live demo by another ham on an unidentified 6000-series radio. The ask is moot; what came back instead is a level recipe. See section 4c.** Original (now-corrected) note follows: ~~Do NOT assume 1-SCU can't loop (Noel, 2026-08-07): Don has two slices + an XVTR port and has likely done this before.~~ But know the anatomy when reasoning about it: SLICES ARE NOT RECEIVERS — an SCU is the physical chain (port, preselectors, one ADC digitizing a wide swath) and slices are software channels carved from that one stream, so two slices on a 6300 are one receiver heard twice, and "one slice listens while one transmits" reduces to "does the single receive chain survive TX." Candidate reasons it may not: (a) T/R switching / front-end protection (real at 100W, arguably paranoia at XVTR milliwatts), (b) the commonly cited one — during TX the radio borrows a receive chain for its own TX feedback/metering loop; 2-SCU radios have a spare, 1-SCU radios don't (unverified how hard this commitment is). The `full_duplex_enabled` flag exists radio-wide in the protocol regardless, so a 6300 has exactly three outcomes: command refused / accepted-but-inert / accepted-and-works (gate was policy). One raw wire command against Don's 6300 (with his blessing) discriminates. If 1-SCU truly cannot RX-during-TX, the record-then-play path (if the buffer captures monitor audio) is the 1-SCU fallback — **at monitor fidelity only (Noel's correction, 2026-08-07): record is a CAPTURE MECHANISM, not a fidelity tier — it inherits the fidelity of whatever the slice hears.** Record-of-loopback = ground truth, DAF-free (the crown jewel on capable radios); record-of-monitor = the monitor's coloration bottled (still useful for remote DAF-avoidance, labeled honestly). Per Noel's ears the monitor does NOT carry the full processing — so on 1-SCU radios without FDX, **the only ground-truth path is an external receiver on the TX frequency: elevate SDR-listen (WebSDR/KiwiSDR tuned to your TX freq) from someday-vision to a first-class audio-check tier** — it is what Don already does by hand today, and for 1-SCU owners it is the only truth available. Empirical bonus for the track: record the monitor, record the loopback, play both back-to-back — documents the monitor's coloration objectively in the radio's own voice.
- ~~Whether RF power 0 also drives the XVT port enough to loop~~ **ANSWERED: no — at power 0 the loop is silent; 1 watt (the integer floor above zero) is required.** Context that makes 1 fine: `Radio.RFPower` is an int (Radio.cs:8467, whole watts only — Noel's fractional-watts ask is impossible on the main control), but the XVT port is a milliwatt-class output (~+10 dBm max) the slider maps onto proportionally, so "1" is already microscopic at the jack. For precision drive later: `Xvtr.MaxPower` on defined transverter bands is a double in dBm, -10.0 to +10.0 in hundredths (Xvtr.cs:169-202). Check whether the power value field accepts typed digits; if not, work item.
- **Reframing from Noel, live: the loopback is also a transmitter self-test** ("I'm glad I now know my exciter on A/B works") — one PTT press proves DSP → modulator → exciter → port routing with no antenna. Carry this into the help page and the feature's positioning: "check my audio" and "is my radio actually transmitting" are the same button.
- ~~No `Xvtr.cs` band definition was needed — plain antenna selection sufficed. Note for the docs: the "set the port to something very low" step in folklore appears to be the RF power floor, not an XVTR band frequency.~~ **REOPENED by Don's answers (2026-08-08): no band definition was needed to make a signal appear, but the demo Don witnessed defined one — "he put the radio in transverter mode and that allowed him to set the transmitter to 100 milliwatts." The folklore step is an XVTR band after all, and it is how you get sub-watt drive. Section 4c.**
- What leaks over the air at 1W into XVT with antennas connected — caveat wording for the help page.

**Live finding (Noel, 2026-08-07): Ctrl+Shift+W did not open the workshop — it "changed units."** The default binding is correct (`KeyCommands.cs:1069`, Global scope → OpenAudioWorkshop), so something shadowed it: either a control-local units handler swallowing the chord while focus was in a ScreenFields value field (a raw `case Keys.W:` handler exists near `KeyCommands.cs:1958` — investigate), or a saved user keymap override. Audit item for this track: no control-local handler may shadow a Global-scope chord; every bound key speaks its true action in every state (no-silent-keystrokes rule — this one spoke, but spoke the wrong command). Command Finder → "workshop" is the reliable door meanwhile.

## 4a. Menu-parity audit + XVTR-aware power control (Noel, live session, 2026-08-07 — handoff item)

Sparked by the loopback session: the actionable radio controls in ScreenFields (transmit, receive, antenna sections) largely have no menu equivalents — ScreenFields' own menu just expands sections rather than offering actions. Noel uses field expanders constantly but wants the *addressable* path too: **Alt+R → T (Transmit) → P (Power)** should walk a menu with accelerators into a Power dialog. Two work items:

- **Menu-parity audit:** every actionable ScreenFields control gets a menu path with accelerator keys. Reality check from the code: TX Antenna / RX Antenna submenus already EXIST in NativeMenuBar (~line 685-707, checkable, spoken confirmation, built from radio-reported lists) — but Noel has never met them, so the audit is part "add missing items" (power has no menu path anywhere) and part "make existing items findable / verify every menu mode builds them" (the dispatch paths are not unified — four parallel paths per memory). Audit-and-change item, **routed to the orchestrator** (Noel: pass to Phil the first) for filing into the track set — it is app-wide UI architecture, bigger than the audio track.

**Shared-radio courtesy (Noel, same session): keying should not interrupt other clients' listening.** On a shared radio (MultiFlex — e.g., a listen-only guest connected while the owner operates), every listener's RX mutes whenever the transmitting operator keys, because full duplex defaults off. On 2-SCU radios, `FullDuplexEnabled` is exactly the mechanism that would keep listeners' slices alive through the owner's transmissions. Design input for the sharing/scheduling/Connect vision (TX-is-a-mutex memory, slice camping, MultiFlex scheduling): a "keep listeners listening" policy on shared 2-SCU radios — with the front-end/antenna caveats thought through (a listener on a nearby antenna will hear the TX; their choice). Not audio-track scope; flag for the Connect design inputs section of the queue.
- **XVTR-aware power control:** the Power dialog (menu path) and the ScreenFields power field both switch to milliwatt/dBm entry (decimal, `Xvtr.MaxPower` semantics) when the selected TX antenna is a transverter port; integer watts otherwise. The radio's own design agrees — fine drive control exists only in the XVTR band definition because dBm is the transverter world's unit and mixer overdrive is the classic transverter killer.

- **RATIFIED (Noel, 2026-08-08): we build our own trackable dBm/mW slider.** Established first by code search: **there is no milliwatt-denominated property anywhere in FlexLib.** The complete transmit-power surface is `Radio.RFPower` (int, 0-100, whole watts), `Radio.MaxPowerLevel` (int 0-100, "relative, non-linear", PA-scale), `Radio.TunePower` (int 0-100), `Slice.MaxInternalPaPowerWatts` (platform-set), and `Xvtr.MaxPower` (double, dBm). So the only fine drive control in existence is the dBm one, and nothing converts it to the unit an operator actually thinks in. Noel's design call: **an arrowable slider that speaks BOTH units.** Notes for whoever builds it:

  - Conversion is `mW = 10^(dBm/10)`. Landmarks across the usable span: −10 dBm = 0.1 mW, 0 dBm = 1 mW, +8 dBm = 6.3 mW, +10 dBm = 10 mW, +15 dBm = 31.6 mW.
  - **Speak both** on every change ("minus three dBm, half a milliwatt"). dBm is the wire unit and the one that makes the ladder linear to arrow through; mW is the one that means something. Neither alone is enough — this is the verbosity-channel argument applied to a single control.
  - **Compute the ceiling from the radio, never hardcode it.** The clamp is model- AND IF-dependent (`Xvtr.cs:169-208`): floor always −10.0 dBm; ceiling +15.0 dBm normally, +10.0 dBm on 6400/6400M/6600/6600M, +8.0 dBm when IF ≥ 80 MHz. Same discipline as `MaxSlices`/`DiversityIsAllowed` — ask the radio.
  - Step size: the wire format is hundredths of a dB (`max_power=` f2). 1 dB coarse steps give ~25 detents across the span, which is arrowable; offer a finer step for the linear-window hunt if the loopback work shows it matters.
  - **The loopback feature consumes this control** rather than growing its own drive logic — the automated Audio Check sets drive through the same path the operator can reach by hand, so what the feature does is inspectable and overridable.

## 4b. Confirmed crash + trace-growth findings (2026-08-07 afternoon — fold into this track's instructions)

- **CRASH (full stack in `Errors\JJFlexError-20260807-153513.zip`): AudioWorkshopDialog poll timer races radio teardown at app close.** `MeterTimer_Tick` → `PollTxAudio` (AudioWorkshopDialog.xaml.cs:300) → `FlexBase.get_MicGain` (FlexBase.cs:7839) → NullReferenceException on the nulled `theRadio`. Same family as the 2026-08-05 ActiveSlice getter sweep (`f406b4cc`, 39 sites) — that sweep covered slice-level getters; the TX-family getters (MicGain and siblings: boost, bias, compander, processor, TX filter edges, Monitor/SB gains) still dereference `theRadio` bare. Two-layer fix, both in this track's files: (1) null-conditional guards with defaults across the TX getter family in FlexBase; (2) the workshop stops `_meterTimer` when the RIG dies, not only when the dialog closes (today `SetRig(null)` stops it but nothing calls that on app-close teardown; the singleton outlives the radio). Dispatcher caught it (Terminating: False) so it presents as a spurious error dialog during exit — user-visible, not fatal.
- **Trace growth unbounded in-session: the live `JJFlexRadioTrace.txt` reached 11.7 GB during today's marathon.** Boot maintenance prunes archives, but nothing rotates or caps a LIVE trace mid-session. Design ratified with Noel (2026-08-07): (1) size-based rotation — at ~250-500 MB close the active file, zip it into the archive as a session PART, start fresh; long sessions become chains of parts, nothing ever needs splitting after the fact; (2) crash bundles attach the CURRENT PART (the tail — the evidence that matters), bounded by construction — today's bundle is missing the session trace precisely because the whole-file attach was impossible at 11.7 GB; (3) upload size policy — report text + trace tail always, the full-memory dump only under the server limit, else held locally with a spoken "saved here if support asks"; the dialog Noel saw ("couldn't save a stream of that size") was most plausibly the ~500 MB bundle's upload rejection, and a clear "saved fine, too big to auto-send" message is half the fix. Belongs to the diagnostics/trace surface rather than the audio track; orchestrator routes it.

## 4c. Don's answers landed (returned 2026-08-07 evening, absorbed 2026-08-08)

Full answered doc with verbatim quotes: `docs/planning/for-don/2026-08-07-transverter-listening-trick.md`. Six questions, all answered. What they change:

**Provenance, third and final correction.** No video, no link, and not Don's own operating: an **in-person live demo by another ham**, radio unidentified — "could have been a 6400 or 6600. It certainly predated the 8000 series." The guess straddles exactly the line the question was trying to settle (6400 = 1 SCU, 6600 = 2 SCU), so the 1-SCU question stays open. But it is a second independent sighting of live RX-during-TX on a 6000-series radio, and Don is unambiguous that it was live: "you heard it live during the demo not a recording."

**The level recipe — the most useful thing that came back.** The demo ham "put the radio in transverter mode and that allowed him to set the transmitter to 100 milliwatts," and "turned on the receiver maybe by using the separate receive antenna port (rca) jack not sure" (Don flags his own uncertainty on the second — hypothesis, not fact).

Both differences point the same direction, and it is the direction the FINAL VERDICT identified as the open problem. Noel's verified 8600 recipe ran **1 watt on the integer main power control into XVT A with the ears-slice on that same XVT A** and produced a massively overloaded receiver — the "simulacrum of a signal, basically splatter, can't be detuned" result. The demo ran roughly **10× less drive via a defined XVTR band** (dBm precision instead of whole watts) and possibly a **looser coupling path** (separate RX port instead of the adjacent transverter port). Both changes push energy toward the receiver's linear range.

**Numeric caveat, flagged rather than smoothed over: "100 milliwatts" is out of range on every model.** 100 mW is +20 dBm. The actual `Xvtr.MaxPower` clamp (`Xvtr.cs:169-208`, read on `track/flexlib-4220`) is:

- Floor is always **−10.0 dBm** (0.1 mW).
- Ceiling with IF below 80 MHz: **+15.0 dBm** (~31.6 mW) generally, but **+10.0 dBm** (10 mW) specifically on FLEX-6400/6400M/6600/6600M.
- Ceiling with IF at or above 80 MHz: **+8.0 dBm** (~6.3 mW).

So even the most permissive ceiling is 5 dB below Don's remembered figure — and note that the two models he guessed the demo was on (6400, 6600) are exactly the two with the *tightest* +10 dBm cap, an order of magnitude below "100 milliwatts." The number is almost certainly misremembered, or is a reading off some other display in transverter mode. Do not build to the figure. What survives, and is the load-bearing part: **sub-watt drive, set through transverter mode, at finer resolution than the integer-watt control.** Resolve the units at the radio during verification item 1b, not by reasoning about it.

**Noel's power-0 datapoint closes the argument that a level knob exists at all (2026-08-08).** Setting RF power to 0 definitively killed the loop signal; power 1 overdrove the receiver. Two points bracketing a monotonic response prove the coupling tracks drive level with real authority — the loop is not an all-or-nothing artifact, and "turn it down until it stops overloading" is a real move rather than a hope. The problem is purely that the main `RFPower` control is an int (`Radio.cs:8467`), so 0 and 1 are *adjacent* — there is no room between "nothing" and "too much" on that control. **That is precisely the gap the XVTR band's dBm `max_power` fills**, and it is independent corroboration of Don's demo recipe from Noel's own bench. The whole usable XVTR span (−10 to +15 dBm) sits at least 15 dB below the 1-watt (+30 dBm) setting that overloaded, giving roughly a 25 dB sweep to find the linear range in.

**Open mechanism question for item 1b:** whether `max_power` rescales the integer `RFPower` control onto the band's ceiling (so RFPower 1 with a low `max_power` yields sub-milliwatt drive — the fine control we want) or merely caps it. The command is `xvtr set <index> max_power=<dBm, f2>`; band creation is `xvtr create` (`Radio.RequestXvtr()`, `Radio.cs:5515-5518`). Determine empirically; do not assume.

So the plan's open question — "whether level management can upgrade this to clean demodulation" — now has an eyewitness datapoint saying yes, with a recipe. **Design consequence: the XVTR band definition moves from 'not needed' to the primary drive-control mechanism for the loopback tier** (`Xvtr.MaxPower`, `Xvtr.cs:169-202`, −10.0 to +10.0 dBm in hundredths). The "temp XVTR band auto-creation yes/no" queue question is effectively answered yes, pending verification. Verification protocol for the track: rerun the 8600 loopback at a defined XVTR band's dBm drive rather than 1 integer watt, and separately with the ears-slice on a different RX port, and score whether the overload coloring clears.

**Jim's version is closed: duplex was never achieved.** "It never worked. It did allow for the transverter and I believe the power could be set properly but duplex was not achieved." Don doesn't know whether Jim ever got it working on his own radio. This matches the code archaeology exactly — Jim's sole transverter artifact was the hardcoded `"XVTR"` TX-antenna string (`Flex6300Filters.cs:701`). The TX half shipped; the RX half was never built. Nothing to recover from Jim's code here; this is new construction.

**New hard requirement for the record tier: RX bandwidth must match TX bandwidth.** Don's stated condition for accepting the record-and-play fallback: "IF possible set the receivers band width to match that of the transmitter before recording or request the user to do so before they start." Actionable as specified — the session already snapshots and restores slice state, so setting the ears-slice filter to the TX filter's low/high edges (both already surfaced in the workshop) is the same save/restore pattern. Preference order per the friction-tax principle: **do it automatically and say so** ("receive filter matched to your transmit filter"), fall back to a spoken prompt only where the slice filter can't be set. This is a real fidelity requirement, not a nicety — a wide receive filter on a narrow transmitted signal adds noise that isn't in your audio, and a narrow one truncates the audio you're trying to judge.

**Don ranks the in-radio path ABOVE external SDR — a tension with the ratified honest-claim language.** His case, in his words: SDR receivers "vary a fair bit quality wise so sometimes their receiver characteristics are not great for checking high quality audio"; "some sdr interfaces don't always allow for control of the receivers band with due to accessibility issues"; band conditions and "qsb and qrm at times" can block the check entirely. Conclusion: "I trust the flexes receiver and audio chain to render my transmit audio most accurately... using your own equipment is the way to go if it can render the transmit audio accurately."

Section 4's ratified framing says the opposite ("An SDR connected to a radio on a real antenna remains the best test, full stop"). Both are right about different things and the conditional in Don's sentence is where they meet — **"if it can render the transmit audio accurately" is precisely the level-management question above.**

**Noel's resolution (2026-08-08): either tier is fine, because one of Don's three objections is ours to eliminate and we already plan to.** Don's case against SDR rests on (a) variable receiver quality, (b) no bandwidth control "due to accessibility issues," and (c) band conditions. Objection (b) is not a property of SDR — it is a property of *other people's web interfaces*. **When JJ Flex is the SDR client, we control the bandwidth**, and KiwiSDR support is already on the roadmap (`project_remote_services.md`; Remote Hams / KiwiSDR / WebSDR). The accessibility objection disappears the moment the SDR is one we support rather than one the user has to drive through someone else's inaccessible web UI.

What that leaves:

- *Absolute fidelity ranking* stands as ratified: a real receiver on a real antenna beats an in-radio loop. Don is not disputing the physics, and objections (a) and (c) are real but are properties of *which* SDR, not of the tier.
- **Product consequence: do NOT rank the tiers flatly in either direction.** The in-radio loop and a JJFlex-driven SDR are both legitimate everyday paths, chosen by circumstance — the loop when you want a no-antenna self-contained check or the bands are dead, the SDR when you want off-air truth. Help text says what each tier can and cannot prove (§4's fidelity map) instead of naming a winner.
- **Design carry-over to the SDR tier: the same RX-bandwidth-matching requirement applies.** Don's condition for the record tier ("set the receivers band width to match that of the transmitter") generalizes — when JJFlex drives a KiwiSDR for an audio check, it should set the Kiwi's passband to match the TX filter automatically, for exactly the same reason. One requirement, three tiers. Note this in the KiwiSDR design round so the audio-check integration is designed in rather than bolted on.
- The level-management work above is still worth doing on its own merits — it decides whether the loop is a faithful listen or a "simulacrum" — but it is no longer load-bearing for whether SDR-listen deserves a place in the ladder. It does.

**Still open after Don's answers.** The 6300 test itself — Don has never run this, so his radio remains the only 1-SCU datapoint and he shifts from informant to experiment subject. The guided record-during-mute session (plan §6, likely-confirm after the FDX-off simulation) is unchanged and still the decisive run. Ask when scheduling it: whether his 6300's single "XVTR" port behaves like the 8600's XVT A for coupling purposes.

## 4d. ITEM 1b RUN AND ANSWERED — the working recipe is RECEIVER GAIN, not drive (Noel at the 8600, 2026-08-09)

**A clean, detune-confirmed, splatter-free in-radio loopback exists.** Noel: *"at rf gain of 8 I can start to hear myself, at 32 it's full signal... setting rx to transverter B doesn't have any splatter... I hear myself detuned which I do."* This is materially better than the 2026-08-07 "simulacrum of a signal, basically splatter, can't be detuned" verdict, and it was reached by turning a knob nobody had been able to turn.

**The verified 8600 recipe (2026-08-09), superseding §4's:**

1. `full_duplex_enabled=1` — still the gate. **Set it AFTER connecting**: the global profile load on connect (`getProfileInfo:global profile loaded JJRadioDefault`) resets it to 0 every time.
2. Transverter band defined (`xvtr create`; name TEST, rf_freq 144.1, if_freq 28.1). Its role is to make the band tunable — **not** to control drive.
3. TX slice: tuned in the band, USB, **txant = XVT A**, `rfpower = 1`.
4. Ears slice: same frequency, USB, **rxant = XVT B** — a DIFFERENT transverter port. Same-port (A→A) is what overloads.
5. **Every other slice muted** (`slice set N audio_mute=1`). Non-negotiable — see the contamination finding below.
6. **Ears slice `rfgain` is the control.** 8 = first audible, 32 = full signal. Set 32 for a "check my audio" session, restore after.
7. TX monitor off.

**Three things this overturns.**

- **`Xvtr.MaxPower` is inert for drive.** Swept the full −10 → +15 dBm range twice, once under clean mute conditions; no audible change. Confirmed from the other side by the SmartSDR decompile: `XvtrViewModel.MaxPower` is a **bare pass-through** to `Xvtr.MaxPower` with no scaling, no companion command, and no interaction with RF power. SmartSDR has no secret handshake — the field simply does not govern drive. **Design consequence: the ratified dBm/mW slider (§4a, 2026-08-08) is NOT the loopback's drive control.** It may still be worth building for real transverter owners, but the audio-check feature must not depend on it. Don's remembered "100 milliwatts" was almost certainly a *displayed* number, never an achieved level — consistent with it being numerically out of range on every model.
- **`rfpower` still governs, and it is still coarse.** 0 = silence, 1 = yesterday's overload on XVT A. The "no room between nothing and too much" problem was never solved by transverter mode; it was **side-stepped** by loosening coupling and amplifying after the front end instead.
- **The physics, stated plainly:** port-to-port coupling is fixed in hardware. XVT A couples tightly enough to drive the front end into overload, and no downstream gain rescues an overloaded front end — which is exactly why 2026-08-07 could not detune it. XVT B couples loosely enough that the receiver stays linear, and once linear, receiver gain simply sets the listening level. **The recipe is not "turn the transmitter down." It is "pick a port loose enough to stay linear, then turn the receiver up."**

**BLOCKER FOR IMPLEMENTATION — a vendor FlexLib bug means we cannot set RF gain at all.**

`Slice.cs:213` builds the command without a space after `set`:

```csharp
_radio.SendCommand("slice set" + _index + " rfgain=" + _rfGain);
```

emitting `slice set1 rfgain=24`, which the radio silently discards. **Every other setter in the file has the space.** Present in our vendored copy AND in the pristine `flexlib-api-4.2.20` drop, so it is FlexRadio's bug, not ours. Report upstream (Noel has the alpha-tester channel).

Sent correctly the radio parses and validates it, and answers: valid values are **−8, 0, 8, 16, 24, 32** (six discrete 8 dB steps, 40 dB span); anything else returns `50000031 RF Gain out of range`. **This also retroactively falsifies the 2026-08-07 conclusion that "a 32 dB rfgain cut produced no response, so the overload margin exceeds it"** — that command never reached the radio. It was a malformed string, not a measurement.

Implementation options: patch the vendored FlexLib (one character), or send the raw command from our layer. Patching is cleaner and should be noted in `MIGRATION.md` so the next FlexLib upgrade re-applies it.

**METHODOLOGICAL FINDING, and the reason this session nearly recorded a false negative: the operator hears a MIX of every unmuted slice.** For most of the run only ONE slice was on a transverter port while three others sat on ANT 1 at 14.100 contributing noise, so loop changes were buried. The "max_power does nothing" and "detuning does nothing" results were both measured through that contamination, and the detune result **reversed** once Noel muted everything but the ears slice. Carry this into the automated session: **mute every slice except the ears slice, and restore the mute state on teardown.** Also carry it into how we score any future by-ear experiment — say what else was audible.

**Open after this run:**

- **Don's 6300 (1 SCU) has only ONE transverter port ("XVTR"), not an A/B pair.** The whole recipe rests on transmitting into one port and listening on another, so the 8600 path may not exist on his radio at all. His test is now specifically: is there a second port, and if not, does max RF gain on the single port stay linear? Noel's read: *"On a radio like Don's, you could maybe try a tx antenna to hear yourself, but setting rf gain to max seems to do it."*
- Where between XVT A and XVT B the linear range actually sits, and whether rfgain on XVT A at −8 clears the overload (untested — the first honest test of that knob).
- `band_persistence_enabled` flipped itself back to 1 during the session after being set to 0. Something re-asserts it; unidentified.
- `transmit freq` reported 28.100 (correct IF) early in the session and 144.100 later, with the band still valid. Unexplained; may be a reporting quirk.

### The feature shape, ratified by Noel at the radio (2026-08-09)

> *"The way you'd want to design a feature is to allow the user to select an unconnected antenna port from a combo box. It would set up the radio, making sure mute and rf gain is set up. On a single SCU unit it would use record; on 2 SCU units you get to hear it from the beginning."*

**The operator picks the listening port. The app cannot and must not guess.** Nothing in the protocol reports what is physically plugged into a jack, so which port is free is knowledge only the operator has. A combo box built from the radio-reported RX antenna list, minus whatever port we are transmitting into, with a label that says *why* it is being asked — pick a port with nothing connected to it. Persist the choice **per radio serial** (same pattern as the rest of the per-radio config), because it is a property of the station's wiring, not of the session.

**Everything else the app does itself** (friction-tax principle — the operator answers the one question only they can answer, and we handle the rest): snapshot current state, set the TX slice onto the transverter port, create/aim the ears slice at the chosen port, **mute every other slice**, set RF gain (32 is the working value on the 8600; make it adjustable and speak both the value and the step), full duplex where applicable, monitor off, RX filter matched to the TX filter (Don's condition, §4c). Teardown restores all of it, including the mute state and the gain.

**Two tiers, chosen by SCU count, not by guesswork:**

- **2 SCU (6600, 6700, 8600, AU-520): live.** Full duplex on, you hear yourself from the first syllable. Verified on the 8600, 2026-08-09.
- **1 SCU (6300, 6400, 8400, AU-510): record then play — but it is a PROCESSING check, not an RF check.** ~~No full-duplex gate needed — the record tap sits upstream of the transmit mute (proven 2026-08-07).~~ **Corrected 2026-08-09: the record buffer captures the MIC / TX-audio tap, not RF** (see the 144.500 control below). Key, talk, unkey, hear your processed transmit audio back. Auto-play on unkey is the right default; Noel judged it "arguably the nicer default everywhere, since it never demands talking and listening at once." **The announcement and help text must say what this tier can and cannot prove** — it answers "how does my processing sound", not "how do I sound on the air". Offering it as an equivalent to the 2-SCU loopback would be a false claim.

**Two recording-tier rules, from watching the raw version fail (Noel, 2026-08-09):**

- **Gate the recording to the keyed interval — arm at key-down, stop at key-up.** A free-running recorder fills its buffer to the 120-second cap, and playback then starts two minutes upstream of anything the operator said. Today's manual run hit exactly that: `record_time=120.0`, with the take buried somewhere inside a rolling window. Transmit-gated recording makes the take *be* the recording, makes auto-play-on-unkey instant and exact, and sidesteps the cap entirely. It also removes the re-arm hazard that nearly wiped an operator's takes on 2026-08-07, because arming is tied to a PTT edge rather than to a button someone might press twice.
- **Playback is heard in isolation.** Silence everything except the playback source before play starts, restore afterwards. Same discipline as the capture side and for the same reason: the operator hears a mix of every unmuted slice, and a judgment about audio quality made over other slices' noise is not a judgment about audio quality. The mute state is part of what teardown restores.

Gate on the radio's reported capability, never on a model table. The Feature Availability tab explains the tier a given radio gets and why.

**Announcement obligations:** say which port is being used for listening, say that other slices are being muted (and that they will be restored), and say the gain being applied. Every one of those is a change the operator would otherwise discover by its side effects — the exact failure mode that made today's session take four hours.

### THE SAGA RESOLVED — the record buffer is a MIC TAP, the live loopback is real RF (Noel's control, 2026-08-09)

**The control that settled it, designed by Noel:** tune the ears slice to **144.500** — 400 kHz off, where nothing can possibly be received — set **half duplex**, transmit on slice 0, record slice 1, play it back.

**Result: voice, with silence around it.** No RF at 144.500 could be demodulated by any mechanism, so the voice in that buffer cannot be RF. It is the microphone / TX-audio path being injected into the record stream.

**This falsifies the 2026-08-07 conclusion outright.** That session recorded "VOICE IN THE BUFFER" with full duplex off, concluded the record tap sits upstream of the transmit mute, and built the entire 1-SCU record tier on it. The voice was the mic tap. There was never a 1-SCU RF path.

**And yet both days were right about different paths — that is why the saga went in circles for two days:**

- **Live listening, full duplex ON, is genuine RF.** Confirmed today the honest way: on the clean XVT A → XVT B path at rfgain 32 with every other slice muted, detuning 1 kHz low chipmunks the voice. Frequency-selective demodulation, which no injected tap can fake.
- **Record with full duplex OFF is the mic tap.** Confirmed today by the 144.500 control.

The 2026-08-07 session ran both paths and attributed the results of each to the other. The antenna-isolation test that "proved it was real RF all along" was testing the *live* path; the detune tests that "proved it was a tap" were testing the *record* path. Both experiments were sound. The error was treating them as evidence about one mechanism.

**THE FIDELITY LADDER, now empirically grounded rather than assumed:**

1. **Processed-TX-tap recording — universal, every radio, no full duplex needed.** What the record buffer actually captures. It carries the full processing chain (2026-08-07's two-take A/B with the processor cranked came back audibly saturated), so it genuinely answers *"how does my compander/processor sound?"* — which is Don's actual stated need. **It does NOT prove modulation, exciter, RF routing, or signal-in-noise behaviour.** Label it honestly as a processing check, never as an off-air listen.
2. **In-radio RF loopback — 2-SCU / full-duplex-capable radios only.** TX into one transverter port, listen on a different unconnected port, receiver gain to taste. Detune-confirmed real demodulation, no splatter, milliwatt class. Proves DSP → modulator → exciter → port routing → a real receiver.
3. **External SDR — absolute ground truth, off-air.** Unchanged.

**Consequence for Don and every 1-SCU owner:** tier 1 and tier 3, not tier 2. His 6300 cannot do the in-radio RF loopback — it has one transverter port, no dedicated receive input, and no full duplex. But tier 1 covers the thing he actually asked for, and tier 3 (a KiwiSDR that JJFlex drives, so the bandwidth controls are ours and accessible) covers off-air truth. Neither is a consolation prize; they answer different questions.

### DAXIQ probe built and INCONCLUSIVE — do not read it as a negative (2026-08-09)

`tools/rigbench/daxiq_probe.py` exists and the plumbing is proven end to end: `client udpport` over TCP, `client udp_register handle=0x..` as a UDP keepalive to port 4991, `stream create type=dax_iq daxiq_channel=1`, `display pan set 0x<pan> daxiq_channel=1`, `stream set 0x<id> daxiq_rate=48000`. Packets arrive at ~273/sec, correctly framed — verified against a real dump: type 1 with our stream ID, OUI `1C2D`, packet class `0x02E4` (48 kHz wide IQ), payload little-endian float32 at offset 28, exactly as the parser computes.

Energy sat at a mean of ~48.4 for thirty seconds in BOTH a half-duplex run and a full-duplex run, the latter while Noel could hear himself with full processing. **The instrument showed no response to a signal known to be present, so neither run says anything about whether the transmit mute reaches the IQ stream.** Recording these as "IQ is muted during TX" would be a false negative.

**CORRECTION, same day (follow-up instrument session — see `daxiq-instrument-task.md` Findings).** This section originally claimed the payload was "a synthetic test pattern (`-64, +64, -16, +16` repeating)". **That was wrong, and the binding was never broken.** DAX IQ is coarsely quantized — every sample is a multiple of 16.0 against a full scale of 32768 (`VitaIFDataPacket.cs`, `ONE_OVER_ZERO_DBFS`) — so at a quiet noise floor the sample alphabet is only ~25 values and a hex dump reads as if it repeats. It was **real receiver noise** throughout. Three proofs, all gathered with no operator and no transmission: autocorrelation over lags 2..512 peaks at r = 0.16–0.18 (a genuine repeating pattern scores above 0.95); the mean tracks the preamp (23.6 at rfgain 0 rising to 47.8 at rfgain 32), which no generator could know; and the broadband floor is frequency-dependent (~6 dB higher at 14.075 than at 14.990), which is atmospheric band noise behaving normally.

**The disconfirming evidence was already in the original logs and was misread.** `iq-run.txt` and `iq-fdx-on.txt` show per-second means varying (48.20–48.72) and peaks varying (224–288). A deterministic pattern at a fixed packet rate yields identical statistics every second; those numbers were never flat, they were varying-but-unresponsive. **Lesson for any future by-ear or by-eye scoring: "looks constant" is not a measurement. Autocorrelate, or vary a control (here, the preamp) and check the response.** Same failure mode as the detune misscoring on 2026-08-07, one level up the stack.

All three hypotheses in the original brief are resolved and all three were wrong: pan ownership is a non-issue (the radio auto-associates an unbound client's dax_iq stream with the resident GUI client), the `daxiq` key in `Waterfall.cs:1123` is an ignored status key rather than a settable flag, and no client-identity registration is required on the LAN path.

**So the real open question is unchanged and untested: does IQ energy respond to keying with full duplex OFF?** The probe is now rewritten, validated against the live 8600, and self-owning — it needs nobody. Stage 1 is GO, with a mandatory pre-flight signal check. **Note for whoever runs it: the bench 8600 has no antenna connected**, so the only signal available is the transverter loopback itself.

**A Windows Firewall rule was required** before any UDP reached the probe — inbound UDP to `python.exe` was silently dropped, including the radio's own discovery broadcasts. Diagnosed by testing against those broadcasts rather than by guessing at the VITA parse. Noel allowed it via the Windows Security prompt. Any future bench tooling that listens on UDP will hit this.

**Still worth chasing (Noel, same session): raw IQ during transmit.** We have only ever asked what the *audio* path does. If DAXIQ keeps streaming real IQ from the ears slice while keyed, JJFlex could demodulate PC-side and get tier-2 fidelity with no full-duplex flag — the universal rung the ladder is missing. Unknown whether the IQ stream survives the transmit mute; the audio mute clearly does not stop the record tap, so the two are plumbed separately. This is the DAXIQ probe already sketched in §6, now with a specific motivating question.

**Bench tooling built for this run lives in `tools/rigbench/`** — `flexwire.py` (raw wire client), `snapshot.py`, `slices.py`, `txset.py`, `rset.py`, `raw.py`, plus two operator steppers, `power.py` (transverter dBm) and `gain.py` (receiver RF gain). All of them refuse to transmit by construction: keying stayed with the operator's hand mic throughout.

## 4e. THE IQ TIER IS REAL — and it supersedes the loopback (2026-08-09 evening)

**Proven live on the 8600, with full duplex OFF and the audio path muted.** The
DAX IQ stream carries the transmitted signal straight through the transmit
mute. Captured, demodulated PC-side, and confirmed by software detuning.

**The evidence, in order of strength:**

- **Software detune works perfectly.** One recorded capture decoded at 144.099,
  144.100 and 144.101 gives a voice that shifts pitch a full kHz up and a full
  kHz down. *Nothing about the radio, the coupling, or the voice differs between
  those files — only the arithmetic.* No tap, injection, or non-RF path can
  produce a pitch shift under those conditions, because there is no carrier for
  the shift to be relative to. This is a far stronger test than the live detune,
  and it is repeatable forever without the radio.
- **Energy tracks keying** with full duplex OFF: floor at −52.87 dBFS, jumping
  to −40.52 mean / −31.94 peak while keyed, with a **stable spectral peak
  between +328 and +1254 Hz** — SSB voice exactly where it belongs relative to
  the carrier — returning to the floor the instant the operator unkeys.
- **A full-duplex control run** was taken first and behaves identically, so the
  instrument was validated against a signal known to be present before the
  decisive run. (Mandatory: two earlier runs were nearly recorded as a false
  negative for want of exactly this.)

**Consequence: the fidelity ladder collapses.** The morning's conclusion — that
1-SCU radios get only a processing check — is superseded. **Don's 6300 and every
other single-SCU radio can have genuine RF ground-truth audio checking**, with
no full-duplex flag and no second receiver, by demodulating IQ PC-side.

**The IQ path is not merely equal to the in-radio loopback, it is cleaner.**
Listening through a slice means hearing the transmitted audio *plus the
receiver's AGC* (slice 1 was running `agc_mode=med, agc_threshold=70`) — a
compressor applied after the fact to the very thing being judged; the "pumping"
Noel heard live is that AGC breathing, and none of it is in the signal. DAX IQ
is tapped at the DDC, upstream of the slice demodulator and its AGC. **Full
duplex retains exactly one advantage: immediacy — you hear yourself live rather
than after the fact** (Noel's assessment). That is a real UX benefit and a
narrow one.

**Three design problems dissolve into decode-time parameters.** Once
demodulation is ours, every rendering choice is a setting on the same recording,
re-listenable indefinitely:

- **Filter width** — Don's stated hard condition for accepting a recorded tier
  ("set the receiver's bandwidth to match that of the transmitter") is satisfied
  *by construction*, because we choose the filter. The bench demod already
  band-limits to 150–2900 Hz against a TX filter of `lo=100 hi=2900`. No slice
  plumbing, no save/restore, no spoken fallback prompt.
- **AGC simulation** (Noel's design call) — an app checkbox that applies a
  software AGC mirroring the radio's own `agc_mode` / `agc_threshold` /
  `agc_off_level`, so the two listens are directly comparable. **Default OFF**:
  the entire argument for this tier is an uncoloured listen, so the honest
  rendering is what you get without asking. On answers "what does the receiving
  station hear", off answers "what did I actually transmit".
- **Tuning offset** — free, as demonstrated. Useful as a diagnostic and as proof
  to the operator that they are hearing real RF.

### Architecture decision (Noel, 2026-08-09): the demodulator lives IN THE APP

**Not numpy, not a script.** The Python demod (`tools/rigbench/demod.py`) was
scaffolding to prove the mechanism and should be treated as a reference
implementation only. The real thing is C# inside JJFlex, **writing an audio
file** — because a recording of what you transmitted is a feature we want
regardless, and the audio check is only its first consumer.

Why this is tractable rather than a DSP project:

- **We already own the VITA layer** (`FlexLib_API/Vita/`), including
  `VitaIFDataPacket` with the wide-IQ classes already parsed.
- **The demodulation is almost trivial** because the pan is centred on the
  transmit frequency, so the suppressed carrier sits at DC and the complex
  baseband IS the analytic signal of the audio. Keep the positive-frequency
  voice band, take the real part. **No mixing, no Hilbert transform, no carrier
  recovery.**
- **FFT infrastructure already exists** in the PC-side DSP work
  (`SpectralSubtractionProvider` and friends).

Plumbing learned on the bench that the C# implementation inherits: the probe
must hold its own GUI seat and **create its own panadapter** — a pan owned by a
different GUI client cannot be bound (`endpoint_type=Not Assigned`), and a pan
we retune but do not own has its centre re-asserted by its owner. Bind
`daxiq_channel`, set `daxiq_rate`, and the stream reports
`endpoint_type=Display` when it is live.

### 4f. SOFTWARE FULL DUPLEX — build our own, on any radio (Noel, 2026-08-09 evening)

> *"By default radios don't give us full duplex, and when they do you get the
> processed audio which you may or may not want. So why don't we create our own
> full duplex with a small delay."*

**Take the IQ stream live instead of to a file, demodulate continuously, and
play it to the operator as they transmit.** This works because of the finding
above: the IQ keeps flowing through the transmit mute on a half-duplex radio,
so the receiver is live during transmit on *every* Flex — the only missing
piece was somebody demodulating it. **This deletes full duplex's last remaining
advantage.** Live self-monitoring becomes available on hardware that has no
full-duplex capability at all. We are not working around the radio's
limitation; we are routing around the stage that imposes it.

**The shape:**

1. Operator says they are ready to check their audio.
2. **Countdown with beeps from five.** During it, the app opens an IQ stream
   centred on the transmit frequency (it knows the frequency — no operator
   input needed) and mutes every other slice.
3. Continuous demodulation, played back live as it happens.

**What the countdown is actually for (Noel's correction): it is a READINESS
GATE, not a speech-timing device.** *"The countdown's really there to make sure
we have the IQ connection bound and make sure the op has their mic in hand,
then they can twiddle knobs to their hearts content."* It covers stream binding
latency and gets the microphone into the operator's hand; it also happens to
tell a blind operator when to start with nothing to watch. **Then the session
stays live indefinitely.** This is not a "say your test phrase" moment — it is
an adjustment session with your own transmitted signal in your ears while you
change mic gain, compander, EQ, filter edges.

**A detune button, as a trust affordance (Noel).** *"If they don't believe us
it's the real signal we can add the detune button to show them."* One press
shifts the software tuning and the operator's own voice changes pitch — proof
from physics that no injected tap can fake, on demand, on their own voice. Most
software asks to be believed; this lets the operator demand proof and get it
instantly. It costs nothing: we already have software tuning, demonstrated
2026-08-09 by decoding one capture at 144.099 / 144.100 / 144.101.

**Recording becomes a byproduct, trimmed to the audio (Noel).** *"If they just
want to record a chunk, that could be demodulated and trimmed to the audio
length."* Detect the voiced span in the demodulated stream and trim to it —
which permanently solves the fixed-buffer problem (the radio's recorder fills
to its 120-second cap and buries the take, hit live on 2026-08-09).

**The radio's own record/play retires from this feature.** Not because ours is
nicer but because **the radio's record buffer is a MIC TAP** — proven by the
144.500 control — so it structurally cannot show an operator their transmitted
signal, however the rest of the feature is built. Keep it only as a convenience
for someone who explicitly wants a quick radio-side record.

**Risk: delayed auditory feedback — real, but smaller than it first looks.**
Hearing your own voice delayed disrupts speech, peaking around 150–200 ms;
under ~25 ms it is unnoticeable. Our budget is one IQ block (1024 samples at
48 kHz ≈ 21 ms) plus network jitter plus output buffering.

**Noel's mitigating observation, which materially lowers this risk: the radio's
own full-duplex loopback already has a delay, and it was already judged fine**
(*"you can totally tell exactly how you sound — amazing"*, §4). That path runs
TX → modulator → port → receiver → demodulator → audio. **So the relevant
number is not our total latency but the INCREMENT over a path already accepted**
— network transport plus our buffering on top of a chain that mostly existed
already.

**Measure it, do not estimate it.** VITA packets carry `tsi`/`tsf` timestamps
which the probe already parses. Comparing a packet's radio-side timestamp
against the moment its audio reaches the sound card yields true end-to-end
latency in milliseconds. **Build this in as a diagnostic from the start** — it
turns "does the delay bother you" into a number to tune against.

Secondary mitigation, already inherent in the design: reading or counting under
DAF is far less disruptive than composing speech, because the disruption acts on
speech planning. "Beeps, then say your test phrase" is close to best case;
"hold a QSO while monitoring" would be worst case. If measurement lands us above
~80 ms, the honest response is a spoken warning plus record-then-play as the
alternative, not silently shipping a disruptive live mode.

### 4h. Libraries and settings for the decode work (2026-08-09)

**No new dependencies are needed.** Everything the decode/record architecture
requires is already referenced:

- **IQ ingestion** — `FlexLib_API/Vita`, already parsing the wide-IQ packet
  classes (`0x02E3`–`0x02E6`) confirmed on the bench.
- **DSP** — **FftSharp**, already a PackageReference. The demodulation itself is
  ~15 lines of signal processing: the pan sits on the transmit frequency so the
  suppressed carrier is at DC, so it is band-limit plus take the real part. No
  mixing, no Hilbert transform, no carrier recovery.
- **Playback and panning** — existing `RxAudioPipeline` + JJPortaudio, which is
  also the mixer the spatial replay-vs-live separation needs.
- **Opus** — `P-Opus-master` (Opus 1.5.2 wrapper), already in the solution.
- **WAV / MP3 / M4A export (Noel: "hams like to pass that around")** —
  **NAudio 2.2.1**, already referenced, exposes `MediaFoundationEncoder`
  (`EncodeToMp3`, `EncodeToAac` — AAC is what M4A wraps) using **Windows' own
  built-in codecs**. No LAME binary, and MP3 patents expired in 2017, so
  shipping MP3 export is clean. **Skip Vorbis** — hams do not circulate `.ogg`;
  MP3 is the lowest common denominator, M4A covers Apple.
  - **Caveat to design for:** Windows N/KN editions lack the Media Feature Pack
    and may have no Media Foundation encoders. **We cannot download or bundle
    it** — it is a Windows Feature-on-Demand served by Microsoft, not a
    redistributable. Three-layer answer (Noel asked 2026-08-09 whether we could
    offer the download):
    1. **Ship our own MP3 encoder and skip the OS entirely.** LAME is LGPL,
       MP3 patents expired 2017, dynamic linking satisfies the licence. MP3 is
       the format hams actually circulate, so this makes the common case work
       on every Windows edition.
    2. **AAC/M4A stays Media-Foundation-only** (AAC licensing is messier than
       MP3's). When absent, **deep-link to `ms-settings:optionalfeatures`** —
       drops the operator on the exact screen. For a blind user, "here is the
       button" beats "go find Optional Features" by a wide margin.
    3. WAV and Opus are always available; we own both.

**DAXIQ sample rates are 24, 48, 96 and 192 kHz** — those four only, from the
packet classes at `VitaFlex.cs:30-33`; **24 kHz is the floor.** Cost is
`rate × 8` bytes/sec: 24 kHz ≈ 192 KB/s (~1.5 Mbps), 48 kHz ≈ 384 KB/s
(~3.1 Mbps).

**Default the audio check to 24 kHz.** A 3 kHz SSB signal needs nothing
approaching 24 kHz of spectrum, so the lower rate halves the bandwidth and
loses nothing that matters. This is the single biggest lever on whether the IQ
tier is viable over SmartLink for remote-only operators like Don. Higher rates
belong to anything that wants a wide view, not to this feature.

**FFT block size is a user-changeable setting (Noel), in advanced settings**,
because it is a latency-versus-CPU trade and not every machine can hold the
small end: 1024 samples at 48 kHz ≈ 21 ms, 512 ≈ 11 ms; smaller means lower
delay and more work per second.

- **Speak both numbers** — block size and the resulting latency in milliseconds.
  Same reasoning as the dBm/mW slider: one is the wire unit, the other is the
  one that means something to an operator.
- **Show MEASURED latency, not theoretical.** Since VITA packets carry
  `tsi`/`tsf` timestamps, the setting can report what the path is actually
  doing end to end rather than what the arithmetic predicts — a self-verifying
  knob, and the same measurement that tells us whether we are in the
  transparent (<25 ms) or disruptive (~150 ms) DAF zone.
- Help text should say the symptom plainly: if the audio breaks up, raise it.

### 4g. The rolling IQ buffer — "you said what?" and recording (Noel, 2026-08-09)

**The decode architecture's real payoff is not the audio check.** Noel:
*"'you said what' relies on the ability to go back 5 seconds in a live IQ
stream. We'll have to start slice number (n) streams and keep them rolling, but
that's just temporary disk that we can clear, since recording data will be
piped to Opus."*

The shape: each slice of interest gets its own DAX IQ stream, continuously
written to a temporary ring on disk. **"You said what?" replays the last N
seconds from that ring.** Retained recordings get encoded to Opus; the raw ring
is scratch and is cleared as it ages.

**Why buffer IQ rather than demodulated audio**, which would be four times
cheaper: because a re-decode is *better than a replay*. From IQ you can go back
five seconds **and change the filter, shift the passband, or re-tune** —
"I missed that callsign, decode it narrower" is a genuinely new capability, not
a repeat of what already failed to be intelligible. Buffering audio can only
replay the same rendering that was already missed.

**This is an accessibility feature first.** Replay-with-re-decode serves
operators with hearing loss (see `memory/patrick_bh_network_tester.md`), noisy
shacks, split attention, and anyone who missed a callsign in a pileup. It is
arguably higher-value than the audio check that motivated the decode work.

**Disk math, so the design is sized honestly.** 48 kHz complex float32 is
384 KB/s ≈ 23 MB per minute per stream; four slices rolling is ~92 MB/min. A
60-second ring across four slices is ~92 MB of scratch — unremarkable. Opus at
~24 kbps is ~3 KB/s, negligible, so anything *retained* costs nothing. Sizing
the ring is therefore a pure UX decision (how far back can you go), not a
storage constraint. The 8600 reports `daxiq_capacity=16`, so stream count is
not the limit either.

**Graceful degradation on constrained links (Noel, 2026-08-09): stream the
ACTIVE SLICE ONLY.** Rolling IQ for every slice is a LAN luxury; over SmartLink,
or on any link that cannot carry it, instantiate one stream and follow the
active slice. This is a real capability reduction and a sensible one — the
active slice is what the operator is listening to anyway.

- **Default by transport:** all slices on LAN, active-slice-only over
  SmartLink, with an override either way (flexibility principle).
- **Say which mode is in effect, and document it** in help and the changelog.
  A "you said what" that works on slice A and silently does nothing on slice B
  reads as a bug, not a policy — the same failure shape as the band-persistence
  reverts that cost an hour on 2026-08-09. Silent capability differences are
  the thing this project keeps having to fix.
- At 24 kHz a single stream is ~1.5 Mbps, which is what makes one-stream mode
  plausible remotely where four would not be.

**Link capacity check — measure the real stream, not a speed test (Noel,
2026-08-09).** Driven by Tony's rural connection, where Don's radio lives: the
downlink is poor and the uplink is unknown.

- **Do not use Ookla.** Their official Speedtest CLI's EULA restricts
  redistribution, so shipping it needs a commercial agreement, and the
  unofficial scrapers break and violate the ToS. **More importantly it measures
  the wrong thing** — PC-to-Ookla burst throughput, when the question is
  whether *this* uplink sustains a *UDP VITA stream* to *this* operator. A good
  Speedtest number and a stuttering stream coexist easily on rural links with
  asymmetric upstream and shallow buffers.
- **Measure the feature itself.** The packet rate is fixed and therefore known
  arithmetic — the probe already observes ~273 pkt/s at 48 kHz. Open a 24 kHz
  stream for ~10 s, compare received against expected, and report gaps and
  arrival jitter. That answers the only question that matters: can this link
  carry this feature to this operator right now.
- **Self-selecting:** start at 24 kHz; if it is clean with margin, offer 48. If
  24 stutters, say so plainly and fall back to the audio tiers rather than
  shipping something that breaks up.
- Needs no keying and no transmitting, so it can run quietly at connect — and
  it is exactly the pre-flight to run before spending a remote tester's time.
- **Do NOT trigger the radio's own network self-test** for this; per the queue
  it endangers punched sessions. Ours is a passive observation of a stream we
  were opening anyway.
  - **That caveat is transport-specific and temporary (Noel):** it applies to
    today's hole-punched UDP sessions. **JJ Flexible Connect's transport is
    QUIC + ICE**, where ICE does candidate gathering and connectivity checks
    properly, so there is no fragile punch to endanger. Do not inherit this
    constraint into Connect-era code.
  - **Reliability policy for that era — use BOTH, split by purpose (Noel's
    correction, 2026-08-09).** An initial "always use unreliable datagrams"
    recommendation was too dogmatic: head-of-line blocking only costs you when
    there is loss, so on a LAN or a symmetric gigabit link reliable delivery is
    essentially free and gives perfect IQ.
    - **The two consumers want opposite things.** Live monitoring (§4f) is
      latency-critical and loss-tolerant — a dropped packet is a click, a
      retransmit is a latency spike that can push you into DAF range. The
      recording and "you said what" buffer (§4g) are completeness-critical and
      latency-tolerant — you want every sample, and 200 ms spent filling a gap
      is invisible in something replayed later, let alone exported to MP3.
    - **QUIC multiplexes reliable streams and unreliable datagrams (RFC 9221)
      over the SAME connection** — one handshake, one NAT traversal. Map them
      onto the split: reliable for the buffer, unreliable for the live monitor.
    - **For the live path, start reliable and degrade only on measurement.**
      The end-to-end latency measurement built for the block-size knob (§4h)
      already watches true delay via VITA timestamps; drop to datagrams when
      retransmits push it past threshold. The same build then behaves correctly
      on a gigabit link and on Tony's rural one with nothing to configure.
    - QUIC's congestion control remains a genuine upgrade over raw UDP either
      way: on a marginal link it backs off and degrades rather than gapping
      blindly.

**Consequence flagged by Noel: slice handling has to change** when recording or
"you said what" is active — streams must be started per slice and kept rolling,
rather than opened on demand. That is a real architectural change to how slices
are managed, and it belongs in the decode-architecture design rather than being
bolted on later.

**Open question this raises: can we listen on the SAME port we transmit into?**
A→A overloaded the *audio* path at rfgain 32, but it was never tested against
the IQ tap with gain pulled down. If same-port works, the second-port
requirement disappears and radios with few ports stop being a special case.
One keying cycle to find out.

**Still open, one keying cycle:** the compander A/B. Capture with
`compander=0` and compare against the existing take (`compander=1,
compander_level=85`, i.e. high — the scale clamps at 100, `Radio.cs:10007`).
Different → the transmit processing chain rides the IQ and the tier is proven
end to end. Identical → the IQ tap sits upstream of TX processing, a real limit
worth knowing before anyone builds on it. Deferred to after dinner, 2026-08-09.

## 5. Help and docs deliverables

- Rewrite `docs/help/md/audio-workshop.md` to describe the dialog that exists (TX sculpting, live meters, earcons, presets) plus the new Audio Check session; delete the never-built routing/multi-output text. CHM rebuild so updated help ships.
- Command Finder keywords for the new command (keyboard-audit item 3; no bindings change, so the rest of the audit is N/A).
- Changelog entry in the house voice once it ships.
- Deconfliction: `docs/help/md/audio-troubleshooting.md` (silent-radio advisory ladder) belongs to **Track B** — this track must not also write it.

## 6. Verification sessions (split by hardware reality)

**Live results, round two (Noel at the radio with the hand mic, 2026-08-07):** the 8600's hand mic works as TX source WITH PC audio on — monitor audio reached the PC headphones while the front-panel mic fed the transmitter. Code question flagged: `FlexBase.cs:9147` sets `MicInput = "PC"` when PC audio enables, yet the hand mic was clearly the source — either hand-mic PTT overrides the input selection radio-side or the source logic is subtler; investigate in code. **Monitor latency is audible even on LAN** via the PC-audio path ("after a bit of delay I hear myself") while the radio's own headphone jack is instant — the DAF concern is real locally, not just remote; record-then-play-back stays the recommended check for remote. Zero-signal watcher confirmed working: with an empty capture device as source, "check mic" spoke during TX (the advisory pattern the plan wants, already alive).

**Live results, round one (Noel at the 8600 via ms-02, 2026-08-07, RF power 0, no antennas):** TX Monitor audio RIDES THE PC-AUDIO STREAM — Noel heard himself through the PC speakers on a local connection, so the hear-yourself loop needs no radio hardware outputs and is the same path remote users get (plus latency). Shift+Space TX lock WORKS from inside the workshop window — global commands reach it, so the one-surface loop half-exists today. Curiosity flagged: ms-02 reportedly has no mic connected, yet something fed TX audio — whatever capture device PC audio silently defaulted to; live instance of the wrong-mic hazard the Settings→Audio track (Track B) is designing against. Session continuing from a machine with a real mic.

On the 8600 (the test mule — local, low power or loopback; never full power, no dummy load):

1. **Record semantics:** with monitor on, key + `RecordOn` + talk + unkey — does playback contain my TX audio? Does `PlayOn` while keyed transmit the recording (parrot)? What does `PlayEnabled` gate?
1a. **RX-bandwidth matching (Don's condition, section 4c):** set the ears-slice filter low/high to the TX filter's edges before recording, and score whether the playback is audibly cleaner than with a mismatched (wide, then narrow) receive filter. Establishes whether auto-matching is worth the plumbing or a spoken prompt suffices.
1b. **Levelled loopback (from Don's demo recipe, section 4c):** rerun the 8600 loopback driving a defined XVTR band at dBm precision (~100 mW and below) instead of 1 integer watt, and separately with the ears-slice on a different RX port. Does the overload coloring clear? This is the experiment that decides whether the loopback tier is a faithful listen or stays a "simulacrum."
2. **Antenna lists:** does the 8600 report XVTR in `TXAntList`/`RXAntList`, and does the current WPF antenna surface show it? **ANSWERED (Noel, live at the 8600, 2026-08-07):** RX antenna picker reads ANT 1, ANT 2, RX antenna 1, RX antenna 2, XVT A, XVT B; TX antenna picker reads ANT 1, ANT 2, XVT A, XVT B. The 8600 exposes TWO transverter ports (vs the 6300 era's single "XVTR"), both selectable on both sides in shipping JJFlex — the entire loopback signal path is reachable with zero new plumbing. The RX-only jacks (RX A/B IN, rear-panel BNCs) correctly drop out of the TX list. Remaining question is purely acoustic: does audio make it around the loop (item 3).
3. **Loopback:** second slice per Don's procedure — audible? Requires `FullDuplexEnabled`? At what port level?
4. **TX-source behavior:** with PC-sourced TX audio, does `MicGain` act on the stream? Do compander/processor/filter demonstrably apply? (They should — radio-side DSP — but the workshop's annotations depend on the answer.) **ANSWERED for the gain half (Noel, live, 2026-08-07): `MicGain` acts on the SELECTED mic input, not the actual source.** Hand mic + PC audio ON (selection forced to "PC"): monitor audible, Mic Gain arrows do nothing — the knob was adjusting the PC stream while the PTT-override hand mic fed TX untouched. Hand mic + PC audio OFF: Mic Gain works normally. Design consequence: the workshop must aim its controls at the ACTIVE source or say why not — today it silently adjusts a control outside the audio path. Prediction to verify once the mic source picker exists: picker set to MIC + PC audio on → gain works. Compander/processor/filter-apply-to-PC-stream still open (test when a PC mic is available).
5. **CW monitor:** confirm `TXCWMonitorGain` moves sidetone level as expected with the `#if CWMonitor` subsystem active.

**DAXIQ-during-TX — candidate FDX-free ground-truth tap (Noel's VITA question, 2026-08-07; investigate).** SmartSDR displays your own TX spectrum on the panadapter while keyed — something feeds that during transmit. If the DAXIQ stream behind the pan keeps flowing with REAL IQ of the transmitted signal, JJFlex can demodulate it PC-side (SSB demod is routine DSP; we already own the VITA layer) → post-DSP, post-modulation ground truth with NO full-duplex flag, no second slice, potentially on 1-SCU radios — the fidelity ladder's missing universal rung. Unknown: whether the TX-time pan is fed by a true RF feedback tap (jackpot) or synthetically (mirage). Verification: open a DAXIQ channel, key up, inspect what arrives (8600 advertises daxiq_capacity=16). Probe mechanics (no app build): raw TCP command channel + a scripted UDP socket doing the `client udp_register` handshake, dump VITA datagrams to disk during a keyed interval, analyze offline — stage 1 is just "did signal energy appear while keyed" (header parse + arithmetic, settles jackpot-vs-mirage), stage 2 only if yes: numpy SSB demod of the IQ to a listenable WAV (proof-of-concept for the C# implementation). Expected friction: DAXIQ channels associate with a panadapter — the headless client may need to reference the session's pan or create one; discover empirically. Note DAX TX audio is NOT this — that channel is radio INPUT (PC mic inbound), pre-DSP. The slice audio stream during TX carries only the monitor mix (proven tonight by ear).

**THE DECISIVE 1-SCU EXPERIMENT (Noel's insight, late 2026-08-07 — run first): record-during-mute.** The 1-SCU question is really "where does the TX mute live versus where the record tap lives." If the mute is a downstream audio gate while the DSP keeps demodulating, the record buffer may capture the loopback UPSTREAM of the mute — ground truth on a 1-SCU radio, no FDX flag, via key-and-record-then-auto-play. **The 8600 with FDX OFF is a perfect 1-SCU simulator** (identical mute-all-RX-during-TX behavior). Protocol: loopback arrangement (TX ant XVT A, slice B on XVT A, monitor off), FDX OFF, arm record on slice B, key and talk into apparent silence, unkey, play. **RESULT (2026-08-07 morning, live): VOICE IN THE BUFFER.** Noel's playback contained: noise floor → key → the mute audibly clamping → his voice inside the silence → unkey → noise floor returning. The record tap sits UPSTREAM of the audio mute; the receive chain kept demodulating the loopback throughout. With FDX off — the 1-SCU condition — record-then-play captured ground truth. Don's 6300 test is now a likely-confirm rather than a hail-mary (caveat: the 8600 simulates 1-SCU via the flag; a real 6300 could gate deeper — his radio still gets the final word). Buffer telemetry learned en route: `record_time` caps at 120.0 seconds; `play` field transitions disabled → 0 (ready) when content exists; the whole record/play cycle was driven over raw wire commands with the operator only keying and listening — the automated feature is fully rehearsed. ~~Open from this run: whether the captured audio carries the processing chain~~ **ANSWERED same morning (two-take A/B, processor cranked between takes): the loopback recording carries the FULL processing chain** — take two came back audibly saturated/broadcast-compressed vs the plain take one ("totally saturated... Mexican broadcaster" — Noel). Record-of-loopback = ground truth end to end, confirmed. Bonus findings: both takes coexist in one 120s buffer (two-take comparison is a viable feature workflow); buffer at cap retained the takes (ring-like behavior, keeps the recent material). Automation lesson from a live race: the feature must CHECK recorder state before re-arming — a mid-conversation re-arm nearly wiped an operator's takes.

**INJECTION HYPOTHESIS — RAISED, WRONGLY FALSIFIED, THEN CONFIRMED (the two-detune saga, same morning; a lesson in scoring your own experiments honestly):** every FDX-off take showed voice-without-noise during the keyed span, raising the suspicion that the buffer captures an injected TX-audio tap rather than the RF loop. Detune test 1 (slice B +1 kHz): came back "weird / broken speaker" — Claude scored it as off-frequency garble, i.e. real demodulation. **Noel distrusted the score** ("it wasn't detuned like off frequency, it just kind of sounded weird") and demanded the unambiguous version: detune LOW (−1 kHz), which physics requires to chipmunk a real demodulated voice. **Result: NORMAL voice, just compander-hammered. The tuning never mattered. CONFIRMED: with FDX off, the record buffer captures a built-in post-processing TX-AUDIO TAP, not RF.** The take-1 "weird" was compander saturation misread as garble.

What this means for the feature (still excellent, differently shaped):

- **Universal tier, every Flex, FDX-free: record-your-processed-TX-audio.** The tap carries the full DSP chain (saturation audibly present). For the everyday compander/processor-tuning check — Don's actual current need — this may be the MOST useful tier, and it needs nothing but the record/play flags.
- **What the tap cannot prove:** modulation, exciter, RF path, signal-in-noise behavior. Ladder redraws: processed-TX-tap recording (universal) → true RF loopback (FDX on — see open question) → off-air SDR (absolute).
**FINAL VERDICT (end of the saga, 2026-08-07 — the antenna-isolation test): IT WAS REAL RF ALL ALONG.** Two keyings, identical everything except TX antenna: via XVT A = self-audio present; via ANT 1 = GONE. The sound depended on the physical RF path, killing the injection/tap model for good. Full final model: **genuine port-to-port RF coupling into a MASSIVELY OVERLOADED receiver.** The overload explains every misleading intermediate result: no chipmunk on detune (an overdriven front end sprays envelope-shaped distortion across wide tuning, fading with distance — Noel's cross-band bleed observation), no response to a 32 dB rfgain cut (overload margin exceeds it), monitor gain irrelevant (the monitor was never in play, mon=0), power 0 = silence (no RF, no coupling), processing audible (it is the real transmitted signal). Consequences: (1) **the 1-SCU record-during-mute result is RESURRECTED — the buffer captured real demodulated RF, so the receive chain stays alive during TX with FDX off and Don's 6300 ground-truth record-then-play is back on the table**; (2) **new hard design requirement: the automated loopback must manage coupling level** — raw adjacent-port coupling at power 1 overdrives the receiver and distorts the listen ("broken speaker" coloring); the faithful version reduces drive into the receiver's linear range (the XVTR band's dBm-precision `MaxPower` is the built-for-this knob), plausibly auto-calibrating against S-meter/overload indications; (3) the three-model saga (RF → tap → RF-with-overload) is itself the case study for why the track must verify with instruments (DAXIQ/stream analysis), not ears through six routing layers. **Noel's tempered final framing (ratified as the honest product claim): what the in-radio path yields today is "a simulacrum of a signal — basically splatter, can't be detuned."** It proves your audio is present, processed, and roughly right; it is NOT a faithful off-air listen. Whether level management (linear-range coupling via the dBm drive control) can upgrade it to clean demodulation is an OPEN question for the track, not a promise. An SDR connected to a radio on a real antenna remains the best test, full stop. And none of the in-radio results mean anything unless the mic SOURCE is set right — the precondition for every honest measurement this feature will ever make. Superseded intermediate entries below kept for the record.

- ~~**FDX-ON detune test RUN (same morning): normal voice, no chipmunk — even FDX-on live listening is the tap.**~~ (Superseded — the "normal voice" was overload spray, not an injection.) Then Noel's power datum unified the whole two-day picture: at RF power 0 the audio vanishes entirely. **Working model (one mechanism explains every observation): everything heard and recorded in-radio is the TX MONITOR FEED — tapped post-processing (saturation audible) and post-drive-scaling (power 0 = silence), always injected into slice audio during TX; the `mon` flag only gates it into the SPEAKER mix; full duplex only governs whether outputs stay live during TX; no antenna/tuning involvement whatsoever (detune irrelevant in every configuration).** Noel's "monitor doesn't carry processing" folklore resolves too: it does — but only when the mic SOURCE selection matches the actual source, which explains years of mis-scored listening through mismatched configs. Case-closing discriminator (pending): wiggle Monitor Level (TXSBMonitorGain) while keyed — loudness follows the knob = formally identified. Also pending: TX antenna back to ANT1, same listen — voice persists = the XVT routing was never load-bearing for the tap. Also: the stuck-TX episode was `source=RCA` — the hand mic keys via the rear RCA hardware line, which software `xmit 0` correctly cannot override; the interlock `source=` field is the diagnostic. FIDELITY MAP AS IT NOW STANDS: monitor-feed recording (universal, every Flex, FDX-free, post-processing + post-drive — the everyday check, and genuinely good); true off-air listening = an actual receive path on a real antenna (or FDX+antenna arrangements yet to be re-proven); external SDR = absolute ground truth. The XVT-loopback tier as originally conceived may not exist as a distinct fidelity level — pending the two discriminators.
- Standing corollaries: software wire-keying with no announcement left the operator unaware he was transmitting — key-up announcement is safety-critical, not polish; auto-play-on-unkey performed live (unkey → playback within a second) and is the right default flow. Either way, the auto-play-on-unkey UX ("key down and have it auto played back") is the automated check's shape for gated radios — and arguably the nicer default everywhere, since it never demands talking and listening at once.

**Record/play + monitor test matrix (Noel, 2026-08-07 — run these configurations over time, not all tonight):**

- PC audio OFF, hand mic, radio earbuds — the clean baseline; mic-in stays on the jack so even software keying carries voice.
- PC audio ON, hand mic (PTT override) — confirms buffer playback rides the network audio stream (what a remote operator's ears live on).
- PC audio ON, SM7dB → EVO as the PC mic — the real PC-sourced TX config; ALSO the config that closes the open "do compander/processor audibly apply to the PC stream" item, since it finally puts a real mic behind the PC path.
- SmartLink remote from the laptop, laptop's own sound card — the true remote-operator config, deliberately unglamorous hardware; merges with the queued WAN self-testing enabler (port-forward the 8600 so Noel can SmartLink his own radio from home).

Against Don's 6300 (remote, PC-sourced TX audio — the real user configuration):

6. **Remote monitor latency:** rough measurement over SmartLink for the help text and the DAF advisory.
7. **Record/play over remote:** the full record-then-listen loop end to end, Don's actual adjustment workflow.
8. **Loopback on a 1-SCU radio:** whether Don's procedure works on his 6300 at all (his answers first).

## 7. Questions for Don — ANSWERED

Filed at `docs/planning/for-don/2026-08-07-transverter-listening-trick.md` and copied to his Dropbox folder. **All six answered; returned via his Dropbox folder 2026-08-07 evening, absorbed 2026-08-08 — see section 4c.** The answered doc now carries his verbatim replies.

What they did and didn't unblock: they supplied a level recipe (XVTR band, 100 mW), closed the Jim question, and added the RX-bandwidth-matching requirement — but they did NOT settle the 1-SCU question, because Don has never run this on his 6300. Verification item 8 stays gated on a guided session at his radio, where he is now the experiment rather than the informant.

## 8. Future, explicitly not in this track

- **PC-side QSO recording (TX and RX) to files.** Wanted by Noel; codec bundling acceptable. We already ship Opus (`P-Opus-master`) so .opus/.ogg output is near-free, WAV needs nothing, MP3 is possible (LAME patents expired). Needs its own design round: file naming, storage, retention, hotkeys, and where the tap points sit (PC-audio stream vs radio-side). Seed it in the TODO backlog. **Lineage note (Noel, 2026-08-07): the DAXIQ probe doubles as this feature's capture-pipeline prototype** — stream → disk → process is the same spine for IQ probing, QSO recording, and the eventual IQ archive ("save the spectrum, demod later"), so whatever the probe teaches feeds this design round directly. Numpy-side analysis muscle already exists in-house from Freight Fate.
- **Audio routing / multi-output device work.** Wanted "at some point, not now" (Noel, 2026-08-07).

## 9. Notes for the orchestrator (deconfliction + track shape)

- **Files this track touches:** `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml(.cs)` (main surface), `Radios/FlexBase.cs` (record/play wrappers + public CW monitor pan, ~7700-7960 region), `JJFlexWpf/KeyCommands.cs` (one unbound Command Finder registration), `docs/help/md/audio-workshop.md`, help TOC/CHM.
- **Track A overlap:** Track A owns the Lineout `!PCAudio` gate (`KeyCommands.cs:578,584`) and dead `LocalAudioMute` (`FlexBase.cs:7321`) — same files this track edits, different regions; conflicts should be trivial but merge order deserves a call.
- **Track B overlap:** Track B owns everything Settings → Audio (Radio Outputs, PC Audio checkbox, device picker, `audio-troubleshooting.md`). No shared code regions expected; shared *concepts* (PC-audio state, output levels) — both tracks should speak of them with the same vocabulary.
- **Sequencing inside this track:** the MOX session + TX-source annotations + mode-aware monitor + help rewrite have one small unknown (verification item 4) and can start immediately with the annotation copy stubbed. Record/play UI needs verification item 1 first (half a session on the 8600). Loopback waits on Don + verification items 3/8 — recommend shipping the track without it and adding loopback as a follow-up slice when the answers land.
- **Queue linkage:** the queue's wave-2 line "Audio Workshop implementation track — cut from `audio-workshop-plan.md`" is this file; the in-flight "Audio Workshop design conversation" entry can move to done once the orchestrator absorbs this plan.
