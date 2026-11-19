# Shell probe: the WPF main window gate

A throwaway test window for the shell replacement's Step 0 (#550): before the
WinForms shell is replaced with a WPF window, prove that a WPF window run the way
the product would run it speaks and takes keys under NVDA and JAWS.

**It is not part of the product.** It is in no solution, no product project
references it, and nothing ships it. It references `Radios` only to speak
through the app's own speech path.

## What it is

- The VB My.Application loop the product uses, with `OnRun` overridden so there
  is no main form. `OnRun` runs `Application.Run` on an `ApplicationContext`
  that ends when the WPF window closes.
- A WPF `Window` titled "JJ Shell Probe", made keyboard-capable with
  `ElementHost.EnableModelessKeyboardInterop`, with an `HwndSource` hook.
- A native Win32 menu bar (File: Open dialog, Message box, Speak, Exit; Help:
  About the probe), built the way `JJFlexWpf.NativeMenuBar` builds the
  product's. The real `NativeMenuBar` needs the product's `MainWindow`, so it
  is not used here.
- Inside, under a user control named "JJ Shell Probe Main Window": a copy of
  Home's frequency field (a read-only text box named "Home, frequency and
  VFO", automation class "FrequencyDisplay", control type Custom, inside a
  non-focusable user control named "JJ Flexible Home"), buttons Open dialog,
  Message box and Speak, and a text box named Notes.
- Open dialog shows a WPF dialog owned through its Win32 handle, as
  `JJFlexDialog` is. Message box shows a WinForms message box owned through an
  `IWin32Window` wrapper around the window's handle. Speak says one sentence
  through `Radios.ScreenReaderOutput`.
- A `StartupNextInstance` handler restores and activates the window.

## Switches

- `--hybrid`: the POSITIVE CONTROL. Today's shape in miniature: a WinForms form
  hosting the same content in an `ElementHost` named "JJ Shell Probe host", with
  `ShellForm`'s menu-close hop and Alt+letter post. Its host focus event must be
  seen here before a zero in the WPF shell means anything.
- `--keymenu-fallback`: the WPF window posts `SC_KEYMENU` itself for Alt+letter
  and F10, the research's fallback if WPF keeps those keys from the menu.

## Build and run

    dotnet build tools/shell-probe/ShellProbe.vbproj -c Debug -p:Platform=x64 --disable-build-servers -nodeReuse:false

Run it with `JJFLEX_CONFIG_DIR` set to a throwaway folder: the speech path keeps
a speaking-rate file under the settings root. Its trace is
`%TEMP%\jjshellprobe\probe-trace.txt`.

## Instruments beside it

- `evrec.ps1`: a passive WinEvent recorder (foreground, menu, dialog, show,
  focus and name-change events, each with the MSAA name and role a screen reader
  would fetch), plus UIA focus.
- `jaws/jjshellprobe.jss` and `.jkm`: copies of
  `tools/readercapture/jaws/jjflexible.*`, renamed because JAWS loads an
  application script by the executable's name. Round 2 appends two logging
  overrides to the `.jss` (`MenuModeEvent` and `FocusChangedEventEx`, each
  emitting a capture line and then calling the default), so the `.jss` is no
  longer byte-for-byte; the `.jkm` still is.

## Round 2 switches (the menu)

See `for-claude/2026-11-19-shell-probe-menu-research.md` (private) for why.

- `--no-dialog-keys`: candidate 1. A WinForms message filter, added after
  WPF's, translates and dispatches the keys WPF declined, so they never reach
  `IsDialogMessage` and Alt plus a menu letter reaches `DefWindowProc`.
- `--focus-after-menu`: candidate 2. On `WM_EXITMENULOOP`, raise
  `AutomationFocusChanged` on the focused element's peer without moving focus.
- `--dispatcher-loop`: candidate 4. The WPF window on `Dispatcher.Run`, with no
  WinForms loop and no `EnableModelessKeyboardInterop`.
- `--hybrid --nohop`: the hybrid without `ShellForm`'s menu-close hop.
- `--raw`: a plain Win32 window and Edit on a bare message loop, the reference
  for what the readers do with an untouched native menu.

Always on in the WPF window under the WinForms loop (instruments, behaviour-
neutral): key-message logging filters FIRST and LAST in the filter chain, and
`WM_GETDLGCODE`, `WM_INITMENU`, `WM_MENUCHAR`, `WM_MENUSELECT`, `DM_GETDEFID`
and friends in the hook's trace.
