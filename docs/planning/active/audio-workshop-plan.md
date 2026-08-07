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
- **Low-power-during-checks option, default ON** (flexibility principle: togglable, conservative default): session start drops RF power to a floor value (10W or the radio minimum), restores on stop, and says so ("power reduced to 10 watts for the check"). No dummy load in Noel's shack and none assumed in anyone's — an audio check should not blast full power by default.
- While keyed: spoken elapsed reminders every 30 seconds at Terse ("transmitting, one minute"), hard stop at 3 minutes with a Critical announcement.
- **Escape is two-stage: first press unkeys** ("Transmit off") and stays in the dialog; second press closes it. Escape never leaves you transmitting — this extends the house Escape rule rather than bending it.
- Unkey unconditionally on: dialog close, radio disconnect, session teardown, timeout. The session restores whatever state it changed (monitor enable, RF power, and later the loopback's antenna/slice arrangement).
- Remote awareness: when `RemoteRig`, session start adds "over remote, monitor audio arrives delayed — record and play back is recommended," because monitor audio rides the compressed RX stream back and delayed self-hearing actively disrupts speech. This is Don's actual situation and the likely test configuration (his radio, remote).

**Record-and-play flow** (pending semantics verification, section 6):

- "Record a test transmission" → speaks the safety line → keys and starts slice record → operator talks → press again (or 15-second cap) → unkey, stop record, auto-start playback → operator hears their actual audio → adjust → repeat. One button, a loop a tester can lean on.
- FlexBase grows thin wrappers for the active slice's `RecordOn`/`PlayOn`/`PlayEnabled`, same command-queue pattern as the monitor properties (~line 7940 region).

**Mode-aware monitor section.** In CW mode the section shows "CW Monitor Gain" (`CWMonitorGain`, and promote `MonitorPan` to public for CW pan); in phone modes the existing SB fields. Section header names the mode so the screen reader user knows which knob family they're on.

## 4. The transverter loopback (design-pending, mechanism as understood)

Noel's recollection of Don's procedure: turn the transverter port on, set that port to something very low, tune a second slice to what the transverter port was set to — and you hear your transmitted signal through the radio's own internals, no transverter connected. The likely mechanism: TX routed to the XVTR jack radiates ~milliwatts; a second slice receiving on the XVTR (or leaking into a normal front end) demodulates it — your signal after real modulation, not the DSP tap.

Open questions that block building it (Don + hardware answer these):

- Does it need a 2-SCU radio (`FullDuplexEnabled` / receive-during-transmit), or does it work on Don's 1-SCU 6300 — and if so, how?
- Is the second receiver a slice with RX antenna = XVTR, or a normal antenna hearing leakage?
- Does it involve a defined XVTR band (`Xvtr.cs` objects) or just the antenna selection?
- What actually goes over the air while doing it — nothing, or leakage worth a caveat?

If verified, the app automates the whole dance: snapshot state → arrange TX antenna/level and the listening slice → run the Audio Check session on it → tear down and restore on stop. Feature-gated by radio capability and `AvailableSlices`, with the Feature Availability tab explaining absence.

## 5. Help and docs deliverables

- Rewrite `docs/help/md/audio-workshop.md` to describe the dialog that exists (TX sculpting, live meters, earcons, presets) plus the new Audio Check session; delete the never-built routing/multi-output text. CHM rebuild so updated help ships.
- Command Finder keywords for the new command (keyboard-audit item 3; no bindings change, so the rest of the audit is N/A).
- Changelog entry in the house voice once it ships.
- Deconfliction: `docs/help/md/audio-troubleshooting.md` (silent-radio advisory ladder) belongs to **Track B** — this track must not also write it.

## 6. Verification sessions (split by hardware reality)

On the 8600 (the test mule — local, low power or loopback; never full power, no dummy load):

1. **Record semantics:** with monitor on, key + `RecordOn` + talk + unkey — does playback contain my TX audio? Does `PlayOn` while keyed transmit the recording (parrot)? What does `PlayEnabled` gate?
2. **Antenna lists:** does the 8600 report XVTR in `TXAntList`/`RXAntList`, and does the current WPF antenna surface show it?
3. **Loopback:** second slice per Don's procedure — audible? Requires `FullDuplexEnabled`? At what port level?
4. **TX-source behavior:** with PC-sourced TX audio, does `MicGain` act on the stream? Do compander/processor/filter demonstrably apply? (They should — radio-side DSP — but the workshop's annotations depend on the answer.)
5. **CW monitor:** confirm `TXCWMonitorGain` moves sidetone level as expected with the `#if CWMonitor` subsystem active.

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
