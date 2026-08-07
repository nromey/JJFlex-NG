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

- Don's 1-SCU 6300: `FullDuplexEnabled` semantics there, and whether his procedure differs — his answers still gate the 6300/8400-class story (the 2-SCU recipe above is proven for 6600/6700/8600-class). **Do NOT assume 1-SCU can't loop (Noel, 2026-08-07): Don has two slices + an XVTR port and has likely done this before.** But know the anatomy when reasoning about it: SLICES ARE NOT RECEIVERS — an SCU is the physical chain (port, preselectors, one ADC digitizing a wide swath) and slices are software channels carved from that one stream, so two slices on a 6300 are one receiver heard twice, and "one slice listens while one transmits" reduces to "does the single receive chain survive TX." Candidate reasons it may not: (a) T/R switching / front-end protection (real at 100W, arguably paranoia at XVTR milliwatts), (b) the commonly cited one — during TX the radio borrows a receive chain for its own TX feedback/metering loop; 2-SCU radios have a spare, 1-SCU radios don't (unverified how hard this commitment is). The `full_duplex_enabled` flag exists radio-wide in the protocol regardless, so a 6300 has exactly three outcomes: command refused / accepted-but-inert / accepted-and-works (gate was policy). One raw wire command against Don's 6300 (with his blessing) discriminates. If 1-SCU truly cannot RX-during-TX, the record-then-play path (if the buffer captures monitor audio) is the 1-SCU fallback — **at monitor fidelity only (Noel's correction, 2026-08-07): record is a CAPTURE MECHANISM, not a fidelity tier — it inherits the fidelity of whatever the slice hears.** Record-of-loopback = ground truth, DAF-free (the crown jewel on capable radios); record-of-monitor = the monitor's coloration bottled (still useful for remote DAF-avoidance, labeled honestly). Per Noel's ears the monitor does NOT carry the full processing — so on 1-SCU radios without FDX, **the only ground-truth path is an external receiver on the TX frequency: elevate SDR-listen (WebSDR/KiwiSDR tuned to your TX freq) from someday-vision to a first-class audio-check tier** — it is what Don already does by hand today, and for 1-SCU owners it is the only truth available. Empirical bonus for the track: record the monitor, record the loopback, play both back-to-back — documents the monitor's coloration objectively in the radio's own voice.
- ~~Whether RF power 0 also drives the XVT port enough to loop~~ **ANSWERED: no — at power 0 the loop is silent; 1 watt (the integer floor above zero) is required.** Context that makes 1 fine: `Radio.RFPower` is an int (Radio.cs:8467, whole watts only — Noel's fractional-watts ask is impossible on the main control), but the XVT port is a milliwatt-class output (~+10 dBm max) the slider maps onto proportionally, so "1" is already microscopic at the jack. For precision drive later: `Xvtr.MaxPower` on defined transverter bands is a double in dBm, -10.0 to +10.0 in hundredths (Xvtr.cs:169-202). Check whether the power value field accepts typed digits; if not, work item.
- **Reframing from Noel, live: the loopback is also a transmitter self-test** ("I'm glad I now know my exciter on A/B works") — one PTT press proves DSP → modulator → exciter → port routing with no antenna. Carry this into the help page and the feature's positioning: "check my audio" and "is my radio actually transmitting" are the same button.
- No `Xvtr.cs` band definition was needed — plain antenna selection sufficed. Note for the docs: the "set the port to something very low" step in folklore appears to be the RF power floor, not an XVTR band frequency.
- What leaks over the air at 1W into XVT with antennas connected — caveat wording for the help page.

**Live finding (Noel, 2026-08-07): Ctrl+Shift+W did not open the workshop — it "changed units."** The default binding is correct (`KeyCommands.cs:1069`, Global scope → OpenAudioWorkshop), so something shadowed it: either a control-local units handler swallowing the chord while focus was in a ScreenFields value field (a raw `case Keys.W:` handler exists near `KeyCommands.cs:1958` — investigate), or a saved user keymap override. Audit item for this track: no control-local handler may shadow a Global-scope chord; every bound key speaks its true action in every state (no-silent-keystrokes rule — this one spoke, but spoke the wrong command). Command Finder → "workshop" is the reliable door meanwhile.

## 4a. Menu-parity audit + XVTR-aware power control (Noel, live session, 2026-08-07 — handoff item)

Sparked by the loopback session: the actionable radio controls in ScreenFields (transmit, receive, antenna sections) largely have no menu equivalents — ScreenFields' own menu just expands sections rather than offering actions. Noel uses field expanders constantly but wants the *addressable* path too: **Alt+R → T (Transmit) → P (Power)** should walk a menu with accelerators into a Power dialog. Two work items:

- **Menu-parity audit:** every actionable ScreenFields control gets a menu path with accelerator keys. Reality check from the code: TX Antenna / RX Antenna submenus already EXIST in NativeMenuBar (~line 685-707, checkable, spoken confirmation, built from radio-reported lists) — but Noel has never met them, so the audit is part "add missing items" (power has no menu path anywhere) and part "make existing items findable / verify every menu mode builds them" (the dispatch paths are not unified — four parallel paths per memory). Audit-and-change item, **routed to the orchestrator** (Noel: pass to Phil the first) for filing into the track set — it is app-wide UI architecture, bigger than the audio track.

**Shared-radio courtesy (Noel, same session): keying should not interrupt other clients' listening.** On a shared radio (MultiFlex — e.g., a listen-only guest connected while the owner operates), every listener's RX mutes whenever the transmitting operator keys, because full duplex defaults off. On 2-SCU radios, `FullDuplexEnabled` is exactly the mechanism that would keep listeners' slices alive through the owner's transmissions. Design input for the sharing/scheduling/Connect vision (TX-is-a-mutex memory, slice camping, MultiFlex scheduling): a "keep listeners listening" policy on shared 2-SCU radios — with the front-end/antenna caveats thought through (a listener on a nearby antenna will hear the TX; their choice). Not audio-track scope; flag for the Connect design inputs section of the queue.
- **XVTR-aware power control:** the Power dialog (menu path) and the ScreenFields power field both switch to milliwatt/dBm entry (decimal, `Xvtr.MaxPower` semantics) when the selected TX antenna is a transverter port; integer watts otherwise. The radio's own design agrees — fine drive control exists only in the XVTR band definition because dBm is the transverter world's unit and mixer overdrive is the classic transverter killer.

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
2. **Antenna lists:** does the 8600 report XVTR in `TXAntList`/`RXAntList`, and does the current WPF antenna surface show it? **ANSWERED (Noel, live at the 8600, 2026-08-07):** RX antenna picker reads ANT 1, ANT 2, RX antenna 1, RX antenna 2, XVT A, XVT B; TX antenna picker reads ANT 1, ANT 2, XVT A, XVT B. The 8600 exposes TWO transverter ports (vs the 6300 era's single "XVTR"), both selectable on both sides in shipping JJFlex — the entire loopback signal path is reachable with zero new plumbing. The RX-only jacks (RX A/B IN, rear-panel BNCs) correctly drop out of the TX list. Remaining question is purely acoustic: does audio make it around the loop (item 3).
3. **Loopback:** second slice per Don's procedure — audible? Requires `FullDuplexEnabled`? At what port level?
4. **TX-source behavior:** with PC-sourced TX audio, does `MicGain` act on the stream? Do compander/processor/filter demonstrably apply? (They should — radio-side DSP — but the workshop's annotations depend on the answer.) **ANSWERED for the gain half (Noel, live, 2026-08-07): `MicGain` acts on the SELECTED mic input, not the actual source.** Hand mic + PC audio ON (selection forced to "PC"): monitor audible, Mic Gain arrows do nothing — the knob was adjusting the PC stream while the PTT-override hand mic fed TX untouched. Hand mic + PC audio OFF: Mic Gain works normally. Design consequence: the workshop must aim its controls at the ACTIVE source or say why not — today it silently adjusts a control outside the audio path. Prediction to verify once the mic source picker exists: picker set to MIC + PC audio on → gain works. Compander/processor/filter-apply-to-PC-stream still open (test when a PC mic is available).
5. **CW monitor:** confirm `TXCWMonitorGain` moves sidetone level as expected with the `#if CWMonitor` subsystem active.

**DAXIQ-during-TX — candidate FDX-free ground-truth tap (Noel's VITA question, 2026-08-07; investigate).** SmartSDR displays your own TX spectrum on the panadapter while keyed — something feeds that during transmit. If the DAXIQ stream behind the pan keeps flowing with REAL IQ of the transmitted signal, JJFlex can demodulate it PC-side (SSB demod is routine DSP; we already own the VITA layer) → post-DSP, post-modulation ground truth with NO full-duplex flag, no second slice, potentially on 1-SCU radios — the fidelity ladder's missing universal rung. Unknown: whether the TX-time pan is fed by a true RF feedback tap (jackpot) or synthetically (mirage). Verification: open a DAXIQ channel, key up, inspect what arrives (8600 advertises daxiq_capacity=16). Probe mechanics (no app build): raw TCP command channel + a scripted UDP socket doing the `client udp_register` handshake, dump VITA datagrams to disk during a keyed interval, analyze offline — stage 1 is just "did signal energy appear while keyed" (header parse + arithmetic, settles jackpot-vs-mirage), stage 2 only if yes: numpy SSB demod of the IQ to a listenable WAV (proof-of-concept for the C# implementation). Expected friction: DAXIQ channels associate with a panadapter — the headless client may need to reference the session's pan or create one; discover empirically. Note DAX TX audio is NOT this — that channel is radio INPUT (PC mic inbound), pre-DSP. The slice audio stream during TX carries only the monitor mix (proven tonight by ear).

**THE DECISIVE 1-SCU EXPERIMENT (Noel's insight, late 2026-08-07 — run first): record-during-mute.** The 1-SCU question is really "where does the TX mute live versus where the record tap lives." If the mute is a downstream audio gate while the DSP keeps demodulating, the record buffer may capture the loopback UPSTREAM of the mute — ground truth on a 1-SCU radio, no FDX flag, via key-and-record-then-auto-play. **The 8600 with FDX OFF is a perfect 1-SCU simulator** (identical mute-all-RX-during-TX behavior). Protocol: loopback arrangement (TX ant XVT A, slice B on XVT A, monitor off), FDX OFF, arm record on slice B, key and talk into apparent silence, unkey, play. Voice in buffer = record tap upstream of mute = Don's 6300 gets ground-truth record-then-play. Silence = 1-SCU ground truth stays with the SDR tier. Either way, the auto-play-on-unkey UX ("key down and have it auto played back") is the automated check's shape for gated radios — and arguably the nicer default everywhere, since it never demands talking and listening at once.

**Record/play + monitor test matrix (Noel, 2026-08-07 — run these configurations over time, not all tonight):**

- PC audio OFF, hand mic, radio earbuds — the clean baseline; mic-in stays on the jack so even software keying carries voice.
- PC audio ON, hand mic (PTT override) — confirms buffer playback rides the network audio stream (what a remote operator's ears live on).
- PC audio ON, SM7dB → EVO as the PC mic — the real PC-sourced TX config; ALSO the config that closes the open "do compander/processor audibly apply to the PC stream" item, since it finally puts a real mic behind the PC path.
- SmartLink remote from the laptop, laptop's own sound card — the true remote-operator config, deliberately unglamorous hardware; merges with the queued WAN self-testing enabler (port-forward the 8600 so Noel can SmartLink his own radio from home).

Against Don's 6300 (remote, PC-sourced TX audio — the real user configuration):

6. **Remote monitor latency:** rough measurement over SmartLink for the help text and the DAF advisory.
7. **Record/play over remote:** the full record-then-listen loop end to end, Don's actual adjustment workflow.
8. **Loopback on a 1-SCU radio:** whether Don's procedure works on his 6300 at all (his answers first).

## 7. Questions for Don

Filed at `docs/planning/for-don/2026-08-07-transverter-listening-trick.md` and copied to his Dropbox folder. His answers gate section 4 and verification item 8. Noel carries them; also tracked in the queue's "Third parties" blocked list.

## 8. Future, explicitly not in this track

- **PC-side QSO recording (TX and RX) to files.** Wanted by Noel; codec bundling acceptable. We already ship Opus (`P-Opus-master`) so .opus/.ogg output is near-free, WAV needs nothing, MP3 is possible (LAME patents expired). Needs its own design round: file naming, storage, retention, hotkeys, and where the tap points sit (PC-audio stream vs radio-side). Seed it in the TODO backlog.
- **Audio routing / multi-output device work.** Wanted "at some point, not now" (Noel, 2026-08-07).

## 9. Notes for the orchestrator (deconfliction + track shape)

- **Files this track touches:** `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml(.cs)` (main surface), `Radios/FlexBase.cs` (record/play wrappers + public CW monitor pan, ~7700-7960 region), `JJFlexWpf/KeyCommands.cs` (one unbound Command Finder registration), `docs/help/md/audio-workshop.md`, help TOC/CHM.
- **Track A overlap:** Track A owns the Lineout `!PCAudio` gate (`KeyCommands.cs:578,584`) and dead `LocalAudioMute` (`FlexBase.cs:7321`) — same files this track edits, different regions; conflicts should be trivial but merge order deserves a call.
- **Track B overlap:** Track B owns everything Settings → Audio (Radio Outputs, PC Audio checkbox, device picker, `audio-troubleshooting.md`). No shared code regions expected; shared *concepts* (PC-audio state, output levels) — both tracks should speak of them with the same vocabulary.
- **Sequencing inside this track:** the MOX session + TX-source annotations + mode-aware monitor + help rewrite have one small unknown (verification item 4) and can start immediately with the annotation copy stubbed. Record/play UI needs verification item 1 first (half a session on the 8600). Loopback waits on Don + verification items 3/8 — recommend shipping the track without it and adding loopback as a follow-up slice when the answers land.
- **Queue linkage:** the queue's wave-2 line "Audio Workshop implementation track — cut from `audio-workshop-plan.md`" is this file; the in-flight "Audio Workshop design conversation" entry can move to done once the orchestrator absorbs this plan.
