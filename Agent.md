# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.11
- **Last completed:** Sprint 3 - Classic/Modern Mode Foundation (tested & closed Feb 6, 2026)

## 2) Technical Foundation
- Solution: `JJFlexRadio.sln`
- Languages: VB.NET (main app) + C# (libraries)
- Framework: `net8.0-windows` (.NET 8)
- Platforms: x64 (primary), x86 (legacy)
- FlexLib v4: `FlexLib_API/`

## 3) Completed Work

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

## 4) Future Sprints (planned, not implemented)

| Sprint | Key Features |
|--------|-------------|
| Tuning & Band Nav | Fine/Medium/Coarse/Step tuning, band jump, license-aware entry |
| Earcons | Tuning ticks, band edge beeps, speech throttle |
| Audio | PC Audio Boost UI, Audio Test dialog, audio routing |
| Logger | Logging Mode (3rd UI state), Jim's fast-logging, SKCC |
| Hotkeys v2 | Layered keystroke system (Global/Classic/Modern/Logging) |
| Command Finder | F12 search, leader key (Ctrl+J) |
| Waterfall | Braille waterfall, panadapter sonification |

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

*Updated: Feb 6, 2026 — Sprint 3 tested & closed. Build process fixes committed. Ready for Sprint 4.*
