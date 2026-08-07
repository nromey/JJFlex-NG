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

## Launch gate (should already be satisfied when you start)

Verification items 1 and 4 (plan section 6) answered from the 8600 session:
record/play semantics, and whether `MicGain` acts on PC-sourced TX audio.
Look for the results section Phil2 appended. If item 1 is still unanswered,
build phase 1 only and stop before phase 2. If item 4 is unanswered, stub
the TX-source annotation copy behind a single helper so the wording is a
one-line change later.

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
8. **Mode-aware monitor section:** CW mode shows CW Monitor Gain
   (`CWMonitorGain`; promote internal `MonitorPan` to public for CW pan),
   phone modes show the SB fields; section header names the mode.
9. **TX-source annotations:** when TX audio is PC-sourced, jack-only
   controls (Mic Boost, Mic Bias) annotate "radio mic jack only, not in
   use" — de-emphasized, never hidden. Session-start speech names the
   source: "Transmitting, audio from this computer."
10. **Help rewrite:** `docs/help/md/audio-workshop.md` describes the dialog
    that exists plus the Audio Check session; delete the never-built
    routing/multi-output text. Warm voice, prose/bullets, no tables.

## Phase 2 — only after verification item 1 answers

11. **FlexBase wrappers** for the active slice's `RecordOn` / `PlayOn` /
    `PlayEnabled` (Slice.cs:1603-1645) — same command-queue pattern as the
    monitor properties (~line 7940 region).
12. **One-button record-then-listen loop:** "Record a test transmission" →
    safety line → key + record → talk → press again or 15s cap → unkey,
    stop record, auto-play → adjust → repeat. Exact semantics per the
    verification results (does playback contain TX audio; does PlayOn while
    keyed parrot; what PlayEnabled gates).

## Ownership boundaries (do not cross)

- Yours: `AudioWorkshopDialog.xaml(.cs)`, `Radios/FlexBase.cs` ~7700-7960
  region (wrappers + CW pan promotion), ONE unbound registration in
  `KeyCommands.cs`, `docs/help/md/audio-workshop.md` + help TOC.
- NOT yours: `docs/help/md/audio-troubleshooting.md` (Track B), all
  Settings surfaces (B/C/F), `RigSelectorDialog` (E), the Lineout
  `!PCAudio` gate and `LocalAudioMute` (Track A — same files, different
  regions; keep your diff away from theirs).
- Loopback (plan section 4): do not build ANY of it, even scaffolding —
  it is a future track gated on Don's answers.
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
