# PTT and Transmission

JJ Flexible Radio Access includes safety features to help you stay in control of your transmitter.

## PTT (Push to Talk)

PTT works through your radio's standard PTT input — footswitch, hand mic, or VOX. The app monitors PTT state and announces when you key up and unkey.

## TX Status

Press `Alt+Shift+S` to hear the current TX status. This tells you whether you're transmitting, your power level, and other TX-related information.

## CW Transmission

For CW operation:

- Use `Ctrl+1` through `Ctrl+7` to send pre-configured CW messages.
- Press `F12` to immediately stop CW transmission (panic button).
- Press `Alt+Z` to zero-beat the current signal.

## Tune Carrier

Press `Ctrl+Shift+T` to toggle a tune carrier on or off. When you activate the tune carrier, you'll hear a chirp earcon and a speech announcement: "Tune on." When you turn it off: "Tune off." The tune carrier puts out a steady signal at reduced power, which is useful for adjusting your antenna tuner or checking SWR.

Meter tones automatically activate while the tune carrier is on, so you can hear your SWR and power levels in real time.

You can also find this in the menu at **Classic > Operations > Transmission > Tune Carrier**.

## ATU Tune (Antenna Tuner)

If your radio has a built-in antenna tuner (ATU), press `Ctrl+T` to start an ATU tune cycle. While the tuner is working, you'll hear a pulsing tone. When it finishes, you'll hear either a rising arpeggio (success) or a falling arpeggio (failed to find a match), followed by a speech announcement of the SWR value.

You can also start an ATU tune from the menu at **Classic > Antenna > ATU Tune**.

**Tip:** If the ATU can't find a match, your antenna may be too far off resonance for that band. Try a manual tuner or check your antenna.

## TX Safety

JJ Flexible Radio Access has built-in transmission safety features:

- PTT state is always announced so you know when you're on the air.
- The `F12` key is a global emergency stop for CW transmission.
- TX status is always available via `Alt+Shift+S`.
- Tune carrier toggle (`Ctrl+Shift+T`) has clear audio feedback so you always know when you're radiating.

**Warning:** Always verify you're on the correct frequency and mode before transmitting. Press `F2` to hear the current frequency.
