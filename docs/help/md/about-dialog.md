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

The System tab shows technical details about your setup — the .NET runtime version, Windows version, CPU architecture, FlexLib version, Microsoft Edge WebView2 version, the detected screen reader (if any), and whether a braille display is available. These details are useful for troubleshooting, and for reporting bugs where environment-specific behaviour might matter.

### Diagnostics

The Diagnostics tab shows your current connection status (active or not connected). It also exposes two buttons:

- **Check for Updates** — checks GitHub for the latest release and tells you whether an update is available. The check is user-initiated; JJ Flexible Radio Access never calls home on its own.
- **Run Connection Test** — launches the SmartLink connection tester against your currently-connected radio.

## Bottom-Row Buttons

Regardless of which tab you are on, the About dialog has four buttons along the bottom that are always visible:

- **View What's New** (`Alt+N`) — opens the What's New help topic for the current version.
- **Copy to Clipboard** (`Alt+C`) — copies the current tab's plain-text content to the clipboard. Useful for pasting into a support email or forum post.
- **Export Diagnostic Report** (`Alt+E`) — saves a combined report of all four tabs to a text file on disk. The file is timestamped by date in the default filename.
- **Close** (`Alt+L`, or `Escape`) — closes the About dialog and returns focus to the main window.
