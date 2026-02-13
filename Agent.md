# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.13
- **Last sprint:** Sprint 6 - Bug Fixes, QRZ Logbook & Hotkey System (CLOSED)
- **Next sprint:** Sprint 7 — WPF Migration, Station Lookup Features, Status System, Bug Fixes

## 2) Technical Foundation
- Solution: `JJFlexRadio.sln`
- Languages: VB.NET (main app) + C# (libraries)
- Framework: `net8.0-windows` (.NET 8)
- Platforms: x64 (primary), x86 (legacy)
- FlexLib v4: `FlexLib_API/`

## 3) Sprint 7 Planning (kickoff Feb 13, 2026)

### Agreed Scope — 5 Parallel Tracks (use worktrees!)

**Track A: Bug fixes / polish** (branch: `sprint7/bug-fixes`)
- BUG-015: F6 double-announces "Radio pane" in Logging Mode (remove FreqBox Enter handler Speak)
- BUG-013: Duplicate QSO beep not audible (investigate)
- CW hotkey feedback: Ctrl+1-7 and F12 speak message when no CW messages configured
- Fix "coming soon" Modern menu stubs — fill in or improve
- Update changelog for Sprint 6
- Discuss with user: any other bugs to include?

**Track B: WPF migration** (branch: `sprint7/wpf-migration`)
- LogPanel → WPF via ElementHost
- Station Lookup → WPF via ElementHost (or standalone WPF Window)
- Must test with JAWS + NVDA
- Acceptance: AutomationProperties, 1-based grid rows, tab order, focus management

**Track C: Station Lookup enhancements** (branch: `sprint7/station-lookup`)
- "Log Contact" button: enters Logging Mode with callsign/name/QTH/grid pre-filled
- Distance and bearing calculation from operator grid to station grid
- Coordinate merge with Track B (WPF conversion) — Track B merges first

**Track D: Plain-English Status** (branch: `sprint7/status`)
- Speak Status: concise spoken summary of radio state
- Status Dialog: full accessible status display
- Needs ActiveSlice design decisions — discuss with user during planning

**Track E: Configurable Recent QSOs grid size** (branch: `sprint7/qso-grid-config`)
- Currently hardcoded at 20
- Add setting to operator profile (PersonalData)
- Apply to LogPanel grid

### Merge Order
Track A (bug fixes) → Track E (small) → Track B (WPF) → Track C (Station Lookup features, on top of WPF) → Track D (Status)

### Sprint 8 Preview (discussed, not committed)
- WPF: Command Finder + DefineCommands
- Row-0 → row-1 indexing fix (comes free with WPF DataGrid from Sprint 7)
- Slice Menu implementation (big feature)
- Configurable Recent QSOs grid size (if not done in Sprint 7)

## 4) Sprint 6 Summary (CLOSED — v4.1.13)

### Track A: Bug Fixes
- BUG-007: QRZ→HamQTH auto-fallback on login failure
- BUG-008: Built-in HamQTH for LogPanel when no personal credentials
- BUG-009: Modern menu accessibility (AccessibleName on all items)

### Track B: QRZ Logbook
- QRZ Logbook API integration (upload QSOs to QRZ)
- Validate API key, per-QSO upload on save

### Track C: Hotkey System Overhaul
- Context-aware KeyScope (Global/Radio/Logging)
- Command Finder (Ctrl+/) with search and execute
- Tabbed hotkey settings UI (Define Commands)
- ProcessCmdKey routing fix (BUG-010)
- Conflict detection with auto-clear (BUG-012)
- "Coming soon" in menu Text property (BUG-011)

### Post-Sprint Fixes (committed to main)
- BUG-014: XmlSerializer corruption of combined Keys values (integer proxy + contamination guard)
- Ctrl+Shift+M exits Logging Mode first (instead of ignoring)
- F6 handler: removed duplicate Speak, moved rv=True before routine call

### Test Matrix
- Full matrix: `docs/planning/agile/sprint6-test-matrix.md` (also archived)
- All tests PASS (D4/G3 pass with "no CW configured" caveat, G4 N/A)

## 5) Completed Sprints

### Sprint 5: QRZ/HamQTH Lookup & Full Log Access (CLOSED)
- QRZ.com + HamQTH callbook integration
- Credential management with DPAPI encryption
- Station Lookup upgrade (Ctrl+L)
- Full Log Access from Logging Mode (Ctrl+Alt+L)

### Sprint 4: Logging Mode (v4.1.12)
- Ctrl+Shift+L toggle, LogPanel quick-entry, RadioPane, F6 pane switching
- Recent QSOs grid, dup checking, auto-fill from radio

### Sprint 3: Classic/Modern Mode Foundation
- UIMode enum, Modern menu skeleton, Ctrl+Shift+M toggle

### Sprint 2: Auto-Connect (v4.1.11)
- Unified auto-connect for local and remote radios

### Sprint 1: SmartLink Saved Accounts (v4.1.10)
- PKCE auth, DPAPI tokens, account selector

### .NET 8 Migration (All Phases Complete)
- SDK-style conversion, dual x64/x86, WebView2, NSIS installers

## 6) Build Commands

```batch
# Clean + rebuild (guaranteed fresh output - use this!)
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

**WARNING:** Do not use `--no-incremental` expecting fresh output — it doesn't work. Always `dotnet clean` first. See CLAUDE.md for details.

## 7) Key Files

### Hotkey System (Sprint 6)
- `KeyCommands.vb` — Scope-aware registry, KeyDefType with integer XML proxy (BUG-014 fix)
- `CommandFinder.vb` — Ctrl+/ context-aware search dialog
- `DefineCommands.vb` — Tabbed hotkey settings UI (Global/Radio/Logging)
- `Form1.vb` — ProcessCmdKey override (routes all keys through DoCommand before MenuStrip)

### QRZ Logbook (Sprint 6)
- `QrzLookup/QrzLogbookUpload.cs` — QRZ Logbook API client

### Logging Mode (Sprint 4-5)
- `LogPanel.vb` — Quick-entry UserControl
- `RadioPane.vb` — Minimal radio status with arrow-key tuning
- `QrzLookup/QrzCallbookLookup.cs` — QRZ.com XML API client
- `CallbookResult.vb` — Unified callbook result class
- `StationLookup.vb` — Station lookup dialog (Ctrl+L)

### UI Mode System (Sprint 3)
- `globals.vb` — UIMode enum, ActiveUIMode, LastNonLogMode
- `PersonalData.vb` — Operator settings
- `Form1.vb` — BuildModernMenus, ApplyUIMode, ToggleUIMode

### Authentication (SmartLink)
- `Radios/AuthFormWebView2.cs` - WebView2-based Auth0 login
- `Radios/SmartLinkAccountManager.cs` - DPAPI-encrypted account storage

## 8) Documentation

| File | Description |
|------|-------------|
| `CLAUDE.md` | Build commands, project structure, coding patterns |
| `docs/planning/vision/JJFlex-TODO.md` | Feature backlog + bug tracker |
| `docs/CHANGELOG.md` | Version history |
| `docs/planning/` | Product vision, design proposals, sprint plans |
| `docs/planning/agile/` | Active sprint plans |
| `docs/planning/agile/archive/` | Completed sprint plans |

---

*Updated: Feb 12, 2026 — Sprint 6 CLOSED (v4.1.13). All tests pass. Sprint 7 scope agreed: 5 tracks (bug fixes, WPF migration, Station Lookup features, Plain-English Status, configurable QSO grid). Kickoff Feb 13.*
