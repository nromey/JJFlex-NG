---
keywords:
  - collapse Home groups
  - Escape
---

# Escape and Collapse

Use `Escape` to close field groups on JJ Flexible's Home area, or cancel an unwanted action. If you are transmitting, using a JJ key layer, or making an adjustment, Escape may stop or cancel that action before it closes a group.

## Pressing Escape once

Press `Escape` once while focus is inside an expanded field group, such as digital signal processing (DSP), Audio, Receiver, Transmission, or Antenna. The group will collapse, and focus will return to its header. You will hear a short descending chirp confirming the collapse.

If focus is on JJ Flexible Home rather than inside a group, one press closes every open group. If no group is open, Escape returns focus to Home.

## Double Escape (Collapse All)

Press `Escape` twice in quick succession while working inside an expanded group to close all open field groups. Focus will return to JJ Flexible Home, and you will hear a higher tone followed by a lower one. This two-tone descent confirms that all groups are closed and Home has focus.

Use the **Double-Tap Tolerance** setting to control how close together the two presses must be to perform the close all action. The default is 500 milliseconds between presses. See [Double-Tap Tolerance](DoubleTapTolerance.md) for details.

## Why Escape Works This Way

Escape lets you close either the active group or all of Home's open groups:

- Press `Escape` once inside a group to close that group. From Home, a single press closes every open group.
- Press `Escape` twice in quick succession inside a group to close every open group and return focus to Home.

Escape can have another job before it closes a group. If a voice message or a push-to-talk transmission is being sent, Escape stops that transmission immediately. If you are adjusting a filter edge or the RIT or XIT scale, it cancels that adjustment first.

In a JJ key layer, Escape cancels the layer. In an ordinary dialog, it closes the dialog. A running Audio Check or transmit test uses the first press to stop its transmission and keeps its window open.

## Re-expanding a Collapsed Group

After closing a group with Escape, focus is on the group's header. Press `Space` to expand it again. You will hear an ascending chirp, the expand earcon. It mirrors the collapse chirp, sweeping up instead of down.

## What You Will Hear

The sounds distinguish a change to one group from a collapse of all groups:

- When you collapse one group, you will hear a chirp descending from 1200 hertz to 400 hertz over 350 milliseconds. It includes a gritty noise sweep to make it audible through radio background noise.
- When you expand one group, you will hear a chirp ascending from 400 hertz to 1200 hertz, with the same texture.
- When you collapse all groups with double Escape, you will hear two tones: 1200 hertz, then 400 hertz. They last about half a second in total. This sound differs from the single-group chirps so you can tell whether one group or all groups have collapsed.

## Adjusting the Timing

If the two presses need to be closer together or farther apart, adjust **Double-Tap Tolerance** under **Settings > Accessibility**. The same setting controls the double-tap timing for the filter-edge brackets. Pressing `[` or `]` twice quickly enters filter-edge adjustment mode.

## Related Topics

- [JJ Flexible Home](JJFlexibleHome.md)
- [Double-Tap Tolerance](DoubleTapTolerance.md)
- [Keyboard Reference](KeyboardReference.md)
