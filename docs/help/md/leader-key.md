# JJ Key Commands

The JJ key — `Ctrl+J`, J for JJ — gives you quick access to DSP toggles, audio controls, meter controls, status readouts, and other frequently used features of JJ Flexible Radio Access without having to memorise dozens of hotkeys. You press `Ctrl+J` and then a single letter (sometimes with Shift). The set of commands it opens up is the JJ Command layer.

## How It Works

1. Press `Ctrl+J`. You will hear a rising "bink" tone — the JJ Command layer is now open.
2. Press one of the command keys listed below.
3. The action executes, and the layer closes automatically.

If you change your mind, press `Escape`. You will hear a soft falling tone letting you know that the JJ Command layer has closed. The layer waits patiently until you press a key or cancel — there is no timer sneaking you out of it.

## DSP Toggles

Two of these come in on-radio and PC flavours, and the names now say so. "On-Radio" noise reduction runs in the radio's own DSP hardware (8000-series and Aurora only). "PC" noise reduction runs inside JJ Flexible on this computer and works on every radio.

| Key | Action |
|-----|--------|
| N | Toggle Legacy Noise Reduction on or off |
| Shift+N | Toggle NR Filter on or off (model-specific) |
| B | Toggle Noise Blanker on or off |
| W | Toggle Wideband Noise Blanker on or off |
| R | Toggle On-Radio Neural Noise Reduction (RNN) on or off |
| S | Toggle On-Radio Spectral Noise Reduction (NRS) on or off |
| Shift+R | Toggle PC Neural Noise Reduction on or off (runs on your computer, every radio) |
| Shift+S | Toggle PC Spectral Noise Reduction on or off (runs on your computer, every radio) |
| Q | Capture a noise profile for PC Spectral NR — Q for "quiet." Press Q again while it runs to cancel. See the PC-Side Noise Reduction help page. |
| A | Toggle Auto Notch Filter on or off |
| P | Toggle Audio Peak Filter on or off (CW mode only) |

## Audio and Transmit

| Key | Action |
|-----|--------|
| V | Enter volume mode — pick a target with one letter, ride Up and Down, Escape exits |
| K | Mic check — speak your mic-audio verdict and level, nothing else |
| G | Arm or disarm the TX test tone (replaces your microphone while transmitting) |
| C | Toggle Compander on or off |
| Ctrl+A | Turn PC audio on or off — whether radio audio plays through this computer at all. It reads the radio back before answering, so if turning it on could not find a sound device it says that rather than claiming success. `V` then `P` rides how loud it plays; this is the switch |
| Shift+P | Toggle Speech Processor on or off |

## Filter Information

| Key | Action |
|-----|--------|
| F | Speak the current TX filter width |
| Shift+F | Speak the current RX filter width |
| Ctrl+F | Open the direct frequency-entry box |

## Meter and Tuning

| Key | Action |
|-----|--------|
| T | Toggle meter tones on or off |
| Shift+T | Toggle alert sounds (earcons) on or off |
| D | Toggle tuning speech debounce on or off |

## Status, Information, and Slices

| Key | Action |
|-----|--------|
| O | Say what is still running and what it is costing — recording, captures, meter tones. O for "what's on" |
| L | Speak log statistics |
| M | Display Flex memory list |
| Shift+A through Shift+H | Jump straight to that slice from anywhere (Shift+F is reserved for the RX filter readout) |

## Help

| Key | Action |
|-----|--------|
| ? or H | List all JJ Command layer commands aloud |
| Escape | Close the JJ Command layer |

## Audio Feedback

Every JJ key action has its own audio feedback:

- **Feature toggled on** — a two-step rising tone (bonk-bink), then speech confirming the new state: for example, "On-Radio Neural NR on."
- **Feature toggled off** — a two-step falling tone (bink-bonk), then speech: "On-Radio Neural NR off."
- **Information spoken** — no earcon, just speech with the requested information.
- **Invalid or unavailable key** — a dull buzz, then speech: for example, "Audio Peak Filter is CW only" or "On-Radio Neural NR not available on this radio."
- **Cancelled** — a soft descending tone.

## Why a JJ Key?

If you use JAWS or NVDA, you already know the idea as layered keystrokes — press one key, then a second key that does the work. Instead of needing a unique modifier combination for every feature (which would quickly exhaust the available Ctrl, Alt, and Shift combinations), you press the JJ key and then a memorable letter. B for Blanker, R for RNN, S for Spectral, Q for quiet, M for Memory — easy to remember once you have used them a few times.

**Tip:** Press `Ctrl+J`, then `?` (or `H`) to hear the full list of JJ Command layer commands read aloud at any time. You do not need to memorise this page.
