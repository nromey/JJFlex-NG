# JJ Flexible Home

The JJ Flexible Radio Access Home is the main interactive destination in the application — the place your frequency, slice controls, S-Meter, and other tuning-related fields live. Think of it as the radio's front panel brought inside the application, the destination your hand goes to when you want to operate the rig.

## Getting to the JJ Flexible Home

Press `F2` at any time to move focus to the JJ Flexible Home. Your screen reader will announce the destination along with the name of the sub-field you have landed on.

## What the JJ Flexible Home Contains

The JJ Flexible Home presents a sequence of fields that you move between by pressing the left and right arrow keys. The fields, in arrow-navigation order, are:

- **Slice** — the slice that is currently active (A, B, C, and so on)
- **Slice Operations** — volume, pan, and mute controls for the active slice
- **Frequency** — the current receive or transmit frequency
- **Mute** — the mute state of the active slice
- **Volume** — the audio volume of the active slice
- **S-Meter** — the signal strength indicator
- **Squelch** — whether squelch is currently on or off
- **Squelch Level** — the squelch threshold value (shown when squelch is on, hidden as `"---"` when squelch is off)
- **Split** — whether split operation is currently engaged
- **VOX** — whether VOX is currently on or off
- **Offset** — the repeater offset direction
- **RIT** — Receiver Incremental Tuning
- **XIT** — Transmitter Incremental Tuning

Use the left and right arrow keys to move between fields. Each field announces its name and its current value as soon as focus arrives.

## How the JJ Flexible Home Announces Itself

When focus arrives at the JJ Flexible Home, your screen reader speaks the destination name followed by the sub-field you are on. The exact announcement depends on your speech verbosity setting:

- **Terse** — "Home, slice" (or whichever field you are currently on)
- **Moderate** — "Home, slice"
- **Chatty** — "JJ Flexible Home, slice, 14.225.000" — the Chatty announcement includes the current frequency for full context

You can set your verbosity under **Settings > Notifications > Speech verbosity**.

## Universal Keys in the JJ Flexible Home

A small set of letter keys works the same way from any field inside the JJ Flexible Home, so you do not need to navigate to a specific field to use them:

- `M` — toggle mute on or off
- `V` — cycle to the next slice
- `R` — toggle Receiver Incremental Tuning (RIT) on or off
- `X` — toggle Transmitter Incremental Tuning (XIT) on or off
- `Q` — toggle squelch on or off
- `=` — make the current slice transceive (receive and transmit on the same slice)

These universal keys let you make quick adjustments without having to arrow to the exact field.

## Returning to the JJ Flexible Home from Anywhere

- `F2` — return to the JJ Flexible Home from anywhere in the application.
- `Escape` — if you are inside an expanded field group (DSP, Audio, Receiver, Transmission, or Antenna), a single Escape collapses the group and places focus on its header.
- `Escape` pressed twice quickly — collapses every open field group and returns you directly to the JJ Flexible Home.

## Related Topics

- Tuning and Frequency
- Slice Management
- Escape and Collapse
- Keyboard Reference
