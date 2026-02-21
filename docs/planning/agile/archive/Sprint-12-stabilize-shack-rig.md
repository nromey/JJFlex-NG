# Sprint 12: Stabilize the Shack

**Status:** COMPLETE
**Version:** 4.1.115
**Branch:** main
**Dates:** Feb 2026

## Goal

Stabilize the WPF app after the Sprint 8-11 conversion marathon. Make the app actually usable: menus work, tuning works, errors don't vanish behind windows, screen reader speech fires reliably.

## Completed Work

### Native Win32 HMENU Menus (Track A)
- Replaced WPF Menu with native Win32 HMENU via P/Invoke (`NativeMenuBar.cs`)
- Previous attempts (WPF Menu, WinForms MenuStrip) both announced "collapsed/expanded" to screen readers
- Native HMENU uses `ROLE_SYSTEM_MENUBAR` / `ROLE_SYSTEM_MENUITEM` — clean screen reader navigation
- Three mode-specific menu sets (Classic, Modern, Logging) rebuilt as HMENU
- Menu wiring: Connect to Radio, Disconnect, Select Rig
- Focus returns to WPF content on Escape/menu item selection via WM_EXITMENULOOP

### FreqOut Interactive Tuning
- All field handlers ported to WPF: Slice, Mute, Volume, SMeter, Split, VOX, Freq, Offset, RIT, XIT
- Frequency readout during tuning — bypassed broken VB.NET delegate chain (`RigControl.Callouts.FormatFreq` threw NullRef), uses direct C# formatting in `FormatFreqForSpeech()`
- Step multiplier: +N/-N sets custom tune steps (e.g., +5 at 1kHz = 5kHz steps)
- Step speech fix: "Step 5 1 kilohertz" → "Step 5 kilohertz"
- Frequency readout toggle: F key toggles speech on/off during tuning
- Ctrl+Shift+F: speaks current frequency and slice from any mode

### Error Dialog Parenting
- All 7 unparented `MessageBox.Show` calls now use `AppShellForm` as owner
- `ShowErrorCallback` pattern: WPF code routes NoSliceError and disconnect errors to WinForms `MessageBox.Show(AppShellForm, ...)`
- `ShellForm.Show()` before `Start()` so error dialogs have a visible parent window
- `CrashReporter`: parented dialog + screen reader speech before dialog
- `ScreenReaderOutput.Speak()` called before every error dialog

### SmartLink Auth Fixes
- Expired JWT detection before sending to server
- Ghost PKCE handler neutered (just logs, no more phantom auth windows)
- Station name timeout increased to 45s (was 30s, caused race condition on WAN)

### RigSelector Accessibility
- Runs on main UI thread (removed `selectorThread` — was causing slow focus transitions and NVDA speech delays)
- ListBox speech for radio selection
- Auto-select first radio in list

### Panadapter Braille Display
- `PanadapterPanel` with `PanDisplayBox` TextBox for braille cursor navigation
- `Tolk.Braille()` forwarding for refreshable braille displays
- PageUp/PageDown for range jumping
- Segment display with low/high frequency labels

### Slice Management
- Period creates slice, comma releases slice
- Digit keys jump to slice number

### Other
- VOX toggle wired to `FlexBase.Vox` property
- Verbose trace cleanup — removed per-keypress and per-poll-cycle trace lines
- SpeakWelcome — "Welcome to JJ Flex" spoken 2s after window shown (Task.Delay to avoid NVDA stomping)

## Key Architectural Decisions

### WinForms Timer broken in ElementHost
WinForms `Timer` (WM_TIMER) does not fire reliably inside ShellForm with ElementHost. Use `Async Sub` + `Await Task.Delay()` instead — uses `System.Threading.Timer` internally, bypasses message pump.

### VB.NET delegate chains are fragile
`Function(s) RigControl.Callouts.FormatFreq(ULong.Parse(s))` threw NullReferenceException because `RigControl.Callouts` was null at call time despite the module variable being set. Fix: do formatting directly in C# (`FormatFreqForSpeech()` in FreqOutHandlers.cs).

### Native HMENU is the only accessible menu option
- WPF Menu: announces "collapsed/expanded"
- WinForms MenuStrip: also announces "collapsed/expanded"
- Native Win32 HMENU: clean `ROLE_SYSTEM_MENUBAR` / `ROLE_SYSTEM_MENUITEM`

### Dialogs must run on T1
Running WinForms dialogs on a separate STA thread caused slow focus transitions and NVDA speech delays. Screen readers track the main UI thread. `ShowDialog()` on T1 directly is correct.

## Known Issues (Carried to Sprint 13)

- **Right arrow field navigation** — reads "slice 0 slice 1" (both slices at once)
- **NVDA menu announcement** — says "Radio alt+r" not "Radio menu alt+r"
- **Operations menu** — still a stub
- **Tab chain** — Tab doesn't cycle between FreqOut and other controls (waterfall, text areas)
- **Window focus on first SmartLink connect** — sometimes doesn't focus, requires reconnect
