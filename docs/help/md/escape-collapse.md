# Escape and Collapse

The Escape key is your "back out" key in JJ Flexible Radio Access. It behaves differently depending on what you are doing and on whether you press it once or twice.

## Single Escape

Press `Escape` once while focus is inside an expanded field group (DSP, Audio, Receiver, Transmission, or Antenna). The group collapses, focus moves to the group's header, and you will hear a short descending chirp confirming the collapse.

If you are not inside an expanded group — for example, you are already in the JJ Flexible Home — a single Escape simply returns focus to the JJ Flexible Home, the same destination that `F2` takes you to.

## Double Escape (Collapse All)

Press `Escape` twice in quick succession. All open field groups collapse at once, focus returns to the JJ Flexible Home, and you will hear the "two-tone descent" earcon — a higher tone followed by a lower one — signalling "everything is closed, you are back at Home."

"Quickly" in this context is controlled by your Double-Tap Tolerance setting (see the Double-Tap Tolerance help topic). The default tolerance is 500 milliseconds between presses.

## Why Escape Works This Way

Escape as a "back out" key works at two scales inside JJ Flexible Radio Access:

- A single Escape backs you out of the current group — a local scope.
- Two Escapes in quick succession back you out of everything — a global scope.

The same key carries the same underlying meaning, applied at two different levels of reach. You learn the rule once, and it applies everywhere in the application.

## Re-expanding a Collapsed Group

After an Escape-collapse, focus sits on the group's header. Press `Space` to re-expand the group. You will hear the ascending "expand" earcon, which mirrors the collapse chirp — the tone sweeps up instead of down.

## What You Will Hear

- **Collapsing one group** — a descending chirp from 1200 Hz down to 400 Hz over 350 milliseconds, with a gritty noise sweep that cuts through radio ambient noise.
- **Expanding one group** — an ascending chirp, 400 Hz up to 1200 Hz, using the same texture.
- **Collapsing all groups (double Escape)** — a two-tone descent: 1200 Hz, then 400 Hz, about half a second in total. The sound is deliberately different from the single-group chirps, so you always know whether you have collapsed one group or all of them.

## Adjusting the Timing

If double Escape feels too fast or too slow for you, adjust the **Double-Tap Tolerance** setting under **Settings > Accessibility**. The same tolerance value also controls filter-edge bracket timing — the double-tap you use when you press `[` or `]` twice quickly to enter filter-edge adjustment mode.

## Related Topics

- JJ Flexible Home
- Double-Tap Tolerance
- Keyboard Reference
