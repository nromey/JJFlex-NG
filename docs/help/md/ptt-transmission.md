# PTT and Transmission

JJ Flexible Radio Access includes safety features designed to help you stay in control of your transmitter.

## PTT (Push to Talk)

PTT works through your radio's standard PTT input — a footswitch, a hand microphone's PTT button, or VOX (voice-operated transmit). The application monitors the PTT state and announces when you key up and when you unkey.

## TX Status

Press `Alt+Shift+S` to hear the current TX status spoken. This tells you whether you are transmitting, what your power level is, and other TX-related information in a single short announcement.

## CW Transmission

For CW operation:

- Use `Ctrl+1` through `Ctrl+7` to send pre-configured CW messages.
- Press `F12` to immediately stop CW transmission — the "panic button" for CW.
- Press `Alt+Z` to zero-beat the signal you are currently listening to.

## Tune Carrier

Press `Ctrl+Shift+T` to toggle a tune carrier on or off. When you activate the tune carrier, you will hear a chirp earcon followed by a speech announcement: "Tune on." When you turn the carrier off, you will hear "Tune off." The tune carrier puts out a steady signal at reduced power, which is useful for adjusting an antenna tuner or for checking SWR.

Meter tones automatically activate while the tune carrier is running, so you can hear your SWR and power levels in real time.

You can also find the Tune Carrier command in the menu, under **Classic > Operations > Transmission > Tune Carrier**.

## ATU Tune (Antenna Tuner)

If your radio has a built-in Antenna Tuning Unit (ATU), press `Ctrl+T` to start an ATU tune cycle. While the tuner is working, you will hear a pulsing tone. When the tune cycle finishes, you will hear either a rising arpeggio (the tuner found a good match) or a falling arpeggio (the tuner failed to find a match), followed by a speech announcement of the resulting SWR value.

You can also start an ATU tune from the menu, under **Classic > Antenna > ATU Tune**.

**Tip:** If the ATU cannot find a match, your antenna may be too far off resonance for that band. Try a manual tuner, or check your antenna system for other problems.

## Transmit Safety Features

JJ Flexible Radio Access includes several built-in transmit-safety features, each with clear audio feedback so you always know what state the radio is in:

- The PTT state is always announced, so you know the moment you go on the air.
- The `F12` key is a global emergency stop for CW transmission — it works from anywhere in the application.
- The TX status is always a single keystroke away, via `Alt+Shift+S`.
- The Tune Carrier toggle (`Ctrl+Shift+T`) has clear audio feedback — an earcon plus speech — so you always know whether the radio is radiating a carrier.

**Warning:** Always verify that you are on the correct frequency and in the correct mode before you transmit. Press `F2` at any time to hear the current frequency spoken.
