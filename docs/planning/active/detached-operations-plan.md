# Detached operations — implementation plan (merges the two research memos)

Written 2026-08-05 by the orchestrator session after both research memos
landed. Sources, read them before building:

- `detached-firmware-research.md` — SmartSDR update flow, progress truth,
  watcher fixes
- `detached-registration-research.md` — WanRegisterRadio detached path,
  handshake state machine, unregister

## The one-paragraph design

Both operations fail today for the same reason: the radio refuses service
operations from (or around) a connected GUI client. Both succeed in
SmartSDR because it operates from the chooser, detached. So JJ Flex gets
ONE shared engine that takes the app from connected → detached → back,
and two operation implementations that plug into it. The engine owns the
announcements ("Disconnecting from the radio…", "Reconnecting…"), the
power-off/reconnect mechanics, and the safety rails (Escape hides, never
aborts; watchdogs on every wait state).

## Decisions reconciling the memos

- **Engine name and home: `DetachedRadioSession`, new file
  `Radios/DetachedRadioSession.cs`.** (Firmware memo's name and location;
  the registration memo's `RunDetachedAsync` shape becomes its main
  method.) FlexBase gets thin wrappers; the dialogs call those.
- **Engine API sketch:**
  `Task<DetachedResult> RunAsync(DetachedOperation op, IProgress<string> speech, CancellationToken watchCancel)`
  where op supplies: preflight checks, the detached work (given a bare
  radio object), the completion watcher, and terminal speech. The
  engine sequence: preflight → announce → app power-off +
  `FlexBase.Disconnect()` → wait for re-discovery with our station
  absent (discovery packets carry the GUI-client list — verifiable with
  zero connection) → run op → op's watcher → restore `API.IsGUI = true`
  → reconnect via captured serial/lowBW → announce.
- **Registration uses FlexLib's own detached path, no FlexLib changes:**
  `WanRegisterRadio` on a NOT-connected radio object dials its own raw
  TCP :4992 with `_ignoreConnectedEvents`, no `client gui`, and
  disconnects itself on completion. The connected branch we hit on
  2026-08-04 is vendor-untested (SmartSDR cannot reach it) — never use
  it. Registration is LAN-gated like SmartSDR does it.
- **Firmware uses `API.IsGUI = false` + explicit
  `client start_persistence off`, then `SendUpdateFile`,** with the
  radio's REAL progress (`file update transfer=<pct>` → `UploadStatus`
  PropertyChanged) bound to an actual ProgressBar — NVDA's own progress
  announcement setting does the cadence; we speak phase boundaries +
  opt-in milestones. (SmartSDR's bar is a fixed 360-second animation;
  ours will be real.)
- **Token freshness:** registration refreshes the id_token immediately
  before `WanRegisterRadio` (new `forceRefresh` param on
  `GetJwtFromSavedAccount`) — 60-second tokens make this mandatory, and
  SmartSDR's "retry worked" was the human re-pressing the button, which
  re-refreshed. We do ONE automatic retry with a fresh token on server
  refusal, then report.
- **Speech contract:** state callbacks pass ENUMS, not prose (current
  dialog string-matches "key the microphone" — kill that). wait_on_ptt
  speaks at Critical: "Press your PTT now — key the microphone or CW
  key at the radio. About twenty seconds." failed_ptt distinguishes
  timeout from the instant same-millisecond refusal ("the radio believes
  PTT is already active — make sure no other client is connected").
- **Watcher fixes (firmware):** keep our `WatchFirmwareUpdateAsync` but
  fix the success-path hole (discovery maid never removes an Updating
  radio, so the FindDiscoveredRadio==null phase can't fire on success —
  last night's verification only worked because the FAULT cleared the
  flag), subscribe `UpdateFailed`, and adopt SmartSDR's recovery
  auto-resend (minus its 6x00-only filename bug).
- **Unregister ships with registration** — same engine, same PTT proof
  (the radio requires physical keying to unregister too; good — it's a
  possession check), resale-worded success speech, keeps the existing
  stranding warnings.

## Build order

1. `DetachedRadioSession` engine + firmware operation — testable TONIGHT
   via same-version 4.2.20 reflash with "Choose a file instead" on the
   8600 (LAN).
2. Registration + unregister operation — the 8600 is already registered,
   so live testing waits for a deliberate unregister/re-register cycle
   (Noel's call when) or Tony/Don hardware later. Code ships build-now-
   ship-later regardless.
3. C2 item 5b (ConfirmActionDialog warnings readable) should land before
   the live flash so the do-not-power-off warning is readable.

## Hardware-verification items (from both memos — check during first live runs)

- Bare-client (IsGUI=false) upload survives past the 1.4s kick point.
- Capture the radio's full `file update` vocabulary during a real upload
  (including the bare `active`/`detected` tokens FlexLib logs as invalid).
- Radio accepts `client start_persistence off` from a non-GUI client.
- Whether the `wan register` command reply is held until the handshake
  reaches a terminal state (SmartSDR's flow implies yes) — first thing
  to check in the traced live run.
- Reboot timing on the 8600 vs the watcher's phase timeouts.

## Scope

Roughly 900-1200 LOC total across: the engine (~250-350), firmware
watcher rework + vendor patch (~200-280, MIGRATION-tracked), firmware
dialog rework (~200-300), registration rework + forceRefresh + dialog
rewire (~285), advisory/docs touches. Vendor patches stay
comment-marked JJFlex per house style.
