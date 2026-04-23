# Known Issues

This page lists known issues and workarounds in the current version of JJ Flexible Radio Access.

## General

- **The first launch may be slow.** The .NET runtime initialises the first time you launch the application, which takes a few seconds longer than subsequent launches.
- **Windows Defender SmartScreen may warn about the installer.** SmartScreen shows a warning for the installer because JJ Flexible Radio Access is not yet signed by a large commercial publisher's certificate. You can click **More info** and then **Run anyway** to proceed. A code-signing certificate is on the roadmap, which will remove this warning for good.

## Screen Readers

- **JAWS cursor routing** may occasionally lose track of the focused control in some contexts. Pressing `Escape` and then re-entering the area usually fixes this.
- **NVDA with certain add-ons** — some NVDA add-ons can interfere with JJ Flexible Radio Access's announcements. If you are having trouble with speech, try temporarily running NVDA with add-ons disabled to see if one of the add-ons is the source.

## Audio

- **Audio device changes mid-session** — if you plug in or unplug headphones (or any other audio device) while JJ Flexible Radio Access is running, the application may not automatically pick up the new device. A restart of the application will pick up the change.

## SmartLink

- **Session timeout on long sessions** — long SmartLink sessions may occasionally need re-authentication. If your remote connection drops after being up for a long time, try reconnecting by opening the **Radio** menu and choosing **Connect to Radio** again.

## Reporting Issues

If you find a bug, or if you have a feature request, please contact Noel Romey (K5NER). Your feedback helps make JJ Flexible Radio Access better for everyone.
