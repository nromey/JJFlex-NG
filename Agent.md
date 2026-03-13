# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.15 (released 2026-03-07), pending bump to 4.1.16

## Current State — Sprint 24 Ready to Execute

**Status:** Sprint 23 + Mini Sprint 24a testing complete. All testing fixes and mini sprint work are uncommitted (~60 modified files). Sprint 24 plan written and ready to execute.

**Sprint 24 plan:** `docs/planning/skywave-negative-sweepstakes.md`
- 13 phases, 40 items — biggest sprint yet
- Theme: Key Migration (VB-to-C#) + Verbosity Engine + Slices + Audio Architecture + Status Dialog + About WebView2 + Audio Workshop fix
- Structure: Serial foundation (Phases 1-6) → Parallel tracks (Phases 7-10) → Serial finishing (Phases 11-13)
- Track A: Slices + Status Dialog (main repo)
- Track B: Audio + About + Quick Wins (worktree `../jjflex-24b`)

**Key documents:**
- Sprint 24 scope: `docs/planning/sprint24-scope.md`
- Sprint 24 plan: `docs/planning/skywave-negative-sweepstakes.md`

**Uncommitted work (~60 files):**
- Sprint 23 testing fixes: double-speak fix, Neural NR hardware gating, Radio menu Disconnect wording, status speech improvements, About dialog ListBox, Ctrl+S for S-meter
- Mini Sprint 24a (6 phases): earcon audit, dialog earcons removed, access keys on all 42 dialogs, meter hotkeys, wider filter presets, enriched status speech
- Guided testing bug fixes: Ctrl+F/Shift+F bonks, frequency readback slice letters, ATU infinite beeps on 6300, missing toggle earcons, filter edge false positive, adaptive filter step, meter tones silent

**Next steps:**
1. Commit all uncommitted testing fixes + mini sprint work
2. Begin Sprint 24 Phase 1 (Key Migration — extract types to Radios project)

### Deferred to Backlog
- FINDING-25: Multi-radio abstraction — wait for second radio platform
- FINDING-48: Earcon fade-out/envelope — add selectively later
- Connection error hang: SSL error made app unresponsive — needs deeper investigation

## Open Bugs
- BUG-013: Duplicate QSO warning beep not playing
- BUG-015: F6 double-announces "Radio pane" in Logging Mode
- BUG-049: RemoveSlice not updating MyNumSlices (Sprint 24 Phase 7A)

## Mini Sprint 24a — COMPLETE (Tested, Uncommitted)

All 6 phases coded and building clean:
- Phase A1: Earcon audit (8 toggles fixed)
- Phase A2: Dialog earcons removed
- Phase A3: Access keys for all 42 dialogs
- Phase A4: Meter hotkeys (Ctrl+Alt+P, Ctrl+Alt+V)
- Phase A5: Wider filter presets (SSB to 10k, CW to 4k)
- Phase A6: Ctrl+Shift+S status audit (tuning mode, filter preset, meter state)

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

*Updated: Mar 12, 2026 — Sprint 24 plan written (`skywave-negative-sweepstakes.md`). Archived sprint 21-23 plan files. ~60 files uncommitted (testing fixes + mini sprint 24a). Ready to commit and begin Sprint 24.*
