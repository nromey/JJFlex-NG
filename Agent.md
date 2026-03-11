# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.15 (released 2026-03-07), pending bump to 4.1.16

## Current State — Sprint 21 + 22 Testing

**Status:** Sprints 21 and 22 merged to main. Guided testing in progress (2026-03-10). 65 findings logged. Fix sprint needed before release.

**Key documents:**
- Test matrix: `docs/planning/agile/sprint22-test-matrix.md`
- Detailed findings: `docs/planning/agile/sprint21-22-test-findings.md`
- Sprint 21 plan: `docs/planning/agile/sprint21-resonant-signal-sculpt.md`

### Build Fixes Applied During Testing
- 6 build errors on main — Sprint 22 never built the full solution
- Changed `internal` to `public` on 4 methods in MainWindow.xaml.cs
- Added `tuning` to FunctionGroups enum in KeyCommands.vb

### Testing Summary (65 findings)

**What's working well:**
- JJ key system (Ctrl+J leader key) — core interaction solid
- TX filter sculpting (bracket keys)
- 60m channelization (channels, navigation, mode, menus)
- Antenna switching (menus, ScreenFields, checkmarks)
- ScreenFields Transmission expansion (all controls present and tabbable)
- Startup speech content (correct info)
- Shutdown fix (BUG-004 clean exit)
- Tuning debounce (settings UI works)
- Connect confirmation (BUG-023 dialog exists)
- ATU Tune Ctrl+T correctly detects no ATU

**What needs major work:**
- Audio Workshop (can't tab, can't close with Escape — non-functional)
- Meter tones (unreachable — Ctrl+M conflicts, no menu entry, no tab access)
- About dialog (inaccessible — can't navigate tabs or content)
- Hotkey conflicts (6+ conflicts across two dispatch systems)
- Feature gating / BUG-016 (Neural NR toggles on unsupported radios)
- Slice management (stale data, create broken, A/B keys toggle instead of direct select)
- CHM help file (never compiled — F1 silently fails)
- Command Finder category filtering broken

**Architecture decisions from testing:**
- Kill Classic menus — one menu structure, Ctrl+Shift+M just toggles tuning mode
- Unify all hotkeys into KeyCommands.vb scope system
- Standardize ScreenFields controls (step sizes, speech, interaction)
- Add "repeat last message" feature
- Earcon redesign (double-beep patterns for on/off/help/dialog, fade-out support)

---

## Sprint 22 — MERGED TO MAIN (pending testing/fixes)

**What landed:**
- Phase 1: RigSelector Auto-Connect
- Phase 2: About Dialog (accessibility issues found in testing)
- Phase 3: Command Finder categories (filtering broken)
- Phase 4: Tuning speech debounce
- Phase 5: Tune Carrier (Ctrl+Shift+T) + ATU Tune (Ctrl+T)
- Phase 6: Antenna switching (menus + ScreenFields)
- Phase 7: Slice management + enhanced status (Ctrl+Shift+S multi-slice detail)
- Phase 8: Startup speech on connect
- Phase 9: Meters panel + waveform UI
- Phase 10: 60m channelization

---

## Sprint 21: Resonant Signal Sculpt — MERGED TO MAIN

**What landed (65 files, +4,945 / -588 lines):**
- Track A: Meter Sonification Engine (ContinuousToneSampleProvider, MeterToneEngine, AudioOutputConfig, peak watcher, earcon device/volume settings)
- Track B: Audio Workshop dialog (3 tabs: TX Audio, Live Meters, Earcon Explorer), ScreenFields TX expansion (mic gain, compander, processor, TX filter, monitor), AudioChainPreset
- Track C: Leader key system (Ctrl+J, no timeout), TX filter keyboard sculpting (bracket keys), Command Finder extended with leader key commands, BUG-004 fix (FlexKnob.Dispose)
- Track D: App renamed to "JJ Flexible Radio Access" (display-name only)
- Track E: Compiled HTML Help (22 Markdown pages, hhc.exe toolchain, F1 context-sensitive HelpLauncher)
- Bug fixes: BUG-004, BUG-016 (DSP feature gating — still broken per testing), BUG-023

---

## Completed Sprints
- Sprint 22: Auto-connect, About, Command Finder categories, debounce, tune carrier, antenna, slices, startup speech, meters panel, 60m
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
- BUG-004: FIXED (Sprint 21, verified in testing)
- BUG-013: Duplicate QSO warning beep not playing
- BUG-015: F6 double-announces "Radio pane" in Logging Mode
- BUG-016: DSP feature gating — code exists but broken (Neural NR toggles on unsupported radios)
- BUG-023: FIXED (Sprint 21, confirmation dialog exists, wording needs cleanup)

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

*Updated: Mar 10, 2026 — Sprint 21+22 testing in progress. 65 findings logged. Fix sprint needed before 4.1.16 release.*
