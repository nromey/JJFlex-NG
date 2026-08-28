# JJ Key Commands

The JJ key — `Ctrl+J`, J for JJ — gives you quick access to DSP toggles, audio controls, meter controls, status readouts, and other frequently used features of JJ Flexible Radio Access without having to memorise dozens of hotkeys. You press `Ctrl+J` and then a single letter (sometimes with Shift). The set of commands it opens up is the JJ Command layer.

## How It Works

1. Press `Ctrl+J`. You will hear a rising "bink" tone — the JJ Command layer is now open.
2. Press one of the command keys listed below.
3. The action executes, and the layer closes automatically.

If you change your mind, press `Escape`. You will hear a soft falling tone letting you know that the JJ Command layer has closed. The layer waits patiently until you press a key or cancel — there is no timer sneaking you out of it.

<!-- LEADER-KEY-TABLE: every row from here to the END marker is checked against KeyInventory.LeaderCommands by Radios.Tests/LeaderDocCoverageTests. Add a chord to the layer and these tables fail until it has a row; delete a chord and a leftover row fails too. The wording is yours — only the set of keys is checked. -->

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
| E | Re-send the CW notifications you just heard — E for echo. Press E again to step back to an earlier one |

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
| Ctrl+Q | Start or stop the QSO signal analyzer — watch the S-meter, then hear what the signal did, QSB and all. Saved captures live under Tools, Signal captures |

## Status, Information, and Slices

| Key | Action |
|-----|--------|
| O | Say what is still running and what it is costing — recording, captures, meter tones. O for "what's on" |
| Ctrl+D | Start or stop a detailed capture of what the app is doing. Works with no radio connected, and from inside any dialog. Stopping saves the capture as its own session in Saved Diagnostic Logs |
| Ctrl+R | Read the problems recorded this session — everything that has gone wrong since you started, in case you missed an announcement |
| Alt+V | Speak the version, the build type and the date this copy was built. The answer to "which build are you on?" without leaving what you are doing |
| L | Speak log statistics |
| M | Display Flex memory list |
| Shift+A through Shift+H | Jump straight to that slice from anywhere (Shift+F is reserved for the RX filter readout) |

## Help

| Key | Action |
|-----|--------|
| ? or H | List all JJ Command layer commands aloud |
| Escape | Close the JJ Command layer |

<!-- END LEADER-KEY-TABLE -->

## When the JJ layer doesn't know the key you pressed

Press `Ctrl+J` and then something the layer has no command for, and you'll hear a buzz followed by "Unknown key. H for the list, Escape to cancel." Turn verbosity up and it spells the whole thing out: "Unknown key. Press H, or Shift slash, for the list of JJ key commands. Escape to cancel."

Here's the part that matters, because it used to be a small lie. That sentence tells you to press `H` — and until now, by the time it finished saying so, the layer had already closed behind you. `H` did whatever `H` does wherever you happened to be standing. You were told to do the one thing that couldn't work.

Now the layer waits. After an unknown key it stays open for exactly three keys: `H` and `Shift slash`, which both read you the command list, and `Escape`, which closes it. Press any other key and the layer is already gone — that key does the ordinary thing it always does, nothing swallowed, nothing surprising.

Three keys and no more, on purpose. A layer that keeps hold of your keyboard without saying so is a trap, and you can't see whether you're still in one. These three all lead *out*: two to the list, one to the door. There's no way to get stuck, and the layer only ever lingers right after it has told you it's doing so.

A note on how these keys get spoken. You'll notice the app says "Shift slash" rather than "question mark" or the bare `?` character. That's deliberate. If your screen reader's punctuation level is set low, a lone `?` may not be spoken at all — so an instruction can quietly lose the very key it's naming. "Shift slash" also just describes what your hands do, which is the whole job of an instruction. You'll see the same wording anywhere the app asks you to press a punctuation key.

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
