# Mode Switching

JJFlexRadio supports all the standard ham radio modes. Here's how to change modes.

## Quick Mode Switching

| Key | Action |
|-----|--------|
| Alt+M | Cycle to the next mode |
| Alt+Shift+M | Cycle to the previous mode |
| Alt+U | Switch directly to USB |
| Alt+L | Switch directly to LSB |
| Alt+C | Switch directly to CW |

When you change modes, JJFlexRadio announces the new mode name.

## Available Modes

Your Flex radio supports these modes (depending on model and firmware):

- **USB** — Upper Sideband. The standard voice mode for 20 meters and above.
- **LSB** — Lower Sideband. The standard voice mode for 40 meters and below.
- **CW** — Continuous Wave. For Morse code operation.
- **AM** — Amplitude Modulation. Used on some bands, especially 10 meters.
- **FM** — Frequency Modulation. Used on VHF and some HF bands.
- **DIGU** — Digital Upper. For digital modes like FT8, PSK31, etc.
- **DIGL** — Digital Lower. For digital modes on lower sideband.
- **SAM** — Synchronous AM.
- **NFM** — Narrow FM.
- **DFM** — Digital FM.
- **RTTYu** — RTTY on upper sideband.

## Mode and Filter Interaction

Changing modes often changes the default filter width. For example, switching to CW narrows the filter, while USB/LSB uses a wider voice filter. You can always adjust filters independently after changing modes — see the Filters & DSP page.
