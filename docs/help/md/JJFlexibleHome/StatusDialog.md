---
keywords:
  - status dialog
  - radio status
  - tuning mode
  - network identity
  - copy status
  - undelivered details
---

# Status Dialog

Use the Status dialog to read the radio's current operating state, check its network identity, or copy a status report. Press `Ctrl+Alt+S` to open the **Radio Status** window in JJ Flexible Radio Access. It also opens without a connected radio.

For a spoken status report without opening a window, press `Ctrl+Shift+S`. This reads the slice status and current tuning information without moving focus.

## Reading the Status

Focus starts in the **Radio status information** text box. Use the arrows to read it line by line, or select and copy part of the text. The text is read-only; reading or selecting it does not change the radio's settings.

The readout has section headings within the text. Depending on the radio and the information available, it includes:

- **Radio** gives the model, the radio's name if set, and whether the connection is local or through SmartLink.
- **At connect** holds the fuller details behind the connection announcement, such as the slice count and profile results.
- **Operating** gives the Classic or Modern tuning mode and the active filter preset. In Modern mode, the tuning information includes the coarse and fine steps. If the filter does not match a named preset, the readout says so.
- **Slices** describes the available slices, including their frequencies and modes, which is selected for receiving, and which is selected for transmitting. With multiple slices, it also reports mute and pan settings.
- **Meters** gives the S-meter reading while receiving. While transmitting, it reports forward power and the standing wave ratio (SWR), with transmit (TX) drive and microphone information when readings are available.
- **Transmit** says whether the radio is receiving or transmitting and identifies the transmit slice when available.
- **Antenna Tuner** gives the tuner type when the radio has an automatic antenna tuner (ATU).

## Checking the Network Identity

Press `Tab` to reach the **Network identity** text box, then use the arrows to read its lines. It gives the model, serial number and firmware version when the radio reports them, followed by details of the connection.

For a local connection, these details include the radio's address and whether it uses an automatic or fixed address. For a SmartLink connection, they include the public address, the connection path and any saved reachability-test result from this session. Opening the dialog does not start a network test.

## Checking for Missed Details

The **Undelivered details** button opens a list of information the application could not deliver. A separate summary reports its current state. This button works even after a radio disconnects or when no radio is connected.

## Copying and Closing the Status

Press the **Copy to Clipboard** button to copy the status readout and network identity together, with the date and time the copy was made. This is useful when keeping a record of a problem or asking for help.

Press the **Close** button or `Escape` to close the dialog.

## When the Readings Refresh

The dialog refreshes every 5 seconds. While the status text has focus, its contents stay unchanged so the caret does not move during reading. Move focus to another control to allow the next refresh to update it. The network identity text has the same protection while it has focus.

The undelivered-details summary refreshes separately, including while the status text is being read.

## Related Topics

- [JJ Flexible Home](JJFlexibleHome.md)
- [Meter Tones](MeterTones.md)
- [Network Diagnostics](NetworkDiagnostics.md)
- [Diagnostic Log](DiagnosticLog.md)
