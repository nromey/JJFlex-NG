# Screen Reader Setup

JJ Flexible Radio Access is designed to work with NVDA and JAWS. Here's how to get the best experience.

## NVDA

NVDA works well right out of the box. A few tips:

- Make sure you're running a recent version of NVDA (2023.1 or later recommended).
- The app announces important state changes automatically. If NVDA is too chatty, you can adjust verbosity in NVDA's speech settings.
- The ScreenFields panel uses standard accessible controls. Use Tab to move between fields and arrow keys to adjust values.

## JAWS

JAWS also works well. A few things to know:

- JAWS may need a moment to detect the application on first launch.
- If you're not hearing announcements, make sure JAWS is set to read dynamic content changes.
- All menus are native Win32 menus, so JAWS navigates them naturally with the arrow keys and Enter.

## Startup Speech

When you first launch the app, you'll hear "Welcome to JJ Flexible Radio Access." After you connect to a radio, the app automatically speaks a full status summary: the radio model, connection type (local or SmartLink), your current frequency, mode, band, and active slice. For example: "Connected to FLEX-6600, local network. Listening on 14.225 megahertz, USB, 20 meters, slice A."

This means you always know exactly where you are the moment you connect — no need to press anything.

## Focus Order

When navigating the main window with Tab, focus moves through these areas in order:

1. **FreqOut** — The frequency display/entry field
2. **ScreenFields** — The expandable categories panel (when any category is expanded)
3. **Meters Panel** — The meter slots (when the meters panel is open via Ctrl+M)
4. **Panadapter** — The spectrum display area
5. **Received Text** — Incoming decoded text
6. **Sent Text** — Outgoing text entry

Collapsed panels are skipped in the tab order, so if you don't have ScreenFields or the Meters Panel open, Tab moves right past them.

## Common Tips for Both Screen Readers

- **Menus:** Press Alt to activate the menu bar. Arrow keys navigate, Enter selects.
- **ScreenFields:** Use `Ctrl+Shift+1` through `Ctrl+Shift+5` to jump directly to a ScreenFields category (Receiver, DSP, Audio, Transmission, Antenna).
- **Command Finder:** Press `Ctrl+Shift+H` (or `Ctrl+/`) to search for any command by name. This is often faster than memorizing every hotkey.
- **Status announcements:** Press `Ctrl+Shift+S` to hear the current radio status spoken at any time. This is multi-slice aware — it tells you about all active slices.
- **Earcons:** JJ Flexible Radio Access uses short audio tones (earcons) to confirm actions like toggling DSP features. These play through your default audio device alongside screen reader speech.

## Announcements You Should Know About

A few recent accessibility additions are worth knowing about specifically — they happen automatically and you may not notice them at first:

- **Menu toggle states are spoken.** When you flip a DSP toggle (NR, NB, APF, etc.) through a menu, you hear "NR: On" or "NR: Off" directly — you don't have to rely on the checkmark glyph, which doesn't always read the way you'd hope on every combination of screen reader and menu style.
- **Dialog close announcements** tell you where you are when a dialog closes. When you leave Settings or the Status dialog, the app announces the current listening state ("Listening on 14.225, USB, 20 meter band, slice A") so you don't have to press Ctrl+Shift+S to re-orient after closing.
- **Button access keys speak themselves.** When focus lands on a button that has a keyboard accelerator (most do), the screen reader announces the accelerator along with the name: "OK, Alt+O" or "Apply to connected radio, Alt+A." This is automatic — you don't have to explore the dialog to find the Alt-key shortcuts.

## Troubleshooting

If your screen reader isn't reading controls:

1. Make sure JJ Flexible Radio Access is the focused application (Alt+Tab to it).
2. Try pressing `Ctrl+Shift+S` to force a status announcement. If you hear it, the screen reader connection is working.
3. Restart your screen reader if controls seem unresponsive.
4. Check that you're running the latest version of JJ Flexible Radio Access — accessibility improvements land in almost every release.
