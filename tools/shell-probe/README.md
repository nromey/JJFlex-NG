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
- `jaws/jjshellprobe.jss` and `.jkm`: byte-for-byte copies of
  `tools/readercapture/jaws/jjflexible.*`, renamed because JAWS loads an
  application script by the executable's name.
