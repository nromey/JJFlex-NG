# PC-Side Noise Reduction

Every Flex radio has noise reduction built into the radio's own firmware. JJ Flexible Radio Access adds a second layer of noise reduction that runs on your computer, separately from the radio. Both layers are useful, they are not the same thing, and knowing which one to reach for makes a real difference.

## Two Noise Reduction Layers, Two Different Purposes

- **On-radio NR** (the Flex's built-in NR, NRS, RNN, and similar features) runs inside the radio's own DSP hardware. It is fast, it does not touch your computer's CPU, and it works whether JJ Flexible Radio Access is running or not. Use it as your first stop — it is always available, and for most conditions it is enough. In JJ Flexible these controls all say "On-Radio" in their names so you always know which side of the wire you're adjusting.
- **PC-side NR** runs inside JJ Flexible Radio Access on your computer. It processes the audio stream after the audio leaves the radio. It is free, it is available on every radio model regardless of license tier — including the 6300 and 6400 that never had the fancy DSP hardware — and it gives you two different engines.

## The Two PC-Side Engines

- **PC Neural NR** — a neural-network-trained engine designed to reduce wideband noise while preserving voice. This is a good first-choice engine for voice modes and it needs no setup at all. It has a strength control (how much of the cleaned audio you hear versus the original), and a "Voice Modes Only" switch that steps it aside automatically in CW and digital modes, where a speech-trained network does more harm than good.
- **PC Spectral NR** — a classic DSP approach: you capture a few seconds of your band's noise, and the engine subtracts that exact noise from everything you hear afterward. It shines on steady-state noise — a switching power supply, a grow-light, your neighborhood's persistent hash. It has a strength control and a floor control (how much of the original audio always survives, the guard against watery "musical noise" artifacts). **Spectral NR does nothing until it has a noise profile** — capturing one is the whole trick, and it takes one keystroke.

You can run both engines at once — spectral subtraction first, then the neural cleanup — and you can stack either or both on top of the radio's own NR. When both PC engines are on, gentler settings work better than either engine's solo defaults; the Noise Profiles dialog has an "Apply Recommended Levels" button that sets the right values for whatever you have switched on.

## Capturing a Noise Profile

1. Tune to a quiet spot on the band — no signals, just your noise. Stay unmuted, with PC audio on (the capture listens to the radio audio playing through this computer).
2. Press `Ctrl+J`, then `Q` — Q for "quiet." Or press the Capture Noise Profile button in the DSP field group (`Ctrl+Shift+N`), or use Slice menu, DSP, PC Noise Reduction, Capture Noise Profile.
3. Listen. The capture announces itself, counts the seconds out loud as they pass, and finishes with "Noise profile captured" — plus whether Spectral NR is on and using it. Press `Q` again mid-capture (or the same button) to cancel.
4. Turn on PC Spectral NR if it isn't already: `Ctrl+J`, then `Shift+S`.

The capture runs three seconds by default (adjustable from 1 to 5 in the Noise Profiles dialog). A finished capture saves itself automatically and loads again on your next connect — no save dialog in the way, and no more "no noise profile loaded" greeting every session.

If the band's character changes — different band, different antenna, the storm passed — just capture again. Three seconds, one keystroke.

## Saving and Managing Profiles

The Noise Profiles dialog (Slice menu, DSP, PC Noise Reduction, Noise Profiles — or the Noise Profiles button in the DSP field group) is the whole room: both engines' switches and strengths, the capture duration, the capture button, and your saved profiles.

- Profiles carry a name plus the band and antenna they were captured on — "20m, ANT1, captured 2026-08-11" is how the list reads them back — because that is how you'll remember which one you want in March.
- **Save Current As** keeps a capture under a name of your own; the name box suggests your current band and antenna.
- **Load Selected Profile** brings a saved profile back. Whatever you load (or capture) is remembered and reloaded automatically next session.
- **Clear Loaded Profile** empties the engine's memory.
- **Open Profiles Folder** opens the folder in File Explorer — profiles live at `%AppData%\JJFlexRadio\NoiseProfiles`, one small file each, so renaming, deleting, backing up, or sending one to a friend with similar noise is ordinary file work.

## Where the Controls Live

Everything comes in threes, as usual:

- **Hotkeys:** `Ctrl+J, Shift+R` (PC Neural NR), `Ctrl+J, Shift+S` (PC Spectral NR), `Ctrl+J, Q` (capture).
- **The DSP field group** (`Ctrl+Shift+N`): checkboxes for both engines, and when one is on, its strength (and floor, and voice-only) fields appear right under it. The capture button and a noise-profile readout live there too — arrow to the readout to hear which profile is loaded.
- **The menu:** Slice menu, DSP, PC Noise Reduction has the toggles, the capture, the Noise Profiles dialog, and Open Noise Profiles Folder.

Every control speaks its value as you change it, and all of it — switches, strengths, floor, the loaded profile — is remembered across sessions.

## When PC-Side NR Earns Its Keep

- Your radio does not have the premium DSP hardware, but your computer has CPU cycles to spare.
- You have a steady local noise source that the radio's NR only smears — capture it, subtract it.
- You want a different sonic character than what the radio's firmware NR offers. PC Neural NR in particular sounds noticeably different from Flex's RNN.

## What PC-Side NR Will Not Do

PC-side NR runs on the audio stream after it reaches your computer — it cannot make a weak signal louder. For weak-signal work, reach for the radio's AGC, preselector, and filter settings first. PC-side NR is a polish stage, not a signal-lifter. And since it lives in the PC audio path, it only shapes what you hear through this computer — the radio's own speaker and headphone jacks play unprocessed audio.
