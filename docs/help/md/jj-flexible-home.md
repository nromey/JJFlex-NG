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
- **Squelch Level** — the squelch threshold value. The number stays visible whether squelch is on or off; when squelch is off, it is the level that will take effect as soon as you turn squelch back on.
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

A small set of letter keys works the same way from most fields inside the JJ Flexible Home, so you do not need to navigate to a specific field to use them:

- `M` — toggle mute on or off for the currently active slice
- `Shift+M` — toggle mute on or off across every slice at once (multi-slice earcon: a rising or falling tri-tone, distinct from the single-slice mute sound so you can tell the two actions apart by ear)
- `V` — cycle to the next slice
- `R` — toggle Receiver Incremental Tuning (RIT) on or off
- `X` — toggle Transmitter Incremental Tuning (XIT) on or off
- `Q` — toggle squelch on or off
- `=` — make the current slice transceive (receive and transmit on the same slice)
- `Shift+Comma` (the `<` key on US keyboards) — release every slice except the one you are currently on, so you end up cleanly on a single-slice setup. Handy when you want to reset back to one slice in one keystroke.

These universal keys let you make quick adjustments without having to arrow to the exact field.

### Per-field overrides

A couple of fields take some letter and digit keys for their own purposes. The universal keys still work from elsewhere in the JJ Flexible Home, but on these fields specifically the field-local meanings win:

- **Slice field** — digit keys `0` through `9` and letter keys `A` through `H` jump directly to the slice with that index or letter, instead of being treated as universal-key inputs. So pressing `B` on the Slice field selects Slice B; pressing `B` on the Frequency field has no effect.
- **Slice Operations field** — `m` is **explicit mute** (always sets to muted, re-announces if already muted), `S` is **explicit unmute** (always sets to sounding), `A` activates the current slice as the receive slice, and `T` makes it the transmit slice. These are deliberate overrides of the universal-key meanings, taken from Jim Shaffer's original slice-status-row vocabulary so that long-time JJ Flex users do not have to relearn the field. The toggle-style `Space` key on Slice Operations is also a mute toggle, parallel to the universal `M` from other fields.

If you find yourself wanting the universal behaviour on one of these fields, just arrow to a different Home field first and press the key from there.

## Returning to the JJ Flexible Home from Anywhere

- `F2` — return to the JJ Flexible Home from anywhere in the application.
- `Escape` — if you are inside an expanded field group (DSP, Audio, Receiver, Transmission, or Antenna), a single Escape collapses the group and places focus on its header.
- `Escape` pressed twice quickly — collapses every open field group and returns you directly to the JJ Flexible Home.

## Related Topics

- Tuning and Frequency
- Slice Management
- Escape and Collapse
- Keyboard Reference
