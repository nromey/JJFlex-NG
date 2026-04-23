# Quick Actions (Redesign in Progress)

The Ctrl+Tab "Quick Actions" popup menu has been temporarily disabled and is being redesigned. This help page is kept so that users who remember the old popup know where it went.

## What Changed

In earlier releases, pressing `Ctrl+Tab` opened a small popup menu containing ATU Tune, Tune Carrier, Transmit, and Speak Status. That popup menu has been disabled.

`Ctrl+Tab` now does what a keyboard-driven user expects Ctrl+Tab to do in a tabbed interface — it cycles through the major field groups on the main window (DSP, Audio, Receiver, Transmission, Antenna). `Ctrl+Shift+Tab` reverses the direction of that cycling. See the Keyboard Reference page for full details.

## Why the Change

The old popup menu did not behave like an accessible toolbar the way it was intended to — it was more of a one-off command palette, and stealing `Ctrl+Tab` for it conflicted with the universal "cycle between panes or tabs" convention. A future release will introduce a proper accessible toolbar, with a persistent presence and standard navigation behaviour, that restores access to those actions in a form that does not collide with panel navigation.

## What to Use Instead for Now

Until the redesigned toolbar ships, the individual actions that used to be in the popup are still available through their regular hotkeys:

- **ATU Tune** — press `Ctrl+T`.
- **Tune Carrier** — press `Ctrl+Shift+T` to toggle the carrier on or off.
- **Transmit** — use your PTT input (footswitch, hand microphone, or VOX).
- **Speak Status** — press `Ctrl+Shift+S` for a short status summary, or `Ctrl+Alt+S` for the full live Status dialog.

## Related Topics

- PTT and Transmission
- Status Dialog
- Keyboard Reference
