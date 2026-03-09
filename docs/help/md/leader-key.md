# Leader Key Commands

The leader key gives you quick access to DSP toggles and other frequently used features without memorizing dozens of hotkeys. Just press `Ctrl+J` and then a single letter.

## How It Works

1. Press `Ctrl+J`. You'll hear a rising "bink" tone — leader mode is active.
2. Within about 2 seconds, press one of the command keys listed below.
3. The action executes and leader mode ends automatically.

If you change your mind, press `Escape` or just wait for the timeout. You'll hear a soft falling tone letting you know leader mode has cancelled.

## Available Commands

| Key | Action |
|-----|--------|
| N | Toggle Noise Reduction on/off |
| B | Toggle Noise Blanker on/off |
| W | Toggle Wideband Noise Blanker on/off |
| R | Toggle Neural Noise Reduction (RNN) on/off |
| S | Toggle Spectral Noise Reduction on/off |
| A | Toggle Auto Notch Filter on/off |
| P | Toggle Audio Peak Filter on/off |
| M | Toggle Meter Tone on/off |
| T | Open Audio Workshop |
| F | Speak current TX filter width |
| ? or H | List all leader key commands |
| Escape | Cancel leader mode |

## Audio Feedback

Every leader key action has audio feedback:

- **Feature toggled on:** A two-step rising tone (bonk-bink), then speech: "Noise Reduction on"
- **Feature toggled off:** A two-step falling tone (bink-bonk), then speech: "Noise Reduction off"
- **Invalid key:** A dull buzz, then speech: "Unknown command"
- **Cancelled/timeout:** A soft descending tone, no speech

## Why a Leader Key?

The leader key pattern is borrowed from Vim and other keyboard-driven tools. Instead of needing a unique modifier combination for every DSP toggle (which would quickly exhaust the available Ctrl+Alt+Shift combinations), you press one "leader" combo and then a memorable letter. N for Noise Reduction, B for Blanker, A for Auto Notch — easy to remember.

**Tip:** Press `Ctrl+J` then `?` (or `H`) to hear the full list of leader commands at any time. You don't need to memorize this page.
