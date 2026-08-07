# QB Track G — Audio Workshop: hear yourself (mox-parrot-sidetone)

**Recommended model: Fable.** Phase 1 integrates MOX keying with the
existing PTT safety subsystem — safety-critical integration is judgment-tier
work even with a crisp spec.

## THE SPEC IS THE LIVING PLAN FILE — read it first, from the MAIN repo

`C:\dev\JJFlex-NG\docs/planning/active/audio-workshop-plan.md`

Read the MAIN-REPO copy (absolute path above), NOT this worktree's snapshot —
Phil2 (the design session) appends refinements and hardware-verification
results there. This file only adds execution mechanics and boundaries; where
they disagree, the plan file wins. Your worktree's copy is at commit
`9b69ac5e` and may be stale.

## Launch gate — NONE. All three phases fully cleared (2026-08-07 marathon)

Every gating verification completed live: antenna lists (XVT A/B both
pickers); MicGain targets the SELECTED input; monitor rides PC audio;
record-during-mute captured REAL demodulated RF (the antenna-isolation
test closed a three-model saga — genuine port-to-port RF into a massively
overloaded receiver); the recording carries the FULL processing chain
(A/B verified); buffer telemetry known (120s cap, ring-like retention).
Read plan sections 4, 4b, and 6 END TO END before coding — the saga's
superseded intermediate models are kept in the file and you must not
build against one of them.

**Sequencing change (Noel, 2026-08-07): all CW-monitor work is OUT of
this track** — deferred behind the CW pipeline rewrite (queue wave 2).
The mode-aware monitor section ships phone-modes-only; in CW mode keep
current behavior untouched.

## Context

One of the 2026-08-07 queue-burn tracks (plan:
`docs/planning/active/nightowl-pileup-ragchew.md` — read the main-repo copy).
JJ Flex is a screen-reader-first FlexRadio client. Driver: Don (and soon
Noel) transmits PC-sourced audio and needs an adjust-and-hear loop that is
one surface. The Audio Workshop (`Ctrl+Shift+W`,
`JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml.cs`) already holds the full TX
sculpting chain; this track adds keying, hearing, and honesty about the TX
source.

## Phase 1 — build now

1. **Start Audio Check session** (plan section 3): button at the top of the
   TX Audio tab + Command Finder registration ("check my transmit audio";
   keywords audio, check, monitor, hear, myself, transmit, tx, test). NO new
   key binding.
2. **Keying rides `PttSafetyController` — this is the load-bearing
   constraint.** The session's Start drives the controller's TX-lock path so
   the warning ladder, `CanTransmitHereCheck`, and the 15-minute hard kill
   all apply unchanged (`MainWindow.xaml.cs:2663` shows the routing). The
   session adds only spoken elapsed reminders and a 3-minute soft timeout.
   Do NOT build a second timer stack.
3. **Safety line first, always:** "Transmitting on <freq>, <power> watts.
   Escape stops." Then key, then auto-focus the first relevant control — the
   existing tab order is the adjust ring; every control already speaks.
4. **Low-power-during-checks, default ON:** drop to 10W (or radio minimum)
   via the existing `_savedRFPower` save/restore pattern
   (`FlexBase.cs:7963-7984`); announce it; restore on stop.
5. **Two-stage Escape:** first press unkeys ("Transmit off") and stays in
   the dialog; second closes. Unkey unconditionally on close, disconnect,
   teardown, timeout — all through the controller. Restore every state the
   session changed (monitor enable, RF power).
6. **Listen-method cycle field** (per-radio, conservative default Monitor):
   Monitor / Record-and-play (Loopback is NOT in this track — the field's
   design should leave room for it). Session start ensures `TXMonitor` on,
   restores prior state on stop.
7. **Remote awareness:** when `RemoteRig`, session start adds "over remote,
   monitor audio arrives delayed — record and play back is recommended."
8. **Mode-aware monitor section — PHONE MODES ONLY** (CW half deferred
   behind the CW pipeline rewrite; do not touch `CWMonitorGain`,
   `MonitorPan`, or the `#if CWMonitor` subsystem): phone modes show the
   SB gain/pan fields; section header names the mode; CW mode keeps
   today's behavior unchanged.
8b. **Crash pair (plan section 4b — crash zip
   `Errors\JJFlexError-20260807-153513.zip`):** (i) null-guard the TX
   getter family in FlexBase (`MicGain` at ~7839 plus boost, bias,
   compander, processor, TX filter edges, Monitor/SB gains) with
   per-property defaults — same pattern as the 8/5 ActiveSlice sweep,
   which missed this family; (ii) `_meterTimer` must stop when the RIG
   dies, not only on dialog close — the workshop is a singleton that
   outlives the radio (`SetRig(null)` stops it but nothing calls that on
   app-close teardown).
8c. **Key-up announcement is SAFETY-CRITICAL, not polish** — live
   software wire-keying left the operator transmitting unaware. Every
   keying path this track creates announces key-down and key-up,
   unconditionally. Related awareness: a hand mic on the rear RCA line
   keys HARDWARE — software unkey correctly cannot override it; the
   interlock `source=` field is the diagnostic; surface that state
   honestly if the session encounters it.
8d. **Mic-source coherence is the precondition** for every honest
   measurement this feature makes: surface the current TX mic source in
   the workshop (read + set), name it in session speech, and never let a
   check run against a source the controls aren't aimed at.
9. **TX-source awareness — aim controls at the ACTIVE source.** Verified
   live: MicGain adjusts the SELECTED input, so with a hand-mic PTT
   override the knob silently tunes an idle PC stream. The workshop must
   target the active source or say why it can't. Jack-only controls (Mic
   Boost, Mic Bias) annotate "radio mic jack only, not in use" when TX is
   PC-sourced — de-emphasized, never hidden. Session-start speech names
   the source. Investigate `FlexBase.cs:9147` (`MicInput = "PC"` on PC
   audio enable, yet the hand mic clearly fed TX — find the real
   selection semantics before coding the aiming logic).
9a. **Fix the Ctrl+Shift+W shadow.** The Global OpenAudioWorkshop chord
   (`KeyCommands.cs:1069`) got swallowed and spoke "changed units" —
   suspect a control-local `Keys.W` handler (near `KeyCommands.cs:1958`)
   or a saved keymap override. Find and fix; the workshop's front door
   must work from every focus position. (Track H sweeps the general
   class; you fix this instance.)
10. **Help rewrite:** `docs/help/md/audio-workshop.md` describes the dialog
    that exists plus the Audio Check session; delete the never-built
    routing/multi-output text. Warm voice, prose/bullets, no tables.

## Phase 3 — Loopback Check (buildable now; verified live, HONESTLY framed)

11. **The "Loopback check" button** per plan section 4's automation spec:
    snapshot FDX flag + TX antenna + monitor state + RF power + slice
    roster → set the verified recipe (FDX on, TX ant XVT A, ears slice
    same freq/mode on XVT A, 1W floor — power 0 is silent — monitor OFF)
    → teardown restores every saved value and removes the ears slice.
    Gate on 2 SCUs + `AvailableSlices >= 1` + XVT in the TX antenna list;
    Feature Availability explains absence. Keying rides the same
    PttSafetyController path as phase 1.
    **HONESTY REQUIREMENTS (final model, ratified):** what this yields
    today is real RF through a massively overloaded receiver — "a
    simulacrum of a signal, basically splatter" (Noel's ratified product
    framing). UI copy and speech claim presence/processing/rough-shape
    verification, NEVER a faithful off-air listen; SDR-on-a-real-antenna
    is named as the ground-truth tier in help.
    **NEW HARD REQUIREMENT — coupling level management:** raw
    adjacent-port coupling at 1W overdrives the receiver and distorts
    the listen. Reduce drive into the receiver's linear range using the
    XVTR band's dBm-precision `Xvtr.MaxPower` (-10.0..+10.0 dBm,
    hundredths), plausibly auto-calibrating against S-meter/overload
    indications. Whether this upgrades the listen to clean demodulation
    is an OPEN question — implement the mechanism, keep the copy honest
    either way. (Track I owns the USER-facing dBm power UI; you use the
    API programmatically — no conflict.)
12. **`FullDuplexEnabled` FlexBase wrapper** (Radio.cs:10167-10176) —
    JJFlex exposes it nowhere today; command-queue pattern. The loopback
    session sets and restores it; never leave it changed.
13. **Positioning (help + speech):** the loopback is also a transmitter
    self-test — "check my audio" and "is my radio actually transmitting"
    are the same button. Help caveat: what leaks over the air at 1W with
    antennas connected.

## Phase 2 — record/play (UNBLOCKED; semantics fully verified live)

14. **FlexBase wrappers** for the active slice's `RecordOn` / `PlayOn` /
    `PlayEnabled` (Slice.cs:1603-1645) — same command-queue pattern as the
    monitor properties (~line 7940 region). Verified telemetry:
    `record_time` caps at 120.0s; `play` transitions disabled → 0 when
    content exists; buffer behaves ring-like at cap (recent material
    kept); two takes coexist in one buffer (A/B comparison is a viable
    workflow).
15. **Record-then-listen loop:** auto-play-on-unkey is the DEFAULT
    (performed live — unkey → playback within a second; never demands
    talking and listening at once). **MUST check recorder state before
    re-arming** — a live re-arm race nearly wiped an operator's takes.
    Label fidelity honestly: record inherits the fidelity of whatever
    the slice hears (capture mechanism, not a fidelity tier); the
    recording carries the FULL processing chain (verified by A/B with
    the processor cranked). Works under FDX-off (1-SCU condition) —
    the record tap sits upstream of the TX audio mute.

## Ownership boundaries (do not cross)

- Yours: `AudioWorkshopDialog.xaml(.cs)`, `Radios/FlexBase.cs` ~7700-7960
  region (wrappers + CW pan promotion) plus the new FullDuplexEnabled
  wrapper, ONE unbound registration in `KeyCommands.cs` + the
  Ctrl+Shift+W shadow fix, `docs/help/md/audio-workshop.md` + help TOC.
- NOT yours: `docs/help/md/audio-troubleshooting.md` (Track B), all
  Settings surfaces (B/C/F), `RigSelectorDialog` (E), the Lineout
  `!PCAudio` gate and `LocalAudioMute` (Track A — same files, different
  regions; keep your diff away from theirs), the menu-parity audit and
  XVTR-aware power dialog (Track I — your loopback uses a fixed 1W and
  does not depend on it), the DAXIQ probe (orchestrator lab experiment).
- `PttSafetyController`: integrate with it; if it needs a new hook (e.g. a
  configurable soft timeout), add the minimal hook and flag it in your
  report — do not restructure the controller.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Verify: Escape semantics exactly as specified (a
session must never outlive its dialog); every state change speaks; no
silent keystrokes; blank-line/IsDefault NVDA rules on any dialog text you
touch. Anything that keys the radio gets triple-checked for the
unkey-on-every-exit-path guarantee.

## Commit style

Commit after each numbered item (or coherent group): `QB Track G: <what
changed>`. Push to `origin` (never `upstream`). Append a "Design decisions"
section here as you make calls; report completion to Noel when done.
