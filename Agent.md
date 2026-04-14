# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint25/track-a`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 10 LTS (migrated 2026-04-13), dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.16 (version bumped in Sprint 25 Phase 12)

## Current State — Sprint 25 In Progress

**Status:** All coding phases complete (13-21 + 20). Remaining: Phase 22 (changelog after testing). Testing needed for all phases.

### Phase 20: RX Audio Pipeline — COMPLETE
Wired RNNoise and spectral subtraction into the live RX audio path:
- Architecture: Opus decode → gain → PostDecodeProcessor delegate → PortAudio queue
- RxAudioPipeline class (JJFlexWpf) orchestrates chain: spectral sub → RNNoise
- Providers got ProcessInPlace() methods + standalone constructors for push-based pipeline
- JJAudioStream.PostDecodeProcessor delegate bridges JJPortaudio ↔ JJFlexWpf (no circular deps)
- FlexBase.AudioPostProcessor forwards to active stream, handles set-before and set-after audio start
- ScreenFieldsPanel creates pipeline on connect, disposes on detach, feeds mode changes
- PC-side NR works on ALL radios (6000/8000/Aurora) — processing runs on PC, not radio hardware
- UI: "PC Neural NR" and "PC Spectral NR" toggles in DSP section
- Hotkeys: Ctrl+J, Shift+R (PC RNNoise), Ctrl+J, Shift+S (PC Spectral NR)
- Foundational for waterfall FFT tap, recording tap, DSP abstraction layer

**Sprint 25 plan:** `docs/planning/barefoot-qrp-ragchew.md`
**Test matrix:** `docs/planning/agile/sprint25-test-matrix.md`

### Completed Phases (original plan)
- Phase 1: Quick fixes (hotkey conflict, ding tone, slice clamp, status dialog)
- Phase 2: Dynamic slice ops label, modern mode freq navigation
- Phase 3: Sliders to ValueFieldControl, ModeChanged event
- Phase 4: Earcon mute toggle, connection state menu rebuild
- Phase 5: Focus-return context announcement
- Phase 6a: Access key announcements (138 buttons, 37 dialogs)
- Phase 6b: Menu checkbox On/Off state announcements
- Phase 7: Easter egg unlock system, typing sounds, DTMF synthesis
- Phase 8: Braille display status line Phase 1
- Phase 9: Action toolbar (Ctrl+Tab)
- Phase 10: RNNoise ISampleProvider (not wired to audio yet)
- Phase 11: Spectral subtraction ISampleProvider (not wired to audio yet)
- Phase 12: Version bump to 4.1.16, library bumps, changelog started

### Additional Work (beyond original plan)
- SmartLink multi-account: per-account WebView2 cookie jars for session isolation
- Switch Account button on RigSelector, Set Default in Manage SmartLink
- FlexLib upgraded to v4.1.5.39794 (8000-series/Aurora compatibility)
- NR gating corrected: NRF/NRS/RNN all 8000/Aurora only, NRF exposed
- CalibrationEngine obfuscation: pre-computed hashes, no plaintext unlock words
- WebView2 retry on folder lock, connection port tracing

### Interactive Fixes (all complete)
- Status Dialog: pauses refresh when focused, deferred first-item focus
- WebView2 auth: moved to UI thread, no-radios dialog, deadlock fix
- WebView2 folder lock: reverted to shared folder (per-account caused E_ABORT)
- Set Default speech: 200ms delay survives focus-return interruption
- Typing sounds: added to Ctrl+F dialog, per-digit pitch beeps
- Band boundary checks on Ctrl+F and quick-type
- Slice boundaries: Space wraps, Up/Down clamps with speech including slice letter
- Modern tuning: F2 focus, letter filtering, frequency speech
- Connection retry: lightweight RetryConnect, revert early abort to 5s
- Live title bar status: Insert+T reads slice/freq/mode
- Mechanical keyboard sound gain boost (4x→8x)

### Foundational Features/Fixes
**DONE (untested — need directed testing session):**
- Phase 13: RigSelector "Press Remote" label fix + "(Default)" indicator in Manage SmartLink
- Phase 14: Expanded typing sounds — Single tone, Random tones, Musical notes (always available)
- Phase 15: CW prosign notifications (AS/BT/SK) + Verbosity & Notifications tab in Settings
  - ICwNotificationOutput abstraction for future haptics/vibration
  - MorseNotifier engine with configurable sidetone/WPM
  - Wired: AS on slow connection, BT on connect, SK on app close, CW mode announce when speech off
- Phase 16: Editable filter presets dialog (Settings → Tuning → Edit Filter Presets)
- Phase 17: Editable tuning step presets dialog (Settings → Tuning → Edit Tuning Steps)
- Phase 18: Braille status line display-size-aware formatting (20/32/40/80 cell profiles)
- Phase 19: MultiFlex management — client list dialog, connect/disconnect earcons + speech, kick clients
- Phase 20: RX audio pipeline — RNNoise + spectral subtraction wired into live Opus decode path, PC-side NR for all radios
- Phase 21: Trace cleanup — removed SmartLink dialog debug traces, connection traces are properly gated

**REMAINING:**
- Phase 22: Changelog finalization — after all testing complete

### Testing Results (so far)
- Earcon mute: PASS
- Menu checkbox On/Off: PASS
- Focus-return context: PASS
- Slice clamping: PASS
- Slice Operations label: PASS
- Modern mode freq skip: PASS
- Action toolbar: PASS
- Ding tone on freq entry: PASS
- Hotkey conflict fixed: PASS
- Access keys: PASS
- SmartLink multi-account (Don): PASS
- SmartLink multi-account (Justin): Auth works, radio connection fails (network/NAT issue, not our code)

### Cleanup
- Delete `FlexLib_API_v4.1.5.39794/` folder (already integrated into FlexLib_API, untracked)
