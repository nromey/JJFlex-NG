# Mode Switching

JJ Flexible Radio Access supports all the standard ham radio modes. Here's how to change modes within the application.

## Quick Mode Switching

| Key | Action |
|-----|--------|
| Alt+M | Cycle to the next mode  in the list of standard Flex Radio  supported modes |
| Alt+Shift+M | Cycle to the previous mode in the list of suipported Flex Radio supported modes |
| Alt+U | Switch to Upper Side Band (USB) |
| Alt+L | Switch to Lower Side Band (LSB) |
| Alt+C | Switch to the CW mode |
| Alt+A | Switch to the AM mode |
| Alt+F | Switch to the FM mode |
| Alt+D | Switch to Digital Upper (DIGU) |
| Alt+Shift+D | Switch to Digital Lower (DIGL) |

When you change modes, JJ Flexible Radio Access announces the new mode name.

## Available Modes

Your Flex radio supports these modes (depending on the model and firmware):

- **USB** — Upper Side Band. The standard voice mode for 20 meters and above.
- **LSB** — Lower Side Band. The standard voice mode for 40 meters and below.
- **CW** — Continuous Wave. The mode for Morse code operation.
- **AM** — Amplitude Modulation. Used on some bands, especially 10 meters.
- **FM** — Frequency Modulation. Used on VHF and some HF bands.
- **DIGU** — Digital Upper Side Band. For digital modes like FT8 and PSK31 that run on upper sideband.
- **DIGL** — Digital Lower Side Band. For digital modes that run on lower sideband.
- **SAM** — Synchronized AM. An enhanced AM demodulation that locks to the carrier for clearer audio on fading signals.
- **NFM** — Narrow FM. A narrower-bandwidth variant of FM.
- **DFM** — Digital FM.
- **RTTYU** — Radio Teletype on upper sideband.

## Mode and Filter Interaction

Changing modes often changes the default filter width. For example, switching to CW  generally uses narrower filters because a CW signal generally occupies less space on the spectrum. The narrower filter allows an operator to hear one CW signal in a potentially crowded band and can also help an operator hear a clearer signal on a noisy band. Selecting USB and LSB will by default, selects a  wider filter to acccommodate voice modulation's wider band.  You can always adjust the filters independently after changing modes — see the Filters and DSP help page for details on adjusting filter width and characteristics.
