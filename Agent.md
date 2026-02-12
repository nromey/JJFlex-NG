# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.12 (no version bump for Sprint 5 — accumulating features)
- **Last sprint:** Sprint 5 - QRZ/HamQTH Lookup & Full Log Access (CLOSED, all phases + testing + fixes complete)

## 2) Technical Foundation
- Solution: `JJFlexRadio.sln`
- Languages: VB.NET (main app) + C# (libraries)
- Framework: `net8.0-windows` (.NET 8)
- Platforms: x64 (primary), x86 (legacy)
- FlexLib v4: `FlexLib_API/`

## 3) Completed Work

### Sprint 4: Logging Mode (Complete — v4.1.12)
- **Phase 1: Menu & Mode Switching** (commit `142dff9b`)
  - Ctrl+Shift+L toggles Logging Mode on/off from any base mode
  - Logging Mode menu bar: Log, Navigate, Mode, Help
  - Classic/Modern menus no longer show log-related items (except "Enter Logging Mode")
  - Log menu items moved exclusively to Logging Mode
  - `LastNonLogMode` tracks base mode for round-trip
  - Screen reader announces mode transitions

- **Phase 2: Quick-Entry LogPanel + RadioPane** (commit `c6cabe8b`)
  - `LogPanel.vb` — Quick-entry UserControl with fields: Call, RST Sent/Rcvd, Name, QTH, State, Grid, Comments
  - `RadioPane.vb` — Minimal radio status pane with tabbable read-only TextBoxes (Freq, Mode, Band, Tune Step)
  - SplitContainer layout: RadioPane (left, 200px) + LogPanel (right)
  - F6/Shift+F6 switches focus between panes (standard Windows convention)
  - RadioPane arrow-key tuning: Up/Down = 1 step, Shift+Up/Down = 10 steps, Left/Right = change step size
  - Ctrl+F in RadioPane opens FreqInput for manual frequency entry
  - Dup checking on CallSign leave with screen reader + beep alert
  - State preservation: field values survive mode switches (SaveState/RestoreState)
  - Auto-fill from radio: freq, mode, band, UTC date/time
  - Tab order: Call(0) → RST Sent(1) → RST Rcvd(2) → Name(3) → QTH(4) → State(5) → Grid(6) → Comments(7)

- **Phase 3: Recent QSOs Grid + Previous Contact Lookup** (commit `0dfd82e0`)
  - DataGridView with UIA support (JAWS/NVDA arrow-key navigation)
  - 7 columns: Time UTC, Call, Mode, Freq, RST Sent, RST Rcvd, Name
  - Forward-scan loads last 20 QSOs into grid, builds call sign index as side-effect
  - `PreviousContactInfo` class: Count, LastDate, LastBand, LastMode, LastName, LastQTH
  - CallSign tab-out triggers previous contact lookup — announces count, last date/band/mode via screen reader
  - Auto-fills Name and QTH from previous contacts (zero extra file I/O)
  - `PreviousContactBox` — tabbable read-only TextBox (TabIndex=8) for re-reading info
  - Grid auto-updates when new QSO is logged

- **Phase 4: Cleanup & Polish** (commit `0dfd82e0`)
  - SKCC WES form removed from log type registry (files kept for reference)
  - Mode switcher audit: all 7 methods verified correct
  - Screen reader audit: all AccessibleName/AccessibleRole properties verified
  - Version bump to 4.1.12

- **Design doc:** `docs/planning/context-aware-hotkeys.md`
  - KeyScope enum: Global, Radio (Classic+Modern), Logging
  - Same physical key can have different commands per scope
  - Frees 7 Alt+letter keys for radio use by scoping logging commands
  - CW messages → Ctrl+1..9 (Global), freeing F5-F11

### Sprint 3: Classic/Modern Mode Foundation
- **UIMode enum** (Classic=0, Modern=1, Logging=2) in `globals.vb`
- **Persistence:** `UIModeSetting` + `UIModeDismissed` in PersonalData.personal_v1, XML-serialized per operator
- **New operators default to Modern** (PersonalInfo.vb)
- **One-time upgrade prompt** for existing operators on first launch after upgrade
- **Modern menu skeleton** built programmatically in `BuildModernMenus()`:
  - Radio, Slice, Filter, Audio, Tools menus (with submenus for DSP, Antenna, FM, etc.)
  - Items with existing handlers delegate to them; stub items announce "coming soon" via screen reader
- **Mode toggle:** Ctrl+Shift+M global hotkey + menu items in both Classic and Modern
- **ApplyUIMode()** called from Form1_Load, operatorChanged, toggle handler
- **Accessibility:** All Modern menu items have AccessibleName/AccessibleRole; AttachMenuAccessibilityHandlers applied
- **ProcessCmdKey override** for Ctrl+Shift+M (global, not routed through KeyCommands)

### Code Quality: Analyzer Setup + Dead Code Cleanup
- **Roslyn analyzers:** SonarAnalyzer (VB+C#), Roslynator, Meziantou, BannedApiAnalyzers
- **Directory.Build.props:** Solution-wide config with vendor code exclusions
- **.editorconfig:** Tiered severity (warning/suggestion/none), 1,130 baseline warnings
- **BannedSymbols.txt:** Bans BinaryFormatter, MD5, SHA1, DES, WebClient, etc.
- **Dead code cleanup:** Removed JJRadio, old Radios, rigSelector, orphaned tests, crash dumps, LibSources

### Sprint 2: Auto-Connect (v4.1.11)
- Unified auto-connect for local and remote radios
- Friendly "radio offline" dialog with options
- Auto-connect settings dialog with confirmation
- Full screen reader announcements

### Sprint 1: SmartLink Saved Accounts (v4.1.10)
- PKCE authentication flow
- DPAPI-encrypted token storage
- Account selector dialog
- Automatic token refresh

### .NET 8 Migration (All Phases Complete)
- SDK-style conversion, `net8.0-windows`, dual x64/x86
- WebView2 for Auth0, native DLL loading, NSIS installers

### Advanced DSP Features
- Neural NR (RNN), Spectral NR (NRS), Legacy NR, FFT Auto-Notch, Legacy Auto-Notch

### Sprint 5: QRZ/HamQTH Lookup & Full Log Access (CLOSED — no version bump)
- **Phase 1:** Removed 11 Classic/Modern log entry hotkeys (logging belongs in Logging Mode)
- **Phase 2:** Created QRZ lookup library (`QrzLookup/QrzCallbookLookup.cs`) + unified `CallbookResult.vb`
- **Phase 3:** Callbook credentials UI in PersonalData/PersonalInfo (source dropdown, username, password)
- **Phase 4:** Wired callbook lookup into LogPanel (async trigger on CallSign leave, fills empty fields)
- **Phase 5:** Full Log Access from Logging Mode (Ctrl+Alt+L opens JJ's LogEntry form as modal)
- **Phase 6:** Parking lot — Reset Confirmations menu item, BUG-001 fix (disabled Modern stubs)
- **Phase 7:** Sprint 4 archived, changelog updated (version bump deferred)
- **Post-sprint:** Credential validation on save, DPAPI encryption, Station Lookup upgrade (QRZ/HamQTH + DX detection)
- **Testing:** Full 60+ test matrix executed by operator (JAWS), most passed
- **Test fixes applied:**
  - Natural SR announcements: speak actual values ("Bob, San Francisco, California") not field names
  - State included in both LogPanel and Station Lookup announcements
  - Station Lookup scoped to Classic/Modern only (removed from Logging Mode — it's a radio utility)
  - Station Lookup added to Modern Tools menu
  - "Returned to logging mode" SR announcement after closing Full Log Form
  - BUG-005 fix: UTC timestamp now refreshes per QSO (was stuck at first entry's time)
  - BUG-006 fix: Callbook announcement queues after field announcements (no interruption)
- **Bugs filed for Sprint 6:** BUG-007 (QRZ→HamQTH auto-fallback), BUG-008 (built-in HamQTH for LogPanel), BUG-009 (connected-radio retests)
- **Sprint plan:** `docs/planning/agile/archive/Sprint-05-QRZ-Lookup.md`
- **Key new files:** `QrzLookup/QrzCallbookLookup.cs`, `CallbookResult.vb`, `StationLookup.vb` (upgraded)

## 4) Future Sprints (planned, not implemented)

| Sprint | Key Features |
|--------|-------------|
| Tuning & Band Nav | Fine/Medium/Coarse/Step tuning, band jump, license-aware entry |
| Earcons | Tuning ticks, band edge beeps, speech throttle |
| Audio | PC Audio Boost UI, Audio Test dialog, audio routing |
| Hotkeys v2 | Layered keystroke system — design doc at `docs/planning/context-aware-hotkeys.md` |
| Command Finder | F12 search, leader key (Ctrl+J) |
| Waterfall | Braille waterfall, panadapter sonification |
| Logger Customization | Customizable LogPanel fields, contest designer review |
| Operator Profile | QRZ self-lookup, license class selection, band plan enforcement |

See `docs/planning/` for detailed design docs.

## 5) Build Commands

```batch
# Clean + rebuild (guaranteed fresh output - use this!)
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

**WARNING:** Do not use `--no-incremental` expecting fresh output — it doesn't work. Always `dotnet clean` first. See CLAUDE.md for details.

## 6) Key Files

### Sprint 5: QRZ/HamQTH Lookup
- `QrzLookup/QrzCallbookLookup.cs` — QRZ.com XML API client (session auth, cache, background thread)
- `CallbookResult.vb` — Unified callbook result class (source-agnostic)
- `LogPanel.vb` — Callbook integration (InitializeCallbook, TriggerCallbookLookup, ApplyCallbookResult)
- `PersonalData.vb` — CallbookLookupSource, CallbookUsername, CallbookPassword fields
- `PersonalInfo.vb` / `.Designer.vb` — Callbook settings UI (source dropdown, credentials)
- `Form1.vb` — OpenFullLogEntry (Ctrl+Alt+L), Reset Confirmations, BUG-001 fix

### Sprint 4: Logging Mode
- `LogPanel.vb` — Quick-entry UserControl, thin wrapper over LogSession
- `RadioPane.vb` — Minimal radio status with arrow-key tuning
- `Form1.vb` — BuildLoggingPanels, ShowLoggingUI, InitializeLoggingSession, F6 pane switching
- `docs/planning/agile/archive/Sprint-04-Logging-Mode.md` — Sprint plan (archived)
- `docs/planning/context-aware-hotkeys.md` — Hotkey scoping design

### UI Mode System (Sprint 3)
- `globals.vb` — UIMode enum, ActiveUIMode property, LastNonLogMode
- `PersonalData.vb` — UIModeSetting, UIModeDismissed, CurrentUIMode on personal_v1
- `PersonalInfo.vb` — New operator defaults (Modern mode)
- `Form1.vb` — BuildModernMenus, ApplyUIMode, ToggleUIMode, ProcessCmdKey, CheckUIModUpgradePrompt

### Authentication (SmartLink)
- `Radios/AuthFormWebView2.cs` - WebView2-based Auth0 login
- `Radios/FlexBase.cs` - `setupRemote()` method
- `Radios/SmartLinkAccountManager.cs` - DPAPI-encrypted account storage

### DSP/Filters
- `Radios/Flex6300Filters.cs` - DSP controls UI
- `Radios/FlexBase.cs` - Radio abstraction layer

### Security
- `FlexLib_API/FlexLib/SslClientTls12.cs` - TLS 1.2+ wrapper

## 7) GitHub Actions

- **CI:** `.github/workflows/windows-build.yml` - Build on push/PR
- **Release:** `.github/workflows/release.yml` - Tag-triggered release

## 8) Documentation

| File | Description |
|------|-------------|
| `CLAUDE.md` | Build commands, project structure, coding patterns |
| `docs/TODO.md` | Feature backlog |
| `docs/CHANGELOG.md` | Version history |
| `docs/planning/` | Product vision, design proposals, sprint plans |

---

*Updated: Feb 11, 2026 — Sprint 5 CLOSED. All phases complete, full test matrix executed (60+ tests), test-driven fixes applied. Bugs 005-006 fixed, bugs 007-009 filed for Sprint 6. No version bump yet — accumulating features for next release.*
