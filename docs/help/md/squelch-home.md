# Squelch in Home

Squelch mutes your receiver when the signal falls below a threshold, so you don't hear static when no one is transmitting. JJ Flex makes squelch directly controllable from Home — no need to expand the Receiver field group to adjust it during a QSO.

## Where to Find It

In Home, arrow right past the S-Meter field. You'll land on two adjacent fields:

- **Squelch** — shows "Q" when squelch is on, blank when off.
- **Squelch Level** — shows the current threshold level (0 to 100) when squelch is on, or "---" when squelch is off.

## Toggling Squelch

Press `Q` from **any** Home field to toggle squelch on or off. You don't need to navigate to the Squelch field specifically — it works from Slice, Slice Operations, Frequency, or the Squelch and Squelch Level fields themselves. That's the same universal-key pattern as `M` for mute, `R` for RIT, and `X` for XIT.

You can also press `Space` on the Squelch field itself to toggle.

When you toggle squelch, you hear a short two-tone earcon (ascending for on, descending for off) and your screen reader speaks "Squelch on" or "Squelch off."

## Adjusting the Level

Arrow to the Squelch Level field. Press `Up` or `Down` to adjust the level in steps of 5. Each adjustment announces the new value — "Squelch level 45" and so on.

Level adjustment works whether squelch is currently on or off. When squelch is off, the field displays "---" (meaning the level has no effect right now), but your adjustments are still remembered — they take effect as soon as you turn squelch back on.

## Why the Level Disappears When Squelch is Off

A visible number when squelch is off would be misleading — the threshold isn't active, so the number doesn't match what the radio is doing. Showing "---" signals "this setting isn't currently in effect, but it's remembered for when you turn squelch on." The same pattern is used for RIT and XIT: the offset value shows only when the feature is active.

## When to Use Squelch

- Busy bands with lots of background QRM where you only want to hear actual transmissions.
- FM operation, where squelch is effectively mandatory.
- CW in pileups where the noise floor between callers gets fatiguing.

SSB DXing usually works better with squelch off so you can hear weak signals trying to break through.

## Related Topics

- Home
- Keyboard Reference
- Filters and DSP
