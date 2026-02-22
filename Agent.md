# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.115 (target 4.1.116)
- **Sprint 12:** COMPLETE — Stabilization phase. Plan archived.
- **Sprint 13:** COMPLETE — Testing done, bug fixes applied, committed.
  - 13A: Tab chain (FreqOut→Waterfall→Received→Sent), welcome mode announcement
  - 13C: ScreenFields & Operations menus wired with DSP/audio/ATU/receiver controls
  - 13B: Modern mode tuning redesigned (coarse/fine toggle, sane step presets)
  - 13D: Modern menus (Slice/Filter/Audio) wired via shared handlers from 13C
  - Testing fixes: SmartLink auto-retry, STA crash, Modern tuning, menu checkmarks, NR gating, filter bounds, Freq field hotkeys
- **Sprint 14:** COMPLETE — ScreenFieldsPanel, speech debounce, slice menu, filter race fix + boundary announcements.
  - ScreenFieldsPanel: 5 expandable categories (DSP, Audio, Receiver, TX, Antenna) with 25+ focusable controls
  - Speech debounce: 300ms tuning frequency speech rate-limiting
  - Slice menu: active indicator, create/release, count label
  - Filter race condition fix: atomic SetFilter() replaces separate FilterLow/FilterHigh enqueue
  - Filter LSB/CW fix: removed Math.Max(0,...) clamping that broke negative filter edges
  - Filter boundary announcements: "Filter at minimum/maximum", "Beginning", "End"

## Sprint 15 Planning

**Filter overhaul:**
- Band-based filter presets (popular defaults per mode: SSB, CW, digital)
- User-configurable presets (add/remove/rename per band+mode)
- Ctrl+brackets to jump between presets
- Adaptive narrowing step sizes (smaller steps as filter gets narrower)
- Wire FiltersDspControl (FilterLowBox/FilterHighBox) through SetFilter()
- Test minimum filter width the radio hardware supports
- **Passband shift FIXED (Sprint 14 testing):** Shift+[ / Shift+] now slide the entire
  passband down/up together. Both edges move by 50 Hz — bandwidth preserved.
- **Independent edge keys (Sprint 15):** Add Ctrl+[ and Ctrl+] to trim one edge inward.
  Full key set with this addition:
  - `[` narrow both, `]` widen both
  - `Shift+[` slide passband down, `Shift+]` slide passband up
  - `Ctrl+[` low edge up (trim bottom), `Ctrl+]` high edge down (trim top)
  Use case: on LSB raise the lower cutoff to kill rumble without touching the top edge.
- **Filter speech hotkeys (Sprint 15):**
  - `F` = toggle filter speech on/off (silence filter reads)
  - `Alt+Ctrl+F` = read current filter values aloud
- **Filter hotkeys in Classic mode (bug):** Bracket keys currently gated to Modern only
  (MainWindow.xaml.cs:567). Should work in both modes — fix as part of Sprint 15 filter work.
- **FieldsPanel tab order bug:** FieldsPanel ends up last in tab order (after Sent Text)
  instead of second (after FreqOut). Cause: Expanders inside have no explicit TabIndex
  so WPF floats them to int.MaxValue, after all explicitly-indexed controls. Fix when
  doing ScreenFields menu redesign — the menu navigation will be the primary access path.
- **FieldsPanel visibility not saved:** ShowClassicUI() always forces FieldsPanel visible.
  The "Show Field Panel" toggle works per-session but resets on restart/mode switch.
  Should persist to operator profile like UIMode does.
- **ScreenFields menu redesign:** Replace the flat DSP control dump with 5 expander
  navigation items mirroring the panel categories. Menu becomes:
    Show Field Panel [✓]
    ──────────────────
    Noise Reduction and DSP  [collapsed/expanded]  Ctrl+Shift+1
    Audio                    [collapsed/expanded]  Ctrl+Shift+2
    Receiver                 [collapsed/expanded]  Ctrl+Shift+3
    Transmission             [collapsed/expanded]  Ctrl+Shift+4
    Antenna                  [collapsed/expanded]  Ctrl+Shift+5
  Activating (menu or hotkey): if collapsed → show panel, expand, focus expander header.
  If expanded → collapse. Hotkeys shown in menu. Max 7 items, no giant flat control list.
  Solves tab order problem — menu/hotkey is faster than tabbing through whole UI.
  DSP/Filter/Diversity controls move to Operations menu (where they belong).

**UIA / Tolk reduction:**
- Replace Tolk with UIA LiveRegion for most speech output
- Identify which Speak() calls can use LiveRegion vs which need Tolk

**Menu audit:**
- Verify all commands are wired in both Classic and Modern menus
- Identify missing Modern menu items that exist in Classic

**Carried forward:**
- Configurable tuning step lists (user adds/removes from coarse/fine presets)
- Global tuning hotkeys (system-wide — tune/transmit/lock from external apps)
- Logging mode: F6 to flip to radio view
- Alt+letter menu accelerator investigation
- Menu state display for non-toggle items

**Transmit button + PTT safety (Sprint 15):**
- Add Transmit button to Classic main screen (not first in tab order — FreqOut is first)
- Space = PTT hold (TX on keydown, RX on keyup)
- Shift+Space = lock TX, speak "Transmitting, locked"
- Escape or Shift+Space again = unlock, speak "Transmit off"
- User-configurable warning stages:
    Timeout         — hard kill time (default 3 min, max 15 min)
    First warning   — when to start 10-second beeps
    Second warning  — when to start 5-second beeps
    Oh Crap Warning — when to start 1-second beeps ("Oh Crap Warning" is the official name)
- Hard 15 min kill — absolute, non-configurable, no override. Nobody needs 15 min PTT.
- ALC=0 for 60s while locked = auto-release, speak "No signal detected, transmit off"
- Beep tones synthesized via earcon system, not wave files
- Test using Don's radio with TX off ("software dummy load")

**Deferred / future:**
- Earcons (boundary bonk, field tick, tune up/down tones) — transmit warning is first use case.
  Prefer synthesized tones over wave assets for most earcons (no file management, fully
  controllable pitch/duration/envelope). Wave assets available for richer sounds where needed.
- Virtual dummy load: audio setup option that enables TX pipeline without RF output,
  lets you hear yourself via second slice or transverter mode for testing. Way down the road.
- ModeControl back in tab chain after FreqOut

## SmartLink Connection — FIXED (needs testing on Don's radio)

**Fix:** GUIClient removal tracked with timestamp. 15s grace after removal, then disconnect
and auto-retry with fresh connection. No error dialog. Speech: "Connection slow, retrying."
Worst case ~21s instead of 55s + error dialog + manual reconnect.
Also fixed STA crash in SpeakWelcomeDelayed (Task.Delay resuming on thread pool thread).

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
- Sprint 14: ScreenFieldsPanel, speech debounce, slice menu, filter race fix + boundary announcements
- Sprint 13: Tab chain, Modern tuning, menus, SmartLink auto-retry, testing fixes
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

*Updated: Feb 21, 2026 — Sprint 14 complete. ScreenFieldsPanel (5 categories, 25+ controls), speech debounce, slice menu, filter race fix (atomic SetFilter), LSB/CW filter clamping fix, boundary announcements. Next: Sprint 15 — filter overhaul (band presets, adaptive steps), UIA LiveRegion, menu audit.*
