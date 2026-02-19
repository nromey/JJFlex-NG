# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.115
- **Sprint 12:** IN PROGRESS — Stabilization phase. Core features implemented, working through remaining fixes.

## 2) Current Architecture

### WPF Hosting (ElementHost pattern)
```
ShellForm (WinForms Form, visible, HWND owner)
  ├─ Native Win32 HMENU menu bar (non-client area, via P/Invoke)
  └─ ElementHost (Dock=Fill)
       └─ MainWindow (WPF UserControl)
            ├─ RadioControls (FreqOut, Mode, TXTune, buttons)
            ├─ PanadapterPanel (braille display)
            ├─ ContentArea (Received/Sent text)
            ├─ LoggingPanel (collapsed by default)
            └─ StatusBar
```

**Key files for this architecture:**
| File | Role |
|------|------|
| `BridgeForm.vb` | ShellForm — WinForms Form with native HMENU + ElementHost, hosts WPF content |
| `JJFlexWpf/NativeMenuBar.cs` | Win32 HMENU menu bar via P/Invoke (replaced MenuStripBuilder) |
| `JJFlexWpf/MainWindow.xaml` | WPF UserControl (was Window before migration) |
| `JJFlexWpf/MainWindow.xaml.cs` | Code-behind with radio wiring, callbacks, UI modes |
| `JJFlexWpf/FreqOutHandlers.cs` | Field handler methods for FreqOut interactive tuning |
| `ApplicationEvents.vb` | Creates ShellForm in Startup, wires callbacks |
| `My Project/Application.Designer.vb` | Sets TheShellForm as MainForm |
| `globals.vb` | AppShellForm accessor, title setting, initialization |

### Startup Sequence
1. `MyApplication_Startup` → NativeLoader, TLS, crash handlers, ScreenReaderOutput.Initialize()
2. `TheShellForm = New ShellForm()` → creates ElementHost + MainWindow, creates NativeMenuBar
3. `OnHandleCreated` → `NativeMenuBar.AttachTo(Handle)` sets native HMENU via SetMenu
4. Wire callbacks (ScanTimer, Exit, SelectRadio, CloseShell, DoCommand, FreqOutHandlersWire)
5. `InitializeApplication()` → config, operators, radio open → calls `ApplyUIMode` which triggers `MenuModeCallback` → `NativeMenuBar.ApplyUIMode(mode)` rebuilds menu bar
6. `OnCreateMainForm()` → `Me.MainForm = TheShellForm` (reuses instance from step 2)
7. VB.NET shows ShellForm → `ShellForm.OnShown()` → `SpeakWelcome()`

## 3) Sprint 12 Status — Pileup-Ragchew-Shortpath

**Plan file:** `docs/planning/agile/pileup-ragchew-shortpath.md`

### Completed Work
- **Native Win32 HMENU menus** — replaced WPF Menu → WinForms MenuStrip → native HMENU via P/Invoke
- **FreqOut interactive tuning** — field handlers ported, accessibility fixes applied
- **Panadapter braille** — PanadapterPanel with Tolk.Braille() forwarding
- **SmartLink auth fixes** — expired JWT detection, ghost PKCE handler neutered, background thread for Remote button
- **RigSelector accessibility** — ListBox arrow key speech, Remote button feedback, ShowAccountSelector wired
- **Menu wiring** — Connect to Radio, Disconnect, Select Rig, focus return on Escape
- **Build noise suppression** — MSB3277/NU1903 warnings suppressed in Directory.Build.props

### Remaining Work (Sprint 12 Leftovers)

**Phase 1: Diagnostics — DONE**
- [x] Add thread IDs to trace lines (JJTrace/Tracing.cs) — diagnosed selectorThread as root cause of sluggish tabbing
- [x] RigSelector runs on T1 now (removed selectorThread) — tabbing is snappy
- [x] Removed Speak() overrides from RadiosBox — let NVDA read standard ListBox natively
- [x] Auto-select first radio in list so NVDA has something to read
- [x] Station name timeout bumped 30s → 45s (WAN re-add can take 30-40s)

**Phase 2: Fixes**
- [ ] VFO/Frequency display navigation — Right arrow reads "slice 0 slice 1" (both slices at once), "switch" instead of "Split" label, "unable to change" error
- [ ] "Station name not set" prompt blocks on connect — user has to press Enter to proceed
- [ ] NVDA menu announcement — says "Radio alt+r" not "Radio menu alt+r" (may need MSAA/UIA investigation)
- [ ] Operations menu still a stub — needs wiring to actual functions
- [ ] Screen reader speech polish — ensure every action gives clear, non-redundant feedback

**Phase 3: Testing**
- [ ] Full functional testing — menus, logging, FreqOut, panadapter braille, SmartLink connect
- [ ] Build test matrix at `docs/planning/agile/sprint12-test-matrix.md`
- [ ] Test with both JAWS and NVDA

**Sprint Cleanup (before release)**
- [ ] Remove thread ID tracing from JJTrace/Tracing.cs — diagnostic only, adds overhead to every trace line

### Deferred Issues (noted, not Sprint 12)
- Menu item speech interrupted by NVDA focus announcement — `SpeakAfterMenuClose` fix applied, needs re-test

## 4) Roadmap

| Sprint | Focus | Status |
|--------|-------|--------|
| **12** | Stabilize WPF: menus, SmartLink, FreqOut, speech polish | **IN PROGRESS** |
| **13** | Slice Menu + Filter Menu + QSO Grid filtering/paging | Planned |
| **14** | Waterfall braille + sonification — make the band visible and audible | Planned — **GATE: no new release until waterfall flows** |
| **15** | QRZ per-QSO upload, confirmation, data import | Planned |
| Future | FreeDV2/RADEV2, Activity Engine | Backlog |

**Full backlog:** `docs/planning/vision/JJFlex-TODO.md`
**Brainstorming:** `docs/planning/future items from chatgpt brainstorming session.md`

## 5) Key Patterns

### Native Win32 HMENU Menus (CURRENT)
WPF Menu and WinForms MenuStrip both announce "collapsed/expanded" to screen readers. Only native Win32 HMENU menus (via P/Invoke `CreateMenu`/`SetMenu`) give clean screen reader navigation. `NativeMenuBar.cs` handles this. Menu bar is rebuilt on each mode switch. Windows handles Alt/F10 activation natively via `DefWindowProc`.

### ElementHost Architecture (CRITICAL)
WPF content MUST be hosted via ElementHost in a WinForms ShellForm. Standalone WPF Windows shown from VB.NET My.Application get NO keyboard input. Six fix attempts confirmed this.

### Delegate-Based Rig Wiring
WPF controls use Func/Action delegates, not direct FlexLib references. FlexBase wires up controls at radio-open time.

### RadioComboBox _userEntry Pattern
`_userEntry` flag distinguishes user actions from programmatic updates in `SelectionChanged`. Always reset `_userEntry = true` after programmatic updates complete.

### FreqOut Field Navigation
Left/Right jumps between fields with screen reader announcements. Up/Down/Space/letter keys handled by per-field handlers in `FreqOutHandlers.cs`.

### SmartLink Auth Flow
- `TryAutoConnectRemote()` — startup path, checks `isJwtExpired` before sending to server, works correctly
- `setupRemote()` — Remote button path, now also checks JWT expiry first (fixed Feb 2026)
- `WanApplicationRegistrationInvalidHandler` — neutered (just logs), no longer spawns ghost PKCE logins
- `RemoteButton_Click` — runs `RemoteRadios()` on background STA thread, UI updates via `BeginInvoke`

## 6) Completed Sprints
- Sprint 11: WPF adapters, Form1 kill, dead code deletion (~13,000 lines)
- Sprint 10: FlexBase.cs decoupling (interfaces + delegates)
- Sprint 9: All dialogs to WPF (3 parallel tracks, 122 files)
- Sprint 8: Form1 → WPF MainWindow
- Sprint 7: Modern Menu, Logging Polish (v4.1.114)
- Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System (v4.1.13)
- Sprint 5: QRZ/HamQTH Lookup & Full Log Access
- Sprint 4: Logging Mode (v4.1.12)
- Sprint 3: Classic/Modern Mode Foundation
- Sprint 2: Auto-Connect (v4.1.11)
- Sprint 1: SmartLink Saved Accounts (v4.1.10)

## 7) Build Commands

```batch
# Debug build for testing (no installer, faster)
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal

# Clean + rebuild Release (triggers NSIS installer)
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet restore JJFlexRadio.sln -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers (release only)
build-installers.bat
```

**Note:** After `dotnet clean`, always run `dotnet restore` (on the **solution**, not just the project) before `dotnet build` to avoid NETSDK1047 errors. For testing, use Debug config to skip NSIS installer.

---

*Updated: Feb 18, 2026 — SmartLink auth fixes committed. Sprint 12 leftovers documented. Roadmap: Sprint 13 = Slice Menu + QSO Grid, Sprint 14 = QRZ integration.*
