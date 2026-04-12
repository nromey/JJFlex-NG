# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint25/track-a`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.16 (version bumped in Sprint 25 Phase 12)

## Current State — Sprint 25 In Progress

**Status:** Interactive fixes complete. Planning foundational features/fixes before coding. Next up: waterfall sprint after 25 ships.

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

### Foundational Features/Fixes (planning, not yet coded)
1. CW prosign status notifications — AS (connecting/slow), BT (connected), SK (app close); mode changes in CW when speech off; uses configured sidetone freq/speed; output provider abstraction for future haptics/vibration
2. Wire RNNoise + spectral subtraction into audio pipeline — ISampleProviders exist, need pipeline integration
3. MultiFlex management — toggle MultiFlex on/off, connect/disconnect notifications (earcon + speech), connected client list, kick/disconnect clients to reclaim slices
4. Expanded typing sound modes — add single fixed tone, random tones, musical notes as always-available (unlocked) options alongside existing easter egg modes (Mechanical, DTMF)
5. Editable filter presets — create, edit, save, load, share filter presets as XML
6. Editable tuning step presets — user adds/removes from coarse/fine step lists, persisted per operator
7. Braille status line display-size-aware formatting — different content layouts per display width (20/32/40/80), foundation for Dot Pad X
8. Default radio indicator — Manage SmartLink / radio selector should indicate which account/radio is the default (screen reader + visual)
9. RigSelector label fix — says "press Connect to SmartLink" but the button is actually "Remote"; fix the instructional text
10. Trace cleanup — audit and remove verbose dev tracing from Sprint 25 that inflates log files
11. Changelog finalization — after all work complete

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
