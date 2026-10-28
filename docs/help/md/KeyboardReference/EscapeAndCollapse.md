---
keywords:
  - collapse Home groups
  - Escape
---

# Escape and Collapse

The Escape key is your "back out" key in JJ Flexible Radio Access. On Home it closes field groups, but a transmission, a JJ key layer, or an adjustment you are making may need Escape first.

## Single Escape

Press `Escape` once while focus is inside an expanded field group (DSP, Audio, Receiver, Transmission, or Antenna). That group collapses, focus moves to its header, and you will hear a short descending chirp confirming the collapse.

If focus is on the JJ Flexible Home rather than inside a group, one press closes every open group. If no group is open, Escape returns focus to Home.

## Double Escape (Collapse All)

Press `Escape` twice in quick succession while working inside an expanded group. All open field groups collapse at once, focus returns to the JJ Flexible Home, and you will hear the "two-tone descent" earcon — a higher tone followed by a lower one — signalling "everything is closed, you are back at Home."

"Quickly" in this context is controlled by your Double-Tap Tolerance setting (see the Double-Tap Tolerance help topic). The default tolerance is 500 milliseconds between presses.

## Why Escape Works This Way

For Home's field groups, Escape works at two scales:

- A single Escape closes the group you are in. From Home, it closes every open group.
- Two Escapes in quick succession from inside a group close every open group and return focus to Home.

Escape can have another job before it closes a group. If a voice message, a recording you are sending, or a push-to-talk transmission is running, Escape stops that transmission first. If you are adjusting a filter edge or the RIT or XIT scale, it cancels that adjustment first. In a JJ key layer, Escape cancels the layer; in an ordinary dialog, it closes the dialog. A running check in Check your audio, or a transmit test, uses the first press to stop its transmission and keeps its window open.

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
