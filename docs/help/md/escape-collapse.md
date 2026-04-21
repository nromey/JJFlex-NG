# Escape and Collapse

Escape is your "back out" key in JJ Flex. It works differently depending on what you're doing and whether you press it once or twice.

## Single Escape

Press `Escape` once when focused inside an expanded field group (DSP, Audio, Receiver, Transmission, or Antenna). The group collapses, focus lands on the group header, and you hear a short descending chirp confirming the collapse.

If you're not inside an expanded group — for example, you're on Home already — single Escape returns focus to Home, same as pressing `F2`.

## Double Escape (Collapse All)

Press `Escape` twice quickly. All open field groups collapse at once, focus returns to Home, and you hear the "two-tone descent" earcon — a higher tone followed by a lower tone — signaling "everything closed, you're home."

"Quickly" here is controlled by your double-tap tolerance setting (see the Double-Tap Tolerance help topic). The default is 500 milliseconds between presses.

## Why It Works This Way

Escape as "back out" works at two scales:

- One Escape = back out of the current group (local)
- Two Escapes = back out of everything (global)

Same key, same semantic, two levels of scope. You learn the rule once and it applies everywhere.

## Re-Expanding a Collapsed Group

After Escape-collapse, focus is on the group's header. Press `Space` to re-expand the group. You'll hear the ascending "expand" earcon (mirror of the collapse chirp, going up instead of down).

## What You'll Hear

- **Collapse one group:** descending chirp from 1200 Hz down to 400 Hz over 350 ms, with a gritty noise sweep that cuts through radio ambient noise.
- **Expand one group:** ascending chirp, 400 Hz up to 1200 Hz, same texture.
- **Collapse all (double Escape):** two-tone descent — 1200 Hz then 400 Hz, about half a second total. Distinct from the single-group chirps so you know "I collapsed everything," not just one group.

## Adjusting the Timing

If double Escape feels too fast or too slow for you, adjust the **double-tap tolerance** in **Settings > Accessibility**. The same setting controls filter-edge bracket double-tap timing (pressing `[` or `]` twice quickly to enter filter-edge adjustment mode).

## Related Topics

- Home
- Double-Tap Tolerance
- Keyboard Reference
