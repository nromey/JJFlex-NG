# Known Issues

This page lists known issues and workarounds in the current version of JJFlexRadio.

## General

- **First launch may be slow.** The .NET runtime initializes on first launch. Subsequent launches are faster.
- **Windows Defender may scan the installer.** SmartScreen might show a warning for the installer since it's not from a big commercial publisher. You can click "More info" and then "Run anyway."

## Screen Readers

- **JAWS cursor routing** may occasionally lose track of the focused control. Pressing `Escape` and then re-entering the area usually fixes this.
- **NVDA with certain add-ons** — some NVDA add-ons may interfere with JJFlexRadio's announcements. If you're having issues, try running NVDA with add-ons disabled to narrow it down.

## Audio

- **Audio device changes** while JJFlexRadio is running (plugging in or unplugging headphones, for example) may require restarting the application to pick up the new device.

## SmartLink

- **Session timeout** — Long SmartLink sessions may occasionally need re-authentication. If your remote connection drops, try reconnecting from the Connect menu.

## Reporting Issues

If you find a bug or have a feature request, please contact Noel Romey (K5NER). Your feedback helps make JJFlexRadio better for everyone.
