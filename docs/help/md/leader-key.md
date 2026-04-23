# Leader Key Commands

The leader key gives you quick access to DSP toggles, meter controls, status readouts, and other frequently used features without having to memorise dozens of hotkeys. You press `Ctrl+J` and then a single letter (sometimes with Shift).

## How It Works

1. Press `Ctrl+J`. You will hear a rising "bink" tone — leader mode is now active.
2. Within about two seconds, press one of the command keys listed below.
3. The action executes, and leader mode ends automatically.

If you change your mind, press `Escape` or just wait for the timeout. You will hear a soft falling tone letting you know that leader mode has cancelled.

## DSP Toggles

| Key | Action |
|-----|--------|
| N | Toggle Legacy Noise Reduction on or off |
| Shift+N | Toggle NR Filter on or off (model-specific) |
| B | Toggle Noise Blanker on or off |
| W | Toggle Wideband Noise Blanker on or off |
| R | Toggle Neural Noise Reduction (RNN) on or off |
| Shift+R | Toggle PC-side Neural Noise Reduction on or off |
| S | Toggle Spectral Noise Reduction on or off |
| A | Toggle Auto Notch Filter on or off |
| P | Toggle Audio Peak Filter on or off (CW mode only) |

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
| Shift+T | Toggle earcon mute on or off |
| D | Toggle tuning speech debounce on or off |

## Status and Information

| Key | Action |
|-----|--------|
| L | Speak log statistics |
| M | Display Flex memory list |

## Help

| Key | Action |
|-----|--------|
| ? or H | List all leader-key commands aloud |
| Escape | Cancel leader mode |

## Audio Feedback

Every leader-key action has its own audio feedback:

- **Feature toggled on** — a two-step rising tone (bonk-bink), then speech confirming the new state: for example, "Neural NR on."
- **Feature toggled off** — a two-step falling tone (bink-bonk), then speech: "Neural NR off."
- **Information spoken** — no earcon, just speech with the requested information.
- **Invalid or unavailable key** — a dull buzz, then speech: for example, "Audio Peak Filter is CW only" or "Neural NR not available on this radio."
- **Cancelled or timed out** — a soft descending tone, no speech.

## Why a Leader Key?

The leader-key pattern is borrowed from Vim and other keyboard-driven tools. Instead of needing a unique modifier combination for every feature (which would quickly exhaust the available Ctrl, Alt, and Shift combinations), you press one "leader" chord and then a memorable letter. B for Blanker, R for RNN, S for Spectral, M for Memory — easy to remember once you have used them a few times.

**Tip:** Press `Ctrl+J`, then `?` (or `H`) to hear the full list of leader-key commands read aloud at any time. You do not need to memorise this page.
