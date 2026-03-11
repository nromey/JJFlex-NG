# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.15 (released 2026-03-07), pending bump to 4.1.16

## Current State — Sprint 23 Complete, Testing Next

**Status:** Sprint 23 (QRM Fubar Dipole) merged to main. All 16 phases complete. 63 of 65 findings from Sprint 21+22 testing addressed. Guided testing needed before 4.1.16 release.

**Sprint 23 stats:** 30 files changed, +1,227 / -344 lines across 16 serial phases.

**Key documents:**
- Sprint 23 plan: `docs/planning/agile/sprint23-qrm-fubar-dipole.md`
- Test findings (input): `docs/planning/agile/sprint21-22-test-findings.md`
- Test matrix (Sprint 22): `docs/planning/agile/sprint22-test-matrix.md`

**Library versions bumped in Sprint 23:**
- JJFlexWpf: 2.0.0 → 2.1.0
- Radios: 3.2.5.1 → 3.2.6.0

**Next steps:**
1. Create Sprint 23 test matrix
2. Guided testing against all 63 findings
3. Version bump to 4.1.16 after testing passes
4. Update changelog (user-facing, warm tone)
5. Sprint 24 is pledged as VB-to-C# migration sprint

### 2 Findings Deferred to Backlog
- FINDING-25: Multi-radio abstraction — wait for second radio platform
- FINDING-48: Earcon fade-out/envelope — add selectively later

---

## Sprint 23: QRM Fubar Dipole — COMPLETE (16 phases)

**What landed:**
- Phase 1: Kill Classic menus — unified single menu structure
- Phase 2: Unified hotkey dispatch — all hotkeys through KeyCommands.vb scope system
- Phase 3: Earcon redesign — double-beep on/off, help/dialog/fail tones, NAudio earcon playback
- Phase 4: ScreenFields controls standardization — step sizes, speech, ValueFieldControl rewrite
- Phase 5: Comprehensive feature gating — license/hardware checks on all DSP features
- Phase 6: Slice management fixes — stale data, direct A/B select, SliceSelector control
- Phase 7: Audio Workshop accessibility — tab order, Escape close, focus management
- Phase 8: About dialog accessibility — tab navigation, content reading
- Phase 9: JJ key fixes, filter width speech, speak frequency, repeat last message (Ctrl+F4)
- Phase 10: ATU workflow fix (FlexTunerType not FlexTunerOn), 15s timeout, faster progress tone
- Phase 11: Welcome text update, 60m Channel 1 jump, country display names, dialog modality fix
- Phase 12: RX filter width display, filter width in edge speech, Filter Calculator dialog
- Phase 13: MultiFlex slice ownership in status speech and VFO cycling
- Phase 14: Command Finder category filtering fix, compiled CHM help file
- Phase 15: RigSelector empty list announcement, selected item speech
- Phase 16: Library version bumps, installer branding, clean build verified

---

## Sprint 22 — MERGED TO MAIN

**What landed:**
- Phase 1: RigSelector Auto-Connect
- Phase 2: About Dialog
- Phase 3: Command Finder categories
- Phase 4: Tuning speech debounce
- Phase 5: Tune Carrier (Ctrl+Shift+T) + ATU Tune (Ctrl+T)
- Phase 6: Antenna switching (menus + ScreenFields)
- Phase 7: Slice management + enhanced status (Ctrl+Shift+S)
- Phase 8: Startup speech on connect
- Phase 9: Meters panel + waveform UI
- Phase 10: 60m channelization

---

## Sprint 21: Resonant Signal Sculpt — MERGED TO MAIN

**What landed (65 files, +4,945 / -588 lines):**
- Track A: Meter Sonification Engine
- Track B: Audio Workshop dialog, ScreenFields TX expansion, AudioChainPreset
- Track C: Leader key system (Ctrl+J), TX filter keyboard sculpting, Command Finder extensions
- Track D: App renamed to "JJ Flexible Radio Access"
- Track E: Compiled HTML Help (22 pages, F1 context-sensitive)

---

## Completed Sprints
- Sprint 23: Fix sprint — 63 of 65 findings from Sprint 21+22 testing
- Sprint 22: Auto-connect, About, Command Finder, debounce, tune carrier, antenna, slices, startup speech, meters, 60m
- Sprint 21: Meter sonification, Audio Workshop, leader key, TX sculpting, CHM help, app rename
- Sprint 17: Band nav, license tuning, PTT enhancements, settings dialog, controls
- Sprint 16: PTT safety (Ctrl+Space), Dummy Load, Connection Tester
- Sprint 15: WPF RigSelector, filter overhaul, PTT safety infra, menu redesign
- Sprint 14: ScreenFieldsPanel, speech debounce, slice menu, filter race fix
- Sprint 13: Tab chain, Modern tuning, menus, SmartLink auto-retry
- Sprint 12: Stabilize WPF — menus, SmartLink, FreqOut tuning, error dialogs
- Sprint 11: WPF adapters, Form1 kill, dead code deletion (~13,000 lines)
- Sprint 10: FlexBase.cs decoupling (interfaces + delegates)
- Sprint 9: All dialogs to WPF (3 parallel tracks, 122 files)
- Sprint 8: Form1 -> WPF MainWindow
- Sprint 7: Modern Menu, Logging Polish (v4.1.14)
- Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System (v4.1.13)
- Sprint 5: QRZ/HamQTH Lookup & Full Log Access
- Sprint 4: Logging Mode (v4.1.12)
- Sprint 3: Classic/Modern Mode Foundation
- Sprint 2: Auto-Connect (v4.1.11)
- Sprint 1: SmartLink Saved Accounts (v4.1.10)

## Open Bugs
- BUG-004: FIXED (Sprint 21)
- BUG-013: Duplicate QSO warning beep not playing
- BUG-015: F6 double-announces "Radio pane" in Logging Mode
- BUG-016: FIXED (Sprint 23 Phase 5 — comprehensive feature gating)
- BUG-023: FIXED (Sprint 21, wording improved Sprint 23 Phase 11)

## Build Commands

```batch
# Debug build for testing
dotnet clean JJFlexRadio.vbproj -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal

# Clean + rebuild Release (triggers NSIS installer)
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

---

*Updated: Mar 11, 2026 — Sprint 23 complete (16 phases, 63 findings fixed). Testing next, then 4.1.16 release. Sprint 24 pledged as VB-to-C# migration.*
