---
keywords:
  - frequency
  - tuning
  - tuning debounce
  - refused tuning step
  - locked slice
---

# Tuning and Frequency

## Hearing the Current Frequency

Press `F2` at any time to hear the current frequency spoken. The announcement includes the band and the exact frequency.

## Direct Frequency Entry

Press `Ctrl+F` to open the frequency entry box. Type a frequency in megahertz (for example, `14.225` or `7.040`) and press `Enter`. JJ Flexible Radio Access tunes to the frequency you entered immediately.

You can also enter frequencies in kilohertz — for example, `14225` is the same as `14.225`.

## Tuning with the Keyboard

Every field on the Home slice row accepts `Ctrl+Up` and `Ctrl+Down` to tune up or down by the coarse step, and `Shift+Up` and `Shift+Down` to tune up or down by the fine step. Both pairs work in Classic tuning mode and Modern tuning mode, including on the Frequency field.

Plain `Up` and `Down` on the Frequency field use the coarse step in Modern tuning mode and the digit under the cursor in Classic tuning mode. Plain `Up` and `Down` on the Slice and Slice Operations fields use the coarse step in both tuning modes.

The commands **Tune up by your coarse step**, **Tune down by your coarse step**, **Tune up by your fine step** and **Tune down by your fine step** also appear in the Command Finder and the Hotkey Editor without default keys. These commands tune the active slice; assign a key in the Hotkey Editor for access beyond the Home slice row.

A tuning step refused because the slice is locked or the radio is transmitting leaves the frequency unchanged and says nothing. JJ Flexible does not announce a frequency as though the tune had succeeded.

## RIT (Receiver Incremental Tuning)

Receiver Incremental Tuning (RIT) lets you offset the receive frequency slightly from the transmit frequency. This is useful when the station you are listening to is slightly off frequency from where you are transmitting.

Press `Ctrl+Shift+C` to clear the RIT offset and return the receive frequency to zero offset.

## CW Zero Beat

When you are operating CW, press `Alt+Z` to zero-beat the signal you are currently listening to. Zero-beating adjusts your receive frequency so that the received CW tone matches your sidetone pitch — the two tones line up perfectly, and the signal is exactly on your calling frequency.

## Tuning Speech Debounce

When you are tuning rapidly with the arrow keys, hearing every single frequency step spoken can be overwhelming. The tuning speech debounce solves this — it waits until you have stopped tuning, and then speaks only the final frequency you landed on.

You can toggle tuning debounce on or off with the JJ key — press `Ctrl+J`, then press `D`. You will hear "Tuning debounce on" or "Tuning debounce off" depending on the new state.

You can also configure tuning debounce under **Settings > Tuning**, where you will find a checkbox to enable or disable it, along with a field to set the delay in milliseconds. The delay controls how long the application waits after your last keystroke before speaking the final frequency.

## Memory Channels

Press `Ctrl+J` then `Alt+M` to open the memory channel list, where you can save and recall favorite frequencies. (`Ctrl+M` on its own opens the meters panel.)

Memory scan has no key of its own. `Ctrl+Shift+M` switches tuning mode and always has, so memory scan ships unbound — reach it from Command Finder (`Ctrl+/`), or give it any free key you like in the Hotkey Editor.

## Related Topics

- [Band Navigation](BandNavigation.md)
- [Settings and Profiles](Settings.md)
