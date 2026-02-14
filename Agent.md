# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.114 (Don's Birthday Release)
- **Current sprint:** Sprint 7 — COMPLETE, all testing done, bug fixes committed
- **Next:** Sprint 8 — Form1 WPF conversion, Sprint 9 — remaining forms to WPF

## 2) Sprint 7 Status — COMPLETE

All 5 tracks merged to main. Full test matrix completed. 8 bugs found, 5 fixed + 1 disabled, 2 deferred.

### Track A: Bug Fixes & Polish — TESTED & FIXED
- DSP toggle state inversion: fixed (local variable pattern for async FlexLib reads)
- "Coming soon" stubs invisible to SR: fixed (keep enabled, click handler speaks)
- Filter items (Narrow/Widen/Shift): fixed (same local variable pattern)
- Ctrl+Shift+L/M in Logging Mode: fixed (WPF PreviewKeyDown forwarding + ProcessCmdKey fallback)
- Logging Mode exit focus trapping: fixed (clear WPF focus -> move WinForms focus -> then hide)
- R1 "unknown" on enter: **accepted as known interop issue** — resolves with Sprint 8

### Tracks B-E: ALL TESTED & PASSED
- Track B: WPF Migration (LogPanel + StationLookup via ElementHost)
- Track C: Station Lookup Enhancements (Log Contact, distance/bearing)
- Track D: Plain-English Status (Speak Status hotkey, Status Dialog disabled)
- Track E: Configurable QSO Grid (5-100, respects operator setting)

### Bug Fixes (Sprint 7 testing round 2)
- **BUG-017** (Fixed): APF now checks CW mode before toggling
- **BUG-018** (Fixed): Silenced competing dup speech, ShowPreviousContact is primary
- **BUG-019** (Fixed): Log Contact enters silently, SR reads pre-filled call naturally
- **BUG-020** (Disabled): Status Dialog replaced with speech, Ctrl+Alt+S default hotkey
- **BUG-021** (Fixed): QSO grid count respects operator setting
- **BUG-022** (Fixed): RecentQsoRow.ToString() returns callsign
- **BUG-016** (Deferred): Logging Mode exit focus — deferred to Sprint 8
- **BUG-023** (Deferred): Connect-while-connected — deferred to future sprint

### Known Issues (shipping as-is)
- R1 "unknown" on logging mode entry — WPF-in-WinForms interop, resolves Sprint 8
- NVDA DataGrid cell double-read — fixable with custom AutomationPeer in pure WPF
- Focus-on-launch from Explorer — post-Sprint 9

## 3) Sprint 8-12 Roadmap (agreed)
- **Sprint 8:** Convert Form1 from WinForms to WPF Window (kills interop root cause)
- **Sprint 9:** Convert remaining dialog forms to WPF (in stages)
- **Sprints 10-12:** Feature work (shifted from original 8-10 plan)
- Goal: eliminate all WPF-in-WinForms interop issues permanently

## 4) Technical Foundation
- Solution: `JJFlexRadio.sln`
- Languages: VB.NET (main app) + C# (libraries)
- Framework: `net8.0-windows` (.NET 8)
- Platforms: x64 (primary), x86 (legacy)
- FlexLib v4: `FlexLib_API/`

## 5) Key Patterns Learned (Sprint 7)

### FlexLib Async Property Pattern
Property setters enqueue commands to the radio; getters return stale values until the radio responds. Always use a local variable to speak the correct state:
```vb
Dim newVal = RigControl.ToggleOffOn(RigControl.NeuralNoiseReduction)
RigControl.NeuralNoiseReduction = newVal
Speak("Neural NR " & If(newVal = on, "on", "off"))
```

### WPF-in-WinForms Focus Management
- ElementHost retains WPF Keyboard.FocusedElement even when hidden
- Must call Keyboard.ClearFocus() before hiding containers
- Move WinForms ActiveControl BEFORE hiding, not after
- wpfHost.Select() instead of wpfHost.Focus() to avoid SR announcing containers

### WPF Key Forwarding
- ElementHost doesn't propagate Ctrl+Shift+letter to WinForms ProcessCmdKey
- Fix: WPF PreviewKeyDown fires event -> LogPanel handler -> BeginInvoke -> Form1
- Fallback: LogPanel.ProcessCmdKey catches same combos

## 6) Completed Sprints

### Sprint 7: Modern Menu, Logging Polish, Bug Fixes (v4.1.114, Don's Birthday Release)
### Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System (v4.1.13)
### Sprint 5: QRZ/HamQTH Lookup & Full Log Access
### Sprint 4: Logging Mode (v4.1.12)
### Sprint 3: Classic/Modern Mode Foundation
### Sprint 2: Auto-Connect (v4.1.11)
### Sprint 1: SmartLink Saved Accounts (v4.1.10)
### .NET 8 Migration (All Phases Complete)

## 7) Build Commands

```batch
# Clean + rebuild (guaranteed fresh output - use this!)
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

## 8) Key Files

| File | Purpose |
|------|---------|
| `Form1.vb` | Main form, mode switching, keyboard dispatch, menus |
| `LogPanel.vb` | WinForms UC hosting WPF LogEntryControl via ElementHost |
| `JJFlexWpf/LogEntryControl.xaml(.cs)` | WPF log entry form |
| `JJFlexWpf/RecentQsoRow.cs` | QSO grid data class (ToString = callsign) |
| `KeyCommands.vb` | Scope-aware hotkey registry |
| `globals.vb` | UIMode enum, ActiveUIMode, LastNonLogMode |
| `docs/planning/agile/sprint7-test-matrix.md` | Sprint 7 test matrix (complete) |
| `docs/planning/vision/JJFlex-TODO.md` | Bug list and backlog |

---

*Updated: Feb 14, 2026 — Sprint 7 complete. All tracks tested, 5 bugs fixed, 2 deferred. Version 4.1.114 (Don's Birthday Release). Next: Sprint 8 WPF conversion.*
