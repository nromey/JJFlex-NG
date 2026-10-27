# Squelch in Home

Squelch mutes your receiver when the signal falls below a threshold you set, so you don't hear static when noone is transmitting. JJ Flexible Radio Access makes squelch directly controllable from the JJ Flexible Home — you don't have to expand the Receiver field group to adjust it during a QSO.

## Where to Find It

From the JJ Flexible Home, press the right arrow until you hear two adjacent fields:

- **Squelch** — shows "Q" when squelch is on, and nothing when it's off. This is the field that tells you at a glance whether squelch is currently active.
- **Squelch Level** — shows the current threshold level (0 to 100). The number stays visible whether squelch is on or off; when squelch is off, the number is the level that will take effect as soon as you turn squelch back on.

## Toggling Squelch

Press `Q` from **any** Home field to toggle squelch on or off. You don't need to navigate to the Squelch field specifically — the shortcut works from Slice, Slice Operations, Frequency, or from the Squelch and Squelch Level fields themselves. That's the same universal-key pattern as `M` for mute, `R` for RIT, and `X` for XIT.

You can also press `Space` on the Squelch field itself to toggle it.

When you toggle squelch, you will hear a short two-tone earcon (ascending for on, descending for off) and your screen reader speaks "Squelch on" or "Squelch off."

## Adjusting the Squelch Level

Use the arrow keys to move to the Squelch Level field. Press `Up` or `Down` to adjust the level in steps of 5. Each adjustment announces the new value — "Squelch level 45" and so on.

Squelch Level adjustment works whether squelch is currently on or off. When squelch is off, the adjustment still changes the stored level — your change takes effect as soon as you turn squelch back on. The field shows the new number either way, so the announcement and the field display always agree.

## How Squelch and Squelch Level Work Together

The Squelch field is the active-state indicator: "Q" when on, nothing when off. The Squelch Level field is the threshold value, which stays visible whether squelch is currently active or not. This split lets you pre-configure a threshold even while squelch is off, then flip squelch on and have the stored level take effect immediately.

## When to Use Squelch

- On busy bands with lots of background QRM, where you only want to hear actual transmissions.
- For FM operation, where squelch is effectively mandatory.
- In CW pileups, where the noise floor between callers gets fatiguing after a while.

SSB DXing usually works better with squelch turned off, so you can hear weak signals trying to break through the noise.

## Related Topics

- Home
- Keyboard Reference
- Filters and DSP
