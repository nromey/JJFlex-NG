# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint24/track-a`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.15 (released 2026-03-07), version bump to 4.1.16 deferred to after Sprint 25

## Current State — Sprint 24 Complete, Pending Test + Merge

**Status:** All 13 phases coded, committed, and building clean on all four configurations (x64/x86 Debug/Release). Branch `sprint24/track-a` needs merge to main after testing.

**Sprint 24 plan:** `docs/planning/skywave-negative-sweepstakes.md`
**Test matrix:** `docs/planning/agile/sprint24-test-matrix.md`

### Sprint 24 Commit History (sprint24/track-a)
- Phase 1: Extract shared key types to Radios/KeyCommandTypes.cs
- Phase 2: KeyCommands.cs skeleton + KeyCommandContext
- Phase 3: Port all 126 handler methods to C# KeyCommands
- Phase 4: Wire up C# KeyCommands and cut over from VB
- Phase 5: Conflict audit and scope cleanup
- Phase 6a: Verbosity engine core + hotkeys
- Phase 6b: Tag all Speak() calls with verbosity levels
- Phase 7A: Fix slice count tracking and menu rebuild
- Phase 7B: Dual-channel audio architecture
- Phase 8A: Slice selector, operations, freq improvements
- Phase 8B: Merge Audio + Meter Tones tabs into unified Audio tab
- Phase 9A: Accessible Status Dialog with live refresh
- Phase 9B: About Dialog WebView2 upgrade
- Phase 10A: Fix VFO index drift after slice removal + trace logging
- Phase 10B: DSP level minimums and access key announcements
- Phase 11: 60m mode advisory on band jump
- Phase 12: Fix Audio Workshop Tab navigation
- Phase 13: Fix 32 build errors from key migration + x86 build, library version bumps

### Key Findings During Phase 13
- VB project was SKIPPED on x64 solution builds due to missing `Build.0` lines in JJFlexRadio.sln — Sprint 24 Phases 1-4 (key migration) were never build-verified against the VB project
- 32 VB compilation errors in globals.vb and ApplicationEvents.vb from Phase 4's delegate wirings referencing methods that were deleted during the migration
- Radios.csproj had RuntimeIdentifier that broke clean x86 builds (NETSDK1047)
- DX Cluster (ShowArCluster) is a placeholder — cluster UI needs reimplementation after key migration

### Smoke Test (2026-03-13 evening)
- **Tolk DLL fix**: Speech was completely broken — Tolk.dll and nvdaControllerClient64.dll missing from output. Radios.csproj conditioned DLL copy on Platform==x64 but solution maps Radios to AnyCPU. Fixed by adding AnyCPU to condition.
- **Key migration verified**: Mute (M on slice field), verbosity cycling (Ctrl+Shift+V), speak status (Ctrl+Shift+S) all working after Tolk fix
- **SmartLink connects OK** when Don's radio is on
- **Findings for Sprint 25 fixes**:
  - Slice Operations field accessible name says "audio 60" instead of "Slice A Operations"
  - Access key announcements missing in Rig Selector (only Cancel announces Alt+C)
  - SmartLink zero-radios loops back to Auth0 instead of saying "No remote radios available" (pre-existing)

### Library Versions Bumped
- JJFlexWpf: 2.1.0 → 2.2.0 (minor: key migration, verbosity engine, status dialog, audio workshop)
- Radios: 3.2.6 → 3.2.7 (patch: KeyCommandTypes, SixtyMeterChannels, slice fix)

**Next steps:**
1. Continue directed testing of Sprint 24 using test matrix
2. Merge sprint24/track-a to main
3. Begin Sprint 25 — scope defined:
   - Fix sprint items (slice ops accessible name, access key announcements, SmartLink zero-radio UX, Tolk missing DLL error reporting)
   - Easter eggs: "autopatch" → DTMF tones (Patric's repeater welcome sound), "qrm" → PS/2 mechanical keyboard sounds
   - Typing sound options: default beep on/off; if easter egg unlocked, selector expands to beep / mechanical / touch tone / off
   - Standard braille display verbosity design (NOT Dot Pad — save for later sprint)
   - NR provider architecture research (RNNoise, OpenVoiceSharp, RM Noise)
   - Action toolbar (Ctrl+Tab, Tune/ATU/Transmit buttons)
4. Bump version to 4.1.16 and release after Sprint 25

### Deferred to Backlog
- DX Cluster reimplementation (ShowArCluster placeholder — needs event handlers, ClusterForm UI)
- FINDING-25: Multi-radio abstraction — wait for second radio platform
- FINDING-48: Earcon fade-out/envelope — add selectively later
- Connection error hang: SSL error made app unresponsive — needs deeper investigation

## Open Bugs
- BUG-013: Duplicate QSO warning beep not playing
- BUG-015: F6 double-announces "Radio pane" in Logging Mode

## Completed Sprints
- Sprint 24: Key Migration + Verbosity + Slices + Audio + Status Dialog + About + Audio Workshop
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

*Updated: Mar 13, 2026 — Sprint 24 all 13 phases complete on branch sprint24/track-a. All four build configs clean (0 errors). Test matrix created. Ready for testing and merge to main.*
