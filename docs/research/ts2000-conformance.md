# TS-2000 Conformance Scope

**Status:** Multi-radio Phase 7 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to architecture synthesis (Phase 9); future test matrix
**Builds on:** Phases 2-6, plus `project_ts2000_cross_class_testbed.md`

---

## What this document is

`project_ts2000_cross_class_testbed.md` named Noel's Kenwood TS-2000 as the
primary cross-class testbed for `IRadioBackend`. The TS-2000 covers HF + VHF +
UHF + (1.2 GHz on X variant) + satellite + every standard mode — a single
device that exercises most of the abstraction's design surface. If
`HamlibBackend` operating a TS-2000 cleanly passes the conformance list below,
the abstraction is probably sound; if not, where it fails identifies real
holes in the design.

This document specifies the conformance surface — what the TS-2000 backend
must do to declare the abstraction "smoke-tested" — without yet committing to
when the implementation work happens.

---

## 1. Why the TS-2000 specifically

Noel owns one (no external-tester dependency). Hamlib's `rigs/kenwood/ts2000.c`
is mature (well-tested, frequent commits). The radio is a *bridge* between
classes: it operates as an HF transceiver, a VHF/UHF mobile, and a satellite
station all in one chassis. Single device exercises:

- Single-VFO and dual-VFO operation
- Main + sub receiver (parallel listening)
- Cross-band (uplink HF + downlink UHF or vice versa)
- Cross-mode (uplink USB + downlink LSB for inverting transponders)
- Satellite mode where two VFOs are linked
- Standard CAT for HF
- All the FM mobile primitives when in 2m/70cm FM mode
- Built-in TNC for packet
- Memory channels (300+ on TS-2000)
- ATU built-in
- Network audio path NOT applicable (CAT-only radio)
- Soundcard audio path required (data port + speaker output)

Other test radios test a SUBSET of these:

- TS-590 (Mark) — HF only, single-VFO-pair, no satellite, no VHF/UHF
- TM-V71A (Doug) — VHF/UHF FM mobile only, no HF, no satellite
- Flex 6300 (Don) — SDR class entirely, parallel architecture

The TS-2000 is the **superset cross-check.**

---

## 2. The conformance test list

Each item below is a discrete behavior the TS-2000 backend must demonstrate
on real hardware. The list is the smoke test for the abstraction; passing
all of these means `IRadioBackend` is real.

### 2.1 Connection lifecycle

- **C-1:** `Connect` to TS-2000 over COM port at 9600 baud succeeds.
- **C-2:** `Connect` reports the radio's firmware version in `Facts.FirmwareVersion`.
- **C-3:** `Connect` populates `Capabilities` from `rig_get_caps()` —
  capability set includes the standard HF set + `SatelliteMode`,
  `BuiltInTNC`, `MainSubReceiver`, `Diversity = false` (TS-2000 doesn't have it).
- **C-4:** `Disconnect` is clean (no exception, no zombie state).
- **C-5:** Reconnect after disconnect works (no per-process state pollution).
- **C-6:** Connection failure (wrong baud rate, port unavailable) returns the
  right `ConnectResult` enum without throwing.
- **C-7:** `ConnectionStateChanged` fires on connect, on disconnect, and on
  observed connection loss (cable yanked → event fires within reasonable
  time).

### 2.2 Frequency, mode, and basic operation

- **F-1:** Read VFO A frequency on HF (e.g. 14.225 MHz, USB).
- **F-2:** Set VFO A frequency to 7.090 MHz, USB.
- **F-3:** Set VFO A mode to LSB while keeping frequency.
- **F-4:** Read VFO B frequency (initially same band as A).
- **F-5:** Set VFO B to a different band (set to 145.500 FM) — TS-2000's main
  receiver continues on HF.
- **F-6:** Switch active VFO (slice 0 vs slice 1 in JJF terms).
- **F-7:** Toggle split TX (VFO A RX, VFO B TX).
- **F-8:** Set explicit split TX frequency (`rig_set_split_freq`).
- **F-9:** Read frequency-and-mode-and-split composite via `rig_get_vfo_info`
  in one CAT exchange.
- **F-10:** Tuning step changes (250 Hz → 100 Hz → 1 kHz on HF; 5 kHz → 12.5
  kHz on VHF/UHF).

### 2.3 PTT and CW

- **P-1:** PTT on (`rig_set_ptt RIG_PTT_ON`) keys the radio's TX.
- **P-2:** PTT off releases TX.
- **P-3:** PTT state read matches actual radio state (round-trip).
- **P-4:** `rig_send_morse "CQ DE K5NER"` sends CW at the radio's current
  keying speed.
- **P-5:** `rig_stop_morse` stops mid-message.
- **P-6:** Keying speed change via `rig_set_level RIG_LEVEL_KEYSPD` updates
  speed for next morse send.

### 2.4 Levels (analog controls)

- **L-1:** AF gain read returns 0.0..1.0 float.
- **L-2:** AF gain set works; readback matches.
- **L-3:** RF gain read/set.
- **L-4:** Squelch threshold read/set.
- **L-5:** TX power read/set.
- **L-6:** S-meter read returns valid value (when signal present, value is
  non-zero; when squelched, low).
- **L-7:** SWR read returns valid value during transmit.
- **L-8:** Mic gain read/set.
- **L-9:** AGC mode set (slow/medium/fast).
- **L-10:** Filter shift / passband adjust.
- **L-11:** RIT offset set/clear.
- **L-12:** XIT offset set/clear.

### 2.5 Functions (boolean toggles)

- **B-1:** Noise blanker toggle.
- **B-2:** Noise reduction toggle.
- **B-3:** Speech compressor toggle.
- **B-4:** VOX toggle.
- **B-5:** CTCSS encode toggle.
- **B-6:** Tone squelch toggle.
- **B-7:** Repeater shift on/off.
- **B-8:** Lock toggle.
- **B-9:** Mute toggle.
- **B-10:** ATU toggle (manual tune start, auto-tune mode).
- **B-11:** Auto frequency control (AFC) toggle.

### 2.6 Memory channels

- **M-1:** Read channel 0 — frequency, mode, name (if set).
- **M-2:** Write a memory channel with frequency + mode + name.
- **M-3:** Read all 300+ memory channels (bulk read; tolerance for slow CAT
  rate — should complete in < 60 seconds).
- **M-4:** Delete a memory channel.
- **M-5:** Recall (set current operating frequency to) a memory channel.
- **M-6:** Memory channel labels with non-ASCII characters (where supported)
  preserved correctly.

### 2.7 Antenna

- **A-1:** Read current antenna selection (TS-2000 has ANT1, ANT2 for HF; RX
  antenna jack for receive).
- **A-2:** Switch HF antenna (ANT1 → ANT2).
- **A-3:** Switch RX antenna (RX-A vs internal).

### 2.8 Satellite mode

- **S-1:** Enter satellite mode (`rig_set_func RIG_FUNC_SATMODE 1`).
- **S-2:** Set uplink frequency on main VFO (`rig_set_freq RIG_VFO_MAIN`).
- **S-3:** Set downlink frequency on sub VFO (`rig_set_freq RIG_VFO_SUB`).
- **S-4:** Set cross-mode (main USB, sub LSB for inverting transponder).
- **S-5:** Linked tracking — when uplink frequency changes, downlink tracks
  with the same delta (TS-2000 supports this in some modes).
- **S-6:** Exit satellite mode and verify HF state restored.

### 2.9 VHF/UHF FM operation (the mobile-class subset)

- **V-1:** Set VFO B to 146.520 MHz, FM.
- **V-2:** Set CTCSS encode tone on VFO B (e.g. 100.0 Hz).
- **V-3:** Set repeater offset (+600 kHz on VHF, -5 MHz on UHF).
- **V-4:** Read squelch open/closed status (via DCD).
- **V-5:** Switch tuning step to 12.5 kHz.

### 2.10 Built-in TNC

- **T-1:** Enable internal TNC for packet.
- **T-2:** Set TNC mode (1200 baud / 9600 baud).
- **T-3:** Disable TNC, return to normal CAT operation.

The capability flag `BuiltInTNC` is true; the actual packet operation
(sending/receiving AX.25 frames) is out of scope for the conformance test —
we'd verify TNC enable/disable via CAT, but not actual packet sending.

### 2.11 Power state

- **W-1:** `rig_get_powerstat` returns "on."
- **W-2:** `rig_set_powerstat OFF` powers the radio down (with explicit user
  consent, per flexibility principle — not done in automated tests).
- **W-3:** Re-power-on requires physical action (TS-2000 doesn't support CAT
  power-on; document this).

### 2.12 Capability introspection

- **K-1:** `Facts.Class == RadioClass.AllModeAllBand`.
- **K-2:** `Facts.SupportedBands` includes all expected bands (160m through 70cm,
  + 1.2 GHz on X variant).
- **K-3:** `Facts.SupportedModes` includes USB, LSB, CW, AM, FM (and FM-N,
  AM-N where supported).
- **K-4:** `Facts.MaxMemoryChannels == 300` (TS-2000 spec; verify per radio).
- **K-5:** `Facts.AntennaPorts.Count == 3` (ANT1, ANT2, RX).
- **K-6:** `Capabilities.Contains(RadioCapability.SatelliteMode)`.
- **K-7:** `Capabilities.Contains(RadioCapability.BuiltInTNC)`.
- **K-8:** `Capabilities.Contains(RadioCapability.AutoTunerStart)`.
- **K-9:** `Capabilities.Contains(RadioCapability.MainSubReceiver)`.
- **K-10:** `Capabilities.DoesNotContain(RadioCapability.MultiSliceCapable)`
  (TS-2000 isn't an SDR; it's main+sub, not multi-slice).
- **K-11:** `Capabilities.DoesNotContain(RadioCapability.Diversity)`.
- **K-12:** `Capabilities.DoesNotContain(RadioCapability.PanadapterStream)`.

### 2.13 Audio routing (with HamlibBackend + IAudioRouter)

- **U-1:** Audio device picker enumerates the SignaLink USB / RIGblaster /
  TS-2000's own data port USB if connected.
- **U-2:** "Test Receive" button on the audio dialog audibly confirms RX
  audio reaches PC ("Audio is reaching this device").
- **U-3:** "Test Transmit" tone reaches the radio's mic input (verifiable by
  RX side hearing it during local loopback).
- **U-4:** RX audio flows into JJF's audio pipeline; NR and spectral sub
  apply.
- **U-5:** TX audio (live mic input) flows out to the radio when PTT is on,
  muted when PTT is off.
- **U-6:** Latency from PTT-press to audio reaching radio < 100 ms (CW use).

### 2.14 Threading and events

- **E-1:** `FrequencyChanged` event fires when operator turns the dial on
  the radio (transceive on TS-2000 — `RIG_FUNC_TRANSCEIVE`).
- **E-2:** Event fires within 200 ms of the actual change.
- **E-3:** Event fires on UI thread (no `InvokeRequired` issues in handler).
- **E-4:** Concurrent calls to multiple `IRadioBackend` operations
  (e.g. simultaneously setting frequency on slice 0 and reading
  S-meter) don't race — operations serialize correctly within the backend.

### 2.15 Error handling

- **R-1:** Cable disconnect during operation triggers
  `ConnectionStateChanged` with `IsConnected = false` and
  `ErrorOccurred` event with descriptive payload.
- **R-2:** Set-frequency operation timing out (radio non-responsive)
  surfaces as a method failure with appropriate `RadioError` enum value.
- **R-3:** Subsequent connect attempt after error works (no stuck state).
- **R-4:** Operation cancellation via `CancellationToken` returns promptly.

### 2.16 Per-radio config persistence

- **G-1:** Adding the TS-2000 creates `radios\hamlib-1011-<key>\connection.json`.
- **G-2:** Connection params (port COM3, baud 9600) persist across JJF restarts.
- **G-3:** Antenna labels saved in `antennas.json` survive restart.
- **G-4:** Disconnecting and reconnecting reloads per-radio config without re-prompt.

### 2.17 Accessibility

- **X-1:** All controls in the "Add Radio" workflow are keyboard-reachable.
- **X-2:** Audio device picker dialog is screen-reader-friendly (NVDA reads
  device list correctly, focus announcement clear).
- **X-3:** "Test Receive" / "Test Transmit" results are audibly announced.
- **X-4:** Capability changes (entering satellite mode) audibly announce
  "Satellite mode on" via `ScreenReaderOutput`.
- **X-5:** Connection state changes audibly announce ("Connected to TS-2000
  Shack" / "Disconnected from TS-2000 Shack").

---

## 3. What "passes" looks like

**All items pass:** `IRadioBackend` is solid for the All-Mode All-Band class.
The remaining classes (HF Transceiver, VHF/UHF FM Mobile, HT, RX-only) are
subsets of what TS-2000 exercises. The architecture is greenlit for further
implementation.

**Some items fail in surfaceable ways:** the failures identify gaps in the
abstraction. Examples we anticipate:

- "Linked tracking in satellite mode" (S-5) might surface a need for an
  explicit `LinkedSlices` capability + per-slice "linked-to-slice-N"
  property.
- "Concurrent operations don't race" (E-4) might surface need for explicit
  serialization, or for declaring some operations as "best-effort, no
  guarantee of atomicity."

These would feed back into Phase 4's interface design before broader
implementation. **Failing the conformance is a feature, not a bug** — it
tells us the abstraction needs revision before we commit to TM-V71A and
TS-590 implementations.

---

## 4. Test sequencing

Conformance is run in the order:

1. **Connection lifecycle (C-*)** — without these working, nothing else can be tested.
2. **Capability introspection (K-*)** — verify the radio's reported
   capabilities match expectations before exercising them.
3. **Frequency, mode, basic operation (F-*)** — the bread-and-butter.
4. **PTT and CW (P-*)**, **Levels (L-*)**, **Functions (B-*)** — broad
   surface coverage.
5. **Memory channels (M-*)** — bulk operations and persistence.
6. **Antenna (A-*)** — small surface, verify before more exotic tests.
7. **Satellite mode (S-*)** — exercises the `MainSubReceiver` + cross-band
   logic.
8. **VHF/UHF FM operation (V-*)** — exercises FM mobile-class primitives.
9. **Built-in TNC (T-*)** — tests TNC capability flags.
10. **Power state (W-*)** — last because of safety (not running in automation).
11. **Audio routing (U-*)** — depends on a configured `IAudioRouter` and is
    the most operator-driven test.
12. **Threading and events (E-*), Error handling (R-*)** — ambient tests
    that run during the others; also explicit tests for forced errors.
13. **Per-radio config persistence (G-*)** — verify across restart cycles.
14. **Accessibility (X-*)** — verify with NVDA + JAWS test passes.

This is the test order. The actual implementation order is different — the
backend implements one capability surface at a time, and conformance for a
given surface runs as soon as it's implementable.

---

## 5. Test execution model

### 5.1 What can be automated

- All connection lifecycle (C-*) — automated via test harness.
- Capability introspection (K-*) — automated.
- Frequency, mode, levels, functions — automated, but verifying the radio's
  display matches what JJF reports requires a visual confirmation step that
  isn't automatable. JJF test harness reports "set 14.225 — readback says
  14.225"; the visual confirmation that the *actual radio* shows 14.225 is
  a manual sanity check.
- Memory channel reads/writes — automated, with a backup-restore pattern (read
  all channels first, run test, restore from backup) so we don't trash
  Noel's existing memory configuration.
- Antenna switching — automated; visible side-effect on the radio.

### 5.2 What requires manual execution

- Audio routing (U-*) — requires Noel's ears for "Test Receive" /
  "Test Transmit" confirmation. The harness drives, Noel confirms.
- Threading edge cases (E-*) — concurrent operations are reproducible but
  some race conditions only manifest under specific timing.
- Accessibility (X-*) — requires NVDA / JAWS test passes, manual.

### 5.3 What requires special-purpose hardware setup

- Satellite mode (S-*) — needs an actual satellite pass to confirm
  end-to-end, but the CAT-level tests are doable on the bench (set uplink
  and downlink frequencies, verify radio is listening on uplink mode).
  Full end-to-end satellite QSO test is *post-conformance*, validation of
  satellite UX rather than abstraction conformance.
- Power state (W-*) — exercised once with explicit Noel consent; not
  automated to avoid accidental power-down.

---

## 6. The "definition of done" for a TS-2000-class conformance pass

The test list passes when:

- All automated tests in C-*, K-*, F-*, P-*, L-*, B-*, M-*, A-*, V-*, T-*, E-*,
  R-*, G-* pass on a real TS-2000 connected to JJF.
- Manual tests in U-* and X-* pass with Noel + NVDA confirmation.
- Satellite-mode CAT-level tests in S-1 through S-4, S-6 pass; S-5 (linked
  tracking) is verified with manual frequency-pair adjustments rather than
  full QSO.
- Power state W-2 verified with explicit consent; W-3 documented as expected
  behavior (no auto-power-on supported).
- Audio routing exercises with both a SignaLink and the TS-2000's data
  jack-to-USB adapter (whichever Noel uses) — confirm both work.

Once this passes, JJF has a real Hamlib backend operating a real radio. The
broader Hamlib coverage (TS-590, TM-V71A, future radios) is incremental from
that point — same backend code, same `rig_get_caps`-driven capability
mapping, different `rig_model_t`.

---

## 7. Forward references

This document feeds:

- **Phase 8 (Tester onboarding)** — Noel's TS-2000 setup is the first
  onboarding pass for a non-Flex radio; the conformance list informs what
  must work before Mark and Doug get involved.
- **Phase 9 (Architecture synthesis)** — TS-2000 as the conformance gate is
  a pillar of the synthesis.
- **Future test matrix** — once an implementation sprint commits, this
  conformance list becomes the test matrix for that sprint's exit criteria.

---

## Document Status

**Phase 7 of 10 complete.** ~80 conformance tests across 17 categories, with
sequencing, automation/manual split, and definition-of-done. Pass/fail of the
TS-2000 conformance is the smoke test for the full multi-radio architecture.

**Estimated read time:** 12-15 minutes for full review; 6 minutes for
sections 2.8 (satellite) and 2.13 (audio) alone.
