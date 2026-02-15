# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint9/track-a` (ready to merge to main)

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.114 (Don's Birthday Release)
- **Sprint 9:** COMPLETE — all remaining WinForms dialogs converted to WPF
- **Sprint 8:** COMPLETE — Form1 → WPF MainWindow conversion

## 2) Sprint 9 Status — COMPLETE (pending merge to main)

Converted all remaining WinForms dialogs to WPF using 3 parallel tracks with git worktrees.
All tracks merged into sprint9/track-a. Clean build verified x64 + x86 Release (0 errors).

**Stats:** 122 files changed, +10,825 lines, -6,190 lines

### Phase 9.0 — Dialog Base Infrastructure ✅
- `JJFlexDialog.cs` base class + `DialogStyles.xaml`
- Base class: ESC-close, focus management, accessibility, button panel helpers
- Shared styles: DialogButton, OkButton, CancelButton, DialogLabel, etc.

### Track A — High-Priority Dialogs (11 forms) ✅ MERGED
- RigSelector, Welcome, PersonalInfo, Profile, AuthDialog (WebView2), RadioInfo
- AutoConnect settings/failed, SmartLinkAccount, LoginName, ProfileWorker
- Form1 ref migration in KeyCommands.vb

### Track B — Radio Operation Dialogs (13 forms) ✅ MERGED
- FlexMemories, TXControls, DefineCommands (5-scope tabs)
- LogEntry, FindLogEntry, Export, Import, Scan
- EscDialog, FlexEq, FlexTNF, ComInfo, Menus

### Track C — Low-Priority + Library Dialogs (30 forms) ✅ MERGED
- 21 root-level dialogs (About, FreqInput, ShowBands, CW macros, etc.)
- 9 library dialogs (MessageForm, ClusterForm, SetupKeys, log templates, etc.)

### Phase 9.5 — Cleanup ✅ (partial)
- ✅ StationLookup.vb deleted (no live references)
- ✅ TRACK-INSTRUCTIONS.md files deleted
- ✅ Worktrees removed (jjflex-9b, jjflex-9c)
- ✅ Track branches deleted (sprint9/track-b, sprint9/track-c)
- ✅ Clean build verified x64 + x86 Release (0 errors)
- ⚠️ Form1.vb, LogPanel.vb, RadioPane.vb — CANNOT delete yet
  - globals.vb, LogEntry.vb, LOTWMerge.vb reference `Form1` type directly
  - LogPanel.vb and RadioPane.vb referenced by Form1.vb
  - Requires FlexBase.cs/globals.vb decoupling (Sprint 10)
- ⚠️ RadioBoxes/ directory — CANNOT delete yet
  - FlexBase.cs deeply coupled (Flex6300Filters, FlexMemories, FlexATUMemories)
  - Also SmartLinkAccountSelector, GetFile references
  - 28+ build errors when old dialog types are removed
  - Requires Sprint 10 decoupling work

### Known Dead Code (compiles but never runs)
These files are replaced by WPF equivalents but remain because of type dependencies:
- `Form1.vb` (3,757 lines) — replaced by `JJFlexWpf/MainWindow.xaml`
- `LogPanel.vb` (1,217 lines) — replaced by `JJFlexWpf/LogEntryControl.xaml`
- `RadioPane.vb` (264 lines) — replaced by `JJFlexWpf/Controls/RadioPaneControl.xaml`
- `RadioBoxes/` project — custom WinForms controls used by Flex6300Filters etc.
- `Radios/Flex6300Filters.cs` — replaced by `JJFlexWpf/Controls/FiltersDspControl.xaml`
- `Radios/FlexMemories.cs` — replaced by WPF FlexMemoriesWindow
- Various old dialog files in `Radios/` — all have WPF replacements in `JJFlexWpf/Dialogs/`

## 3) Sprint 10 Plan — FlexBase.cs Decoupling

**Goal:** Remove all WinForms type dependencies from FlexBase.cs and globals.vb so dead code can be deleted.

**Key dependencies to break:**
- `FlexBase.FilterObj` (Flex6300Filters) — filter UI methods: RXFreqChange, PanSetup, ZeroBeatFreq, OperatorChangeHandler
- `FlexBase.memoryHandling` (FlexMemories) — memory methods: CurrentMemoryChannel, NumberOfMemories, SelectMemory, SelectMemoryByName, MemoryNames, SortedMemories
- FlexATUMemories references (line 3124)
- SmartLinkAccountSelector references (SmartLinkAccountManager.cs line 97)
- GetFile references (FlexDB.cs lines 63, 187)
- globals.vb Form1 references (lines 448, 537, 546, 759, 768, 773)
- LogEntry.vb Form1 reference (line 248)
- LOTWMerge.vb Form1 reference (line 107)

**Approach:** Use delegate-based wiring pattern (Func/Action delegates) already established in codebase. Extract dialog dependencies into interfaces or delegates so FlexBase.cs doesn't need concrete WinForms types.

## 4) Sprint 8 Status — COMPLETE

Converting Form1 from WinForms to pure WPF Window.

### Phase 8.0-8.9 — All Complete ✅
- WPF App Bootstrap, MainWindow Shell, RadioBoxes→WPF Controls
- Main Content Area, PollTimer→DispatcherTimer, Menu System
- Keyboard Routing (5-scope), FiltersDspControl, Logging Mode Panels
- Integration, Cleanup & Build

**Stats:** 26 files changed, +3,938 lines, -41 lines

## 5) Combined Sprint 8+9 Stats
- ~148 files changed
- +14,763 lines added
- -6,231 lines removed
- Net +8,532 lines

## 6) Sprint 7 Status — COMPLETE

All 5 tracks merged to main. Full test matrix completed. 8 bugs found, 5 fixed + 1 disabled, 2 deferred.

## 7) Completed Sprints

### Sprint 9: All Dialogs to WPF (3 parallel tracks)
### Sprint 8: Form1 → WPF MainWindow
### Sprint 7: Modern Menu, Logging Polish, Bug Fixes (v4.1.114, Don's Birthday Release)
### Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System (v4.1.13)
### Sprint 5: QRZ/HamQTH Lookup & Full Log Access
### Sprint 4: Logging Mode (v4.1.12)
### Sprint 3: Classic/Modern Mode Foundation
### Sprint 2: Auto-Connect (v4.1.11)
### Sprint 1: SmartLink Saved Accounts (v4.1.10)
### .NET 8 Migration (All Phases Complete)

## 8) Technical Foundation
- Solution: `JJFlexRadio.sln`
- Languages: VB.NET (main app) + C# (libraries)
- Framework: `net8.0-windows` (.NET 8)
- Platforms: x64 (primary), x86 (legacy)
- FlexLib v4: `FlexLib_API/`

## 9) Key Patterns

### FlexLib Async Property Pattern
Property setters enqueue commands to the radio; getters return stale values until the radio responds. Always use a local variable to speak the correct state.

### Delegate-Based Rig Wiring
WPF controls use Func/Action delegates, not direct FlexLib references. This allows the Radios/ project to wire up controls without WPF projects referencing FlexLib directly.

### JJFlexDialog Base Class (Sprint 9)
All WPF dialogs inherit from JJFlexDialog — provides ESC-close, focus management, screen reader announcements, and shared styling via DialogStyles.xaml.

## 10) Build Commands

```batch
# Clean + rebuild (guaranteed fresh output - use this!)
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

## 11) Key Files

| File | Purpose |
|------|---------|
| `JJFlexWpf/MainWindow.xaml(.cs)` | WPF main window (replacing Form1.vb) |
| `JJFlexWpf/JJFlexDialog.cs` | Base class for all WPF dialogs |
| `JJFlexWpf/DialogStyles.xaml` | Shared dialog styles |
| `JJFlexWpf/Dialogs/` | All WPF dialog windows (Sprint 9) |
| `JJFlexWpf/Controls/` | WPF controls (FrequencyDisplay, FiltersDsp, RadioPane, etc.) |
| `JJFlexWpf/MenuBuilder.cs` | Constructs all 3 menu hierarchies |
| `JJFlexWpf/WpfKeyConverter.cs` | WPF Key → WinForms Keys conversion |
| `ApplicationEvents.vb` | Creates WPF MainWindow, bridges to WinForms |
| `KeyCommands.vb` | Scope-aware hotkey registry (5 scopes) |
| `globals.vb` | UIMode enum, ActiveUIMode, LastNonLogMode |
| `Radios/FlexBase.cs` | Core rig abstraction (needs decoupling in Sprint 10) |

---

*Updated: Feb 15, 2026 — Sprint 9 COMPLETE. All tracks merged. Clean build x64+x86. Pending: merge to main, then Sprint 10 (FlexBase.cs decoupling).*
