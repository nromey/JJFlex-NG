# CW Send and Receive Text Boxes

JJ Flex has two text boxes for CW operation:

- **Received text** — displays decoded CW text coming from the radio.
- **Sending text** — where you type CW to transmit.

These boxes appear only when you're in a CW mode (CW, CWL, or CWU). In other modes (USB, LSB, AM, FM, and all digital modes), the boxes are hidden and excluded from the tab order so they don't clutter your navigation.

## Switching to CW Mode

Press `Alt+C` to switch to CW mode. The text boxes appear and enter the tab order.

Switch to any other mode (for example, `Alt+U` for USB) and the boxes vanish automatically.

## Where They Live in the Tab Order

When visible, they appear below the main radio controls. Use `Tab` and `Shift+Tab` to move between them and the other radio controls.

## Why They're Hidden Outside CW

Most of the time, the received and sending text boxes aren't doing anything useful — they only decode and accept CW, not SSB or digital modes. Hiding them in non-CW modes removes two extra tab stops that would otherwise slow down your navigation for no benefit. When you switch to CW, they're there. When you switch away, they step out of the way.

## Related Topics

- Operating Modes
- Mode Switching
- Keyboard Reference
