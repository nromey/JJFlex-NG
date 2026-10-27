# About Dialog

The About dialog shows you version information, system details, diagnostic data, and credits for JJ Flexible Radio Access.

## Opening the About Dialog

Open the **Help** menu and choose **About**. The About dialog opens as a tabbed window.

## Tabs

The dialog has four tabs that you can move between using the arrow keys:

### About

The About tab shows the application name (JJ Flexible Radio Access), the current version number shown as a clickable link to What's New for this version, and a short description. The version text here is a link — activating it opens the What's New help topic for this release.

### Radio

The Radio tab displays details about the radio you are currently connected to — the radio model, its serial number, your radio's nickname, the firmware version, the IP address the radio is using, and its key capabilities (how many slices are active and available, whether diversity is supported, and so on). If you are not currently connected to a radio, this tab tells you so and suggests that you connect first.

### System

The System tab shows what is actually running, grouped under four headings you can jump between with your screen reader's heading navigation:

- **Application** — the program's version and full build number, where the executable lives on disk, and whether this install carries its own .NET runtime (self-contained) or uses a shared one.
- **Components** — the .NET runtime, FlexLib, the Opus audio codec, PortAudio, and the Microsoft Edge WebView2 runtime. Every version here is read live from the loaded library itself, never typed in — so what you see is what is really running. PortAudio is reported by its build revision, because its version number never changes upstream and would tell you nothing.
- **Environment** — the Windows version and whether the program is running as 64-bit.
- **Support** — the trace file's location on disk (so "where are your logs" is answered at a glance), the trace archive folder, the detected screen reader, and whether a braille display is available.

These details are what a support conversation runs on. All the text is selectable, and the Copy Everything button below grabs the whole report at once.

### Diagnostics

The Diagnostics tab shows your current connection status (active or not connected). It also exposes two buttons:

- **Check for Updates** — checks GitHub for the latest release and tells you whether an update is available. The check is user-initiated; JJ Flexible Radio Access never calls home on its own.
- **Run Connection Test** — launches the SmartLink connection tester against your currently-connected radio.

## Bottom-Row Buttons

Regardless of which tab you are on, the About dialog has four buttons along the bottom that are always visible:

- **View What's New** (`Alt+N`) — opens the What's New help topic for the current version.
- **Copy Everything** (`Alt+C`) — copies the full report — all four tabs — to the clipboard in one go, so nobody has to read version strings aloud on a support call. Paste it straight into an email or forum post.
- **Export Diagnostic Report** (`Alt+E`) — saves the same combined report to a text file on disk. The file is timestamped by date in the default filename.
- **Close** (`Alt+L`, or `Escape`) — closes the About dialog and returns focus to the main window. Escape works even while you are reading inside the page content.

If the WebView2 runtime is not installed on your machine, the About dialog still works: the same information appears as plain selectable text. You lose the formatting and heading navigation, never the facts.
