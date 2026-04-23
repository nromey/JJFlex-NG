# Squelch in Home

Squelch mutes your receiver when the signal falls below a threshold you set, so you don't hear static when noone is transmitting. JJ Flexible Radio Access makes squelch directly controllable from the Home region — you don't have to expand the Receiver field group to adjust it during a QSO.

## Where to Find It

From the Home region, press the right arrow  until you hear two adjacent fields:

- **Squelch** — shows "Q" when squelch is on, and nothing when it's off.
- **Squelch Level** — shows the current threshold level (0 to 100) when squelch is on, or "---" when squelch is off.

## Toggling Squelch

Press `Q` from **any** Home field to toggle squelch on or off. You don't need to navigate to the Squelch field specifically — the shortcut works from Slice, Slice Operations, Frequency, or from the Squelch and Squelch Level fields themselves. That's the same universal-key pattern as `M` for mute, `R` for RIT, and `X` for XIT.

You can also press `Space` on the Squelch field itself to toggle it.

When you toggle squelch, you will hear a short two-tone earcon (ascending for on, descending for off) and your screen reader speaks "Squelch on" or "Squelch off."

## Adjusting squelch Level

Use the arrow keys to move to the Squelch Level field. Press `Up` or `Down` to adjust the level in steps of 5. Each adjustment announces the new value — "Squelch level 45" and so on.

Squelch Level adjustment works whether squelch is currently on or off. When squelch is off, the field displays "---" (meaning the level has no effect right now), but your adjustments are still remembered — they take effect as soon as you turn squelch back on.

## Why the Level Disappears When Squelch is Off

A visible number when squelch is off would be misleading, because the threshold isn't active and the number wouldn't match what the radio is actually doing. Showing "---" signals "this setting isn't currently in effect, but it's remembered for when you turn squelch back on." The same pattern is used for RIT and XIT: the offset value appears only when the feature is active. When sSquelch is off, you will not hear the squelch level field though toggling the field will instantly use the previously set value.

## When to Use Squelch

- On busy bands with lots of background QRM, where you only want to hear actual transmissions.
- For FM operation, where squelch is effectively mandatory.
- In CW pileups, where the noise floor between callers gets fatiguing after a while.

SSB DXing usually works better with squelch turned off, so you can hear weak signals trying to break through the noise.

## Related Topics

- Home
- Keyboard Reference
- Filters and DSP
