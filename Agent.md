# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.13
- **Current sprint:** Sprint 7 — IN TESTING (all 5 tracks merged to main)
- **Next:** Sprint 8 — Form1 WPF conversion, Sprint 9 — remaining forms to WPF

## 2) Sprint 7 Status — TESTING IN PROGRESS

All 5 tracks are merged to main. Track A bug fixes committed after testing.

### Track A: Bug Fixes & Polish — TESTED, fixes committed
- DSP toggle state inversion: fixed (local variable pattern for async FlexLib reads)
- "Coming soon" stubs invisible to SR: fixed (keep enabled, click handler speaks)
- Filter items (Narrow/Widen/Shift): fixed (same local variable pattern)
- Ctrl+Shift+L/M in Logging Mode: fixed (WPF PreviewKeyDown forwarding + ProcessCmdKey fallback)
- Logging Mode exit focus trapping: fixed (clear WPF focus → move WinForms focus → then hide)
- R1 "unknown" on enter: **accepted as known interop issue** — WPF-in-WinForms artifact, resolves with Sprint 8

### Track B: WPF Migration — UNTESTED
- LogPanel → WPF via ElementHost
- StationLookup → WPF via ElementHost

### Track C: Station Lookup Enhancements — UNTESTED
- "Log Contact" button: enters Logging Mode with fields pre-filled
- Distance/bearing calculation from operator grid

### Track D: Plain-English Status — UNTESTED
- Speak Status hotkey
- Status Dialog

### Track E: Configurable QSO Grid — UNTESTED
- Adjustable Recent QSOs grid size (5-100, default 20)

### Test Matrix
- `docs/planning/agile/sprint7-test-matrix.md` — active, partially filled in
- Modern menu items (M-R1 through M-T7) also untested

## 3) Sprint 8-9 Plan (agreed, not started)
- **Sprint 8:** Convert Form1 from WinForms to WPF Window (kills interop root cause)
- **Sprint 9:** Convert remaining dialog forms to WPF (in stages)
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
- Fix: WPF PreviewKeyDown fires event → LogPanel handler → BeginInvoke → Form1
- Fallback: LogPanel.ProcessCmdKey catches same combos

## 6) Completed Sprints

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
| `KeyCommands.vb` | Scope-aware hotkey registry |
| `globals.vb` | UIMode enum, ActiveUIMode, LastNonLogMode |
| `docs/planning/agile/sprint7-test-matrix.md` | Active test matrix |
| `docs/planning/agile/Sprint-07-pileup-ragchew-barefoot.md` | Sprint 7 plan |

---

*Updated: Feb 14, 2026 — Sprint 7 all tracks merged, Track A tested and bug-fixed. Tracks B-E and modern menu items awaiting testing. Sprint 8-9 WPF migration agreed.*
