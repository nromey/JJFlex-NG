# Screen Reader Setup

JJ Flexible Radio Access is designed to work with both NVDA and JAWS. Here is how to get the best experience from each.

## NVDA

NVDA works well right out of the box, and a few simple tips can make the experience even better:

- Make sure you are running a recent version of NVDA (2023.1 or later is recommended).
- JJ Flexible Radio Access announces important state changes automatically. If NVDA feels too chatty for your taste, you can tone down its verbosity in NVDA's own speech settings.
- The ScreenFields panel uses standard accessible controls. Use Tab to move between fields, and use the arrow keys to adjust values.

## JAWS

JAWS also works well, with a few things to keep in mind:

- JAWS may need a moment to detect the application on first launch.
- If you are not hearing announcements, make sure JAWS is set to read dynamic content changes.
- All menus are native Win32 menus, so JAWS navigates them naturally with the arrow keys and Enter.

## Startup Speech

When you first launch JJ Flexible Radio Access, you will hear "Welcome to JJ Flexible Radio Access." After you connect to a radio, the application automatically speaks a full status summary: the radio model, the connection type (local or SmartLink), your current frequency, your mode, the current band, and the active slice. For example, you might hear: "Connected to FLEX-6600, local network. Listening on 14.225 megahertz, Upper Side Band, 20 meters, slice A."

That greeting means you always know exactly where you are the moment you connect — you do not need to press anything to find out.

## Focus Order

When you navigate the main window with Tab, focus moves through these areas in the following order:

1. **FreqOut** — the frequency display and entry field.
2. **ScreenFields** — the expandable category panel (when any category is expanded).
3. **Meters Panel** — the meter slots (when the Meters Panel is open via `Ctrl+M`).
4. **Panadapter** — the spectrum display area.
5. **Received Text** — the incoming decoded-CW text (visible only in CW mode).
6. **Sent Text** — the outgoing CW text-entry box (visible only in CW mode).

Collapsed panels are skipped in the tab order, so if you do not have ScreenFields or the Meters Panel open, Tab moves right past them.

## Common Tips for Both Screen Readers

- **Menus** — press Alt to activate the menu bar. Arrow keys move through the menus, and Enter selects.
- **ScreenFields** — use `Ctrl+Shift+1` through `Ctrl+Shift+5` to jump directly to a ScreenFields category (Receiver, DSP, Audio, Transmission, Antenna).
- **Command Finder** — press `Ctrl+/` to search for any command by name. This is often faster than memorising every hotkey.
- **Status announcements** — press `Ctrl+Shift+S` to hear the current radio status spoken at any time. The status report is multi-slice aware — it tells you about every active slice, not just the selected one.
- **Earcons** — JJ Flexible Radio Access uses short audio tones (earcons) to confirm actions such as toggling DSP features. The earcons play through your default audio device alongside screen-reader speech.

## Announcements Worth Knowing About

A few accessibility behaviours happen automatically, and you may not notice them at first — but they are specifically worth mentioning:

- **Menu toggle states are spoken.** When you flip a DSP toggle (NR, NB, APF, and so on) through a menu, you hear "NR on" or "NR off" directly — you do not have to rely on a checkmark glyph, which does not always read the way you would hope on every combination of screen reader and menu style.
- **Dialog close announcements** tell you where you are when a dialog closes. When you leave Settings or the Status dialog, the application announces your current listening state ("Listening on 14.225, Upper Side Band, 20 meter band, slice A") so you do not have to press `Ctrl+Shift+S` yourself to re-orient after closing.
- **Button accelerator keys speak themselves.** When focus lands on a button that has a keyboard accelerator (most buttons do), your screen reader announces the accelerator along with the name — for example, "OK, Alt+O" or "Apply to connected radio, Alt+A." You do not have to explore the dialog to discover the Alt-key shortcuts.

## Troubleshooting

If your screen reader is not reading controls the way you expect:

1. Make sure JJ Flexible Radio Access is the focused application (Alt-Tab back to it).
2. Try pressing `Ctrl+Shift+S` to force a status announcement. If you hear it, the screen-reader connection is working.
3. Restart your screen reader if controls seem unresponsive.
4. Check that you are running the latest version of JJ Flexible Radio Access — accessibility improvements land in almost every release.
