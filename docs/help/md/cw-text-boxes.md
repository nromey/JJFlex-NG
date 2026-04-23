# CW Send and Receive Text Boxes

JJ Flexible Radio Access has two text boxes for CW operation:

- **Received text** — displays decoded CW text coming from the radio.
- **Sending text** — the box you type into when you want to send CW.

These two boxes only appear while you are in the CW operating mode. In every other mode (Upper Side Band, Lower Side Band, AM, FM, and all digital modes), the CW text boxes are hidden and excluded from the tab order, so they do not clutter your navigation when you cannot use them anyway.

## Switching to CW Mode

Press `Alt+C` to switch to the CW mode. The text boxes appear and enter the tab order at that point.

If you switch to any other mode — for example, pressing `Alt+U` for Upper Side Band — the text boxes vanish automatically.

## Where They Sit in the Tab Order

When the CW text boxes are visible, they sit just below the main radio controls. Use `Tab` and `Shift+Tab` to move between them and the other radio controls.

## Why They Are Hidden Outside of CW

Most of the time the Received and Sending text boxes are not doing anything useful — they only decode and accept CW, not Upper Side Band or the digital modes. Hiding them in non-CW modes removes two extra tab stops that would otherwise slow down your navigation for no benefit. When you switch back to CW, the boxes come right back. When you switch away, they step out of the way.

## Related Topics

- Operating Modes
- Mode Switching
- Keyboard Reference
