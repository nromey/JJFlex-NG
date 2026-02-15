# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint9/track-a` (main repo), `sprint9/track-b` (jjflex-9b), `sprint9/track-c` (jjflex-9c)

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.114 (Don's Birthday Release)
- **Current sprint:** Sprint 9 — remaining dialog forms to WPF (3 parallel tracks)
- **Sprint 8:** COMPLETE — Form1 → WPF MainWindow conversion

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

### Phase 8.3 — Main Content Area ✅
- ReceivedTextBox, SentTextBox, labels, RigFieldsBox (debug hidden)
- WriteText(), WriteToTextBox(), SetTextAreasVisible() helpers
- Keyboard event handler stubs for text boxes

### Phase 8.4 — PollTimer → DispatcherTimer ✅
- DispatcherTimer (100ms) replacing WinForms Timer
- PollTimerEnabled property, UpdateStatus(), EnableDisableWindowControls()
- Combo and enable/disable control lists for poll cycle

### Phase 8.5 — Menu System (All 3 Modes) ✅
- **MenuBuilder.cs** — constructs Classic, Modern, Logging menu hierarchies
- UIMode enum, ApplyUIMode(), mode switching methods
- ShowClassicUI/ShowModernUI/ShowLoggingUI, ToggleUIMode
- EnterLoggingMode/ExitLoggingMode with visibility toggling

### Phase 8.6 — Keyboard Routing + 5-Scope Hotkeys ✅
- Expanded KeyScope from 3→5: Global, Radio, Classic, Modern, Logging
- Updated ScopeMatchesMode for 5-scope matching (Radio = both Classic+Modern)
- **WpfKeyConverter.cs** — WPF Key → WinForms Keys conversion
- MainWindow PreviewKeyDown with Ctrl+Shift+M/L meta-commands
- DoCommandHandler delegate pattern (avoids circular project reference)

### Phase 8.7 — FiltersDspControl (was Flex6300Filters) ✅
- **FiltersDspControl.xaml** — 7 GroupBox sections replacing 2,822-line WinForms control
- **FiltersDspControl.xaml.cs** — headers, ranges, collections, UpdateAllControls()
- Renamed from Flex6300Filters (6300-specific) to FiltersDspControl (all models)
- Button delegates: Narrow/Widen/Shift, TNF, ESC, Info, RX/TX EQ
- Cleanup() unhooks all rig delegates on disconnect
- Full solution builds clean

### Phase 8.8 — Logging Mode Panels (Native WPF) ✅
- **RadioPaneControl.xaml/.cs** — WPF replacement for RadioPane.vb
  - Frequency, mode, band, tune step display
  - Arrow key tuning (Up/Down), step size cycling (Left/Right), Ctrl+F manual entry
  - Delegate-based rig wiring (no direct FlexLib reference)
- LoggingPanel in MainWindow.xaml now hosts:
  - RadioPaneControl (left column, 250px default)
  - GridSplitter (replaces WinForms SplitContainer)
  - LogEntryControl (right column, fill — already pure WPF)
- EnterLoggingMode() / ExitLoggingMode() updated with focus management
- **Kills ElementHost bugs permanently:**
  - No "unknown" on entry (no intermediate container announcements)
  - No focus trapping on hide (Visibility.Collapsed removes from tab order)
  - Keyboard routing works natively (Window PreviewKeyDown)
- Full solution builds clean (0 errors)

### Phase 8.9 — Integration, Cleanup & Build ✅
- Wired DoCommandHandler in ApplicationEvents.vb (KeyCommands.DoCommand → WPF PreviewKeyDown)
- Added WpfMainWindow accessor property in globals.vb
- Clean builds both x64 and x86 Release (0 errors)
- Verified fresh exe timestamps on both platforms
- NSIS installer generation confirmed working
- **Note:** Form1.vb remains compiled but never instantiated (dead code).
  Full removal requires migrating 24 Form1 references in KeyCommands.vb to
  WpfMainWindow — deferred to Sprint 9 integration phase.

## Sprint 9 Status — IN PROGRESS

Converting all remaining WinForms dialogs to WPF. Plan: `frolicking-forging-map.md`.
3 parallel tracks using git worktrees (see CLAUDE.md Sprint Lifecycle SOP).

### Phase 9.0 — Dialog Base Infrastructure ✅ COMPLETE
- `JJFlexDialog.cs` base class + `DialogStyles.xaml` — committed `b5ad7f08`
- Base class: ESC-close, focus management, accessibility, button panel helpers
- Shared styles: DialogButton, OkButton, CancelButton, DialogLabel, etc.

### Track A — High-Priority Dialogs (11 forms) ✅ COMPLETE
- Worktree: `C:\dev\JJFlex-NG` (main repo), branch: `sprint9/track-a`
- Committed as `90a6c7a2`: all 11 dialogs + Form1 ref migration in KeyCommands.vb
- RigSelector, Welcome, PersonalInfo, Profile, AuthDialog (WebView2), RadioInfo
- AutoConnect settings/failed, SmartLinkAccount, LoginName, ProfileWorker
- Track A also added WebView2 PackageReference to JJFlexWpf.csproj

### Track B — Radio Operation Dialogs (13 forms) 🔄 RUNNING
- Worktree: `C:\dev\jjflex-9b`, branch: `sprint9/track-b`
- FlexMemories, TXControls, DefineCommands (expand to 5 scope tabs)
- LogEntry, FindLogEntry, Export, Import, scan
- EscDialog, FlexEq, FlexTNF, ComInfo, Menus
- CLI session may still be running or may need resume

### Track C — Low-Priority + Library Dialogs (30 forms) 🔄 RUNNING
- Worktree: `C:\dev\jjflex-9c`, branch: `sprint9/track-c`
- 21 root-level dialogs (About, FreqInput, ShowBands, CW macros, etc.)
- 9 library dialogs (MessageForm, ClusterForm, SetupKeys, log templates, etc.)
- CLI session may still be running or may need resume

### Phase 9.5 — Final Cleanup ⬜ (after all tracks merge)
- Delete Form1.vb, LogPanel.vb, RadioPane.vb, RadioBoxes/, StationLookup.vb
- Remove WinForms references, clean build, test matrix

### Next Steps (for resuming session)
1. Check if Track B and C CLI sessions completed or need resume
2. When both complete: merge B into A, then C into A, build-verify after each
3. Merge order doesn't matter — tracks are independent
4. After merge: Phase 9.5 cleanup, test matrix creation

## Sprint 8-12 Roadmap
- **Sprint 8:** COMPLETE — Form1 → WPF MainWindow
- **Sprint 9:** IN PROGRESS — remaining dialog forms to WPF (parallel worktrees)
- **Sprints 10-12:** Feature work (filter key packs, tuning packs, Modern mode enhancements)
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
| `JJFlexWpf/MenuBuilder.cs` | Constructs all 3 menu hierarchies |
| `JJFlexWpf/WpfKeyConverter.cs` | WPF Key → WinForms Keys conversion |
| `JJFlexWpf/Controls/FiltersDspControl.xaml(.cs)` | DSP/filter controls (replaces Flex6300Filters.cs) |
| `JJFlexWpf/Controls/RadioPaneControl.xaml(.cs)` | Radio status pane for Logging Mode (replaces RadioPane.vb) |
| `KeyCommands.vb` | Scope-aware hotkey registry (5 scopes: Global/Radio/Classic/Modern/Logging) |
| `globals.vb` | UIMode enum, ActiveUIMode, LastNonLogMode |
| `docs/planning/agile/sprint7-test-matrix.md` | Sprint 7 test matrix (complete) |
| Plan file | `frolicking-forging-map.md` — Sprint 8-9 plan |

---

*Updated: Feb 15, 2026 — Sprint 9 IN PROGRESS. Phase 9.0 ✅, Track A ✅, Tracks B & C running (may need resume). Next: check track status, merge, Phase 9.5 cleanup.*
