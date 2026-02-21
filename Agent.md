# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.115 (target 4.1.116)
- **Sprint 12:** COMPLETE — Stabilization phase. Plan archived.
- **Sprint 13:** TESTING — All 4 phases implemented. Mid-test bug fixes applied during testing session.
  - 13A DONE: Tab chain (FreqOut→Waterfall→Received→Sent), welcome mode announcement
  - 13C DONE: ScreenFields & Operations menus wired with DSP/audio/ATU/receiver controls
  - 13B DONE: Modern mode simplified FreqOut, coarse/fine tuning, filter hotkeys
  - 13D DONE: Modern menus (Slice/Filter/Audio) wired via shared handlers from 13C
  - Testing fixes: double tab stop, mode persistence, startup hang, boundary announcement

## NEXT TASK — SmartLink Station Name Timeout (fix before Sprint 14)

**Bug:** On SmartLink connect, `raiseNoSliceError("Station name not set")` fires when
the GUIClient remove/re-add cycle takes longer than 45s. Shows error dialog, closes radio.
User must manually reconnect. Happens routinely — not a real error, just a timing race.

**Root cause:** `FlexBase.cs` line 478 — 45s polling loop for station name. On timeout,
calls `raiseNoSliceError` → `NoSliceErrorHandler` in MainWindow.xaml.cs → error dialog →
`CloseRadioCallback`. The station name typically arrives shortly after (trace shows it
at line 262, after the error at line 225).

**Proposed fix:** Silent auto-retry instead of hard error. When station name times out,
wait a short grace period and retry once before showing any dialog. If retry also fails,
then show error. This matches user expectation — they see the radio connect successfully
on manual retry anyway.

**Key files:**
- `Radios/FlexBase.cs` lines 472-513 — station name wait loop
- `JJFlexWpf/MainWindow.xaml.cs` lines 1036-1052 — NoSliceErrorHandler
- `globals.vb` — openTheRadio / CloseTheRadio for retry logic

**Sprint 14 plan items (discuss after SmartLink fix):**
- Screen fields grouped panel (Classic mode, UIA-native controls)
- Replace Tolk with UIA LiveRegion for most speech
- Rate-limit tuning speech (debounce ~300ms)
- ModeControl back in tab chain after FreqOut
- Earcons (boundary bonk, field tick, tune up/down tones)

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
7. VB.NET shows ShellForm → `ShellForm.OnShown()` → `SpeakWelcomeDelayed()` (Async, 2s Task.Delay) → `SpeakWelcome()`

**Note:** `openTheRadio()` now calls `AppShellForm.Show()` + `Activate()` before `Start()` so error dialogs have a parent window. Welcome speech uses 2s `Task.Delay` to let NVDA finish its focus announcements before speaking.

## 3) Sprint 12 Status — COMPLETE

### Completed Work
- **Native Win32 HMENU menus** — replaced WPF Menu → WinForms MenuStrip → native HMENU via P/Invoke
- **FreqOut interactive tuning** — all field handlers ported (Slice, Mute, Volume, SMeter, Split, VOX, Freq, Offset, RIT, XIT)
- **Frequency readout during tuning** — bypassed broken VB.NET delegate chain (NullRef in `RigControl.Callouts.FormatFreq`), uses direct C# formatting in `FormatFreqForSpeech()`
- **Step multiplier** — +N/-N sets custom tune steps (e.g., +5 at 1kHz = 5kHz steps)
- **Step speech fix** — "Step 5 1 kilohertz" → "Step 5 kilohertz" (strips leading number from unit name)
- **Frequency readout toggle** — F key toggles speech on/off during tuning
- **Ctrl+Shift+F** — speaks current frequency and slice
- **Error dialog parenting** — all 7 unparented MessageBox calls now use AppShellForm as owner
- **ShowErrorCallback** — NoSliceError and disconnect errors get screen reader speech + parented WinForms dialog
- **ShellForm.Show() before Start()** — error dialogs have a visible parent window
- **CrashReporter** — parented dialog + screen reader speech before dialog
- **Panadapter braille** — PanadapterPanel with Tolk.Braille() forwarding
- **SmartLink auth fixes** — expired JWT detection, ghost PKCE handler neutered
- **RigSelector accessibility** — runs on main UI thread (removed selectorThread), ListBox speech, auto-select first radio
- **Menu wiring** — Connect to Radio, Disconnect, Select Rig, focus return on Escape
- **Slice management** — period creates slice, comma releases slice, digit keys jump to slice
- **VOX toggle** — wired to FlexBase.Vox property
- **Verbose trace cleanup** — removed per-keypress and per-poll-cycle trace lines
- **SpeakWelcome** — "Welcome to JJ Flex" spoken 2s after window shown (Task.Delay, not WinForms Timer)

### Known Issues (carry to Sprint 13)
- ~~**SpeakWelcome regression**~~ — FIXED. Uses `Async Sub` + `Await Task.Delay(2000)` in `ShellForm.OnShown()` so NVDA finishes focus announcements before welcome speaks. WinForms Timer doesn't work in ElementHost (WM_TIMER swallowed).
- **Right arrow field navigation** — reads "slice 0 slice 1" (both slices at once)
- **NVDA menu announcement** — says "Radio alt+r" not "Radio menu alt+r"
- **Operations menu** — still a stub
- **Tab chain** — Tab doesn't cycle FreqOut fields (by design), but waterfall/panadapter not reachable via Tab

## 4) Sprint 13 Planning — Tab Chain & Field Navigation

**Scope (from research session Feb 19, 2026):**

### Tab Chain
- Focus lands on Slice field (VFO) on load
- Tab order: FreqOut → Waterfall display (if visible) → ReceivedTextBox → SentTextBox
- Skip ModeControl, TXTuneControl, buttons via IsTabStop=False (accessible via hotkeys)
- PanDisplayBox renamed to "Waterfall display" for screen reader

### Field Navigation
- Left/Right navigates within/between FreqOut fields (current behavior, keep)
- Home/End/PgDn for field jumping (current behavior, keep)

### Modern Mode
- Needs design decisions on which fields to include initially
- Separate tuning experience from Classic

### Other Fixes
- ~~SpeakWelcome timing~~ DONE
- Operations menu wiring
- Field announcement polish
- Mode announcement after welcome (e.g., "Welcome to JJ Flex, Classic mode")

## 5) Key Patterns

### Native Win32 HMENU Menus (CURRENT)
WPF Menu and WinForms MenuStrip both announce "collapsed/expanded" to screen readers. Only native Win32 HMENU menus (via P/Invoke `CreateMenu`/`SetMenu`) give clean screen reader navigation. `NativeMenuBar.cs` handles this.

### ElementHost Architecture (CRITICAL)
WPF content MUST be hosted via ElementHost in a WinForms ShellForm. Standalone WPF Windows shown from VB.NET My.Application get NO keyboard input.

### Frequency Speech — Direct C# Formatting
`FormatFreqForSpeech()` in FreqOutHandlers.cs formats frequency directly. Do NOT use the VB.NET delegate chain (`RigControl.Callouts.FormatFreq`) — it throws NullReferenceException due to timing issues with module-level variable access across VB.NET/C# boundary.

### Error Dialog Parenting
All error MessageBox calls must pass `AppShellForm` as owner. Use `ShowErrorCallback` for errors from WPF code (NoSliceError, disconnect). CrashReporter speaks before showing dialog.

### RadioComboBox _userEntry Pattern
`_userEntry` flag distinguishes user actions from programmatic updates in `SelectionChanged`. Always reset `_userEntry = true` after programmatic updates complete.

### SmartLink Auth Flow
- `TryAutoConnectRemote()` — startup path, checks `isJwtExpired` before sending to server
- `setupRemote()` — Remote button path, also checks JWT expiry first
- `WanApplicationRegistrationInvalidHandler` — neutered (just logs)
- `RemoteButton_Click` — runs `RemoteRadios()` on background STA thread

## 6) Completed Sprints
- Sprint 12: Stabilize WPF — menus, SmartLink, FreqOut tuning, error dialogs, screen reader speech
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
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet restore JJFlexRadio.vbproj -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers (release only)
build-installers.bat
```

**Note:** After `dotnet clean`, always run `dotnet restore` before `dotnet build` to avoid NETSDK1047 errors. Build the **project** directly (`JJFlexRadio.vbproj`), not the solution — solution builds may skip the main project.

---

*Updated: Feb 19, 2026 — Sprint 12 complete and archived. SpeakWelcome fixed (Task.Delay approach). Sprint 13 research done (tab chain, waterfall, Modern mode). Next: Sprint 13 implementation — tab chain & field navigation in Classic/Modern modes.*
