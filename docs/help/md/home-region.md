# Home

Home is the main interactive area of JJ Flex where your frequency, slice controls, S-meter, and other tuning-related fields live. Think of it as the radio's "front panel" inside the app — the place your hand goes when you want to operate the rig.

## Getting to Home

Press `F2` at any time to move focus to Home. Your screen reader will announce the region name along with the current sub-field you landed on.

## What's in Home

Home contains a row of fields you can move between with the left and right arrow keys. The fields are:

- **Slice** — which slice is currently active (A, B, C, etc.)
- **Slice Operations** — volume, pan, and mute controls for the active slice
- **Frequency** — the current receive or transmit frequency
- **Mute** — mute state for the active slice
- **Volume** — audio volume for the active slice
- **S-Meter** — signal strength indicator
- **Squelch** — squelch on/off indicator
- **Squelch Level** — the threshold level (shown when squelch is on)
- **Split** — split operation indicator
- **VOX** — VOX on/off
- **Offset** — repeater offset direction
- **RIT** — receiver incremental tuning
- **XIT** — transmitter incremental tuning

Use `Left` and `Right` arrows to move between fields. Each field announces its name and current value when focus lands on it.

## How Home Announces Itself

When focus lands on Home, your screen reader speaks the region name and the sub-field you're on. The exact announcement depends on your speech verbosity setting:

- **Terse:** "Home, slice" (or whichever field is current)
- **Moderate:** "Home, slice"
- **Chatty:** "JJ Flexible Home, slice, 14.225.000" — includes the current frequency for full context

Set your verbosity in **Settings > Notifications > Speech verbosity**.

## Universal Keys in Home

Certain letter keys work the same way from any Home field, so you don't have to navigate to a specific field to use them:

- `M` — toggle mute
- `V` — cycle to next slice
- `R` — toggle RIT
- `X` — toggle XIT
- `Q` — toggle squelch
- `=` — make the current slice transceive (receive AND transmit on this slice)

These universal keys let you make quick adjustments without arrowing around.

## Returning to Home from Anywhere

- `F2` — go to Home from anywhere in the app
- `Escape` — from inside an expanded field group, collapses the group and returns to Home
- `Escape` twice quickly — collapses all open field groups and lands you back on Home

## Related Topics

- Tuning and Frequency
- Slice Management
- Keyboard Reference
- Escape and Collapse
