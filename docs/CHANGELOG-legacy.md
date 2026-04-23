# JJ Flexible Radio Access — Legacy Changelog (versions 1.x through 3.x)

This file preserves the changelog entries authored by Jim Shaffer for versions 1.2.1 through 3.1.12.25, covering the years before Noel Romey took over the project. Jim wrote these entries himself; they're preserved as he authored them, with only HTML-to-markdown conversion applied.

For the current changelog (version 4.x and later), see [CHANGELOG.md](CHANGELOG.md).

## Version 3.1.12.25

- You may now specify the S-meter to be shown in dBm rather than S-units. See the "SMeter display" item in the Operations menu.

## Version 3.1.12.24

- Fixed receive antenna specification. The Ant field has been renamed to TXAnt. Note that currently the receive antenna must be specified independently of the main antenna. For example, if you specify Ant2 as the TXAnt, you must also change the RXAnt to Ant2.

## Version 3.1.12.16

- Added a feature to CW sending to send characters when typed rather than waiting for a space. It's requested by using Ctrl+F4 to go to the send text area.

## Version 3.1.12.8

- I added a discussion of how to move JJ Flexible Radio Access to another computer.
- The actions menu has been updated.
- Prior to version 3.1.12.5, the program created a tx profile named JJRadioDefault. This had the unfortunate effect of setting the output power to 0, for example. Versions 3.1.12.5 and beyond use the Default tx profile in this instance. If you've been using a version of this program at 3.1.12, prior to 3.1.2.5, you might want to make the tx profile named "Default" the default tx profile by updating it and selecting the default checkbox, then make it the current profile by selecting it or just pressing enter on the list entry. In any case, you'll want to at least check your output and tune power levels.
- I've added a section on Sending CW. It mentions a new feature, CW buffering.
- You may now control the state of the transmit-related jacks on the rear panel, see Transmit Controls found in the Operations menu.

## Version 3.1.12

- The slice fields on the left end of the main display now indicate the transmit slice with a capital letter.
- With the cursor on the origin VFO's letter in the VFO field, entering a VFO number, 0 through the max VFO number, will copy the origin VFO to that destination VFO.
- Added support for multiple radio users — see the Rig Sharing section.
- "Remote audio" is now known as "PC audio". It may be toggled off and on even if connected over the network.
- Added a section with general information about the Flex radio.
- Added a "Profile Management" item to the Actions menu.

## Version 2.5.1

- The new version number, 2.5.1 in this case, indicates the minimum FlexLib version supported.
- The FlexKnob NextVFO function now is only valid when in Transceive mode. It changes both the receive and transmit VFOs to the next VFO, and mutes the old VFO and unmutes the new one.
- Made changes to better support the NVDA screen reader.
- Added the PATemp (PA temperature in °C) and Volts fields.
- Replaced the Preamp field with the panadapter's RF gain. The associated gain values and increments are rig dependent.
- Added the ability to only lock the flexknob's tuning without disabling the other knob functions.

## Version 1.3.3

- The on/off status of the FlexControl knob is now reported on the status line.
- A reboot capability has been added — see The Operations Menu.

## Version 1.3.2

- When going to a frequency from the pan adapter, the frequency is no longer rounded.

## Version 1.3.1

- Attention: this version of JJ Flexible Radio Access works with FlexLib version 2.5.1. It will not work with 2.4.9.
- A memory no longer specifies the power.

## Version 1.2.1

- Attention: this version of JJ Flexible Radio Access works with FlexLib version 2.4.9. It will not work with 2.5.1.
- Reconnecting to the radio without restarting JJ Flexible Radio Access now works.
- You may specify a radio to be auto-connected when the program is brought up.
