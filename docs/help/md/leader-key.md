# Leader Key Commands

The leader key gives you quick access to DSP toggles, meter controls, status readouts, and other frequently used features without memorizing dozens of hotkeys. Just press `Ctrl+J` and then a single letter.

## How It Works

1. Press `Ctrl+J`. You'll hear a rising "bink" tone — leader mode is active.
2. Within about 2 seconds, press one of the command keys listed below.
3. The action executes and leader mode ends automatically.

If you change your mind, press `Escape` or just wait for the timeout. You'll hear a soft falling tone letting you know leader mode has cancelled.

## Available Commands

### DSP Toggles

| Key | Action |
|-----|--------|
| N | Toggle Neural Noise Reduction (RNN) on/off |
| B | Toggle Noise Blanker on/off |
| A | Toggle Audio Peak Filter (APF) on/off — CW mode only |

### Meter Controls

| Key | Action |
|-----|--------|
| M | Toggle meter tones on/off |
| E | Toggle meter tones on/off (alias for M) |
| P | Cycle meter preset (RX Monitor, TX Monitor, Full Monitor) |
| R | Speak current meter readings |

### Status and Information

| Key | Action |
|-----|--------|
| S | Speak full status (frequency, mode, band, slice — multi-slice aware) |
| F | Speak current TX filter width |
| L | Speak log statistics |

### Tuning and Audio

| Key | Action |
|-----|--------|
| W | Open Audio Workshop |
| T | Cycle tuning step size |
| D | Toggle tuning speech debounce on/off |

### Help

| Key | Action |
|-----|--------|
| ? or H | List all leader key commands |
| Escape | Cancel leader mode |

## Audio Feedback

Every leader key action has audio feedback:

- **Feature toggled on:** A two-step rising tone (bonk-bink), then speech: "Neural NR on"
- **Feature toggled off:** A two-step falling tone (bink-bonk), then speech: "Neural NR off"
- **Status spoken:** No earcon, just speech with the requested information
- **Invalid key:** A dull buzz, then speech: "Unknown command"
- **Cancelled/timeout:** A soft descending tone, no speech

## Why a Leader Key?

The leader key pattern is borrowed from Vim and other keyboard-driven tools. Instead of needing a unique modifier combination for every feature (which would quickly exhaust the available Ctrl+Alt+Shift combinations), you press one "leader" combo and then a memorable letter. N for Neural NR, B for Blanker, M for Meters, S for Status — easy to remember.

**Tip:** Press `Ctrl+J` then `?` (or `H`) to hear the full list of leader commands at any time. You don't need to memorize this page.
