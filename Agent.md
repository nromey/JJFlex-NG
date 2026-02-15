# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.114 (Don's Birthday Release)
- **Current sprint:** Sprint 8 — Form1 WPF conversion (IN PROGRESS)
- **Next:** Sprint 9 — remaining dialog forms to WPF

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

## 3) Sprint 8 Status — IN PROGRESS

Converting Form1 from WinForms to pure WPF Window. Plan: `frolicking-forging-map.md`.

### Phase 8.0 — WPF App Bootstrap ✅
- Created MainWindow.xaml/.cs in JJFlexWpf
- Bridge form pattern: hidden WinForms form keeps My.* namespace working
- ApplicationEvents.vb creates WPF MainWindow in Startup handler

### Phase 8.1 — MainWindow Shell + Layout ✅
- Full DockPanel layout: Menu, StatusBar, Grid (RadioControls/Content/Logging rows)
- Init skeleton matching Form1_Load order, screen reader greeting
- Closing handler with trace logging

### Phase 8.2 — RadioBoxes → WPF Controls ✅
- **FrequencyDisplay** — replacement for RadioBoxes.MainBox (multi-field display)
- **RadioComboBox** — replacement for RadioBoxes.Combo (ModeControl, TXTuneControl)
- **RadioInfoBox** — replacement for RadioBoxes.InfoBox (read-only/editable text)
- **RadioNumberBox** — replacement for RadioBoxes.NumberBox (integer with arrow keys)
- **RadioCheckBox** — replacement for RadioBoxes.ChekBox (enum flags display)
- **AntennaTuneButton** — WPF Button with tuner status (in MainWindow.xaml)
- **TransmitButton** — WPF Button with PTT toggle (in MainWindow.xaml)
- All controls wired into MainWindow.xaml RadioControlsPanel
- StatusBar expanded with Power, Memory, Scan, LogFile items
- Full solution builds clean (Debug x64)

### Phase 8.3 — Main Content Area (NEXT)
### Phase 8.4 — PollTimer → DispatcherTimer
### Phase 8.5 — Menu System (All 3 Modes)
### Phase 8.6 — Keyboard Routing + 5-Scope Hotkeys
### Phase 8.7 — Flex6300Filters → WPF UserControl
### Phase 8.8 — Logging Mode Panels (Native WPF)
### Phase 8.9 — Integration, Cleanup & Build

## Sprint 8-12 Roadmap
- **Sprint 8:** Convert Form1 from WinForms to WPF Window (kills interop root cause)
- **Sprint 9:** Convert remaining dialog forms to WPF (parallel worktrees)
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
| `JJFlexWpf/MainWindow.xaml(.cs)` | WPF main window (replacing Form1.vb) |
| `JJFlexWpf/Controls/FrequencyDisplay.xaml(.cs)` | Multi-field frequency/status display |
| `JJFlexWpf/Controls/RadioComboBox.xaml(.cs)` | Header + ComboBox (Mode, TXTune) |
| `JJFlexWpf/Controls/RadioInfoBox.xaml(.cs)` | Header + TextBox (rig info fields) |
| `JJFlexWpf/Controls/RadioNumberBox.xaml(.cs)` | Header + numeric TextBox (arrow keys) |
| `JJFlexWpf/Controls/RadioCheckBox.xaml(.cs)` | Enum flags display (expand/collapse) |
| `ApplicationEvents.vb` | Creates WPF MainWindow, bridges to WinForms |
| `My Project/Application.Designer.vb` | Hidden bridge form for My.* compatibility |
| `Form1.vb` | Original WinForms main form (being replaced) |
| `LogPanel.vb` | WinForms UC hosting WPF LogEntryControl via ElementHost |
| `JJFlexWpf/LogEntryControl.xaml(.cs)` | WPF log entry form |
| `KeyCommands.vb` | Scope-aware hotkey registry (Phase 8.6: expand to 5 scopes) |
| `globals.vb` | UIMode enum, ActiveUIMode, LastNonLogMode |
| `docs/planning/agile/sprint7-test-matrix.md` | Sprint 7 test matrix (complete) |
| Plan file | `frolicking-forging-map.md` — Sprint 8-9 plan |

---

*Updated: Feb 14, 2026 — Sprint 8 Phase 8.2 complete. All RadioBoxes WPF controls created and building. Phases 8.0-8.2 done, 8.3 next.*
