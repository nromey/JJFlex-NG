# Meter Tones and the Meters Panel

Meter tones give you audio feedback for signal strength, power output, SWR, ALC, and more — no need to look at a visual meter. The Meters Panel lets you configure exactly what you hear and how.

## Opening the Meters Panel

Press `Ctrl+M` to open (or close) the Meters Panel. When it's open, you'll see a set of meter slots you can configure. Each slot maps a radio meter to an audio tone.

You can also toggle meter tones quickly with the leader key: press `Ctrl+J` then `M` (or `E`).

## How Meter Tones Work

When meter tones are enabled, JJ Flexible Radio Access converts meter readings into audio tones. The pitch rises and falls with the meter reading, giving you an intuitive sense of signal strength, power output, or whatever meter you're monitoring.

## Meter Slots

The Meters Panel supports up to 8 meter slots. Each slot has these settings:

- **Meter source** — What the slot monitors: S-meter, forward power, ALC, SWR, compression, mic level, and others.
- **Waveform type** — The shape of the audio tone: Sine (smooth), Square (buzzy), or Sawtooth (bright and edgy). Different waveforms make it easier to distinguish multiple meters playing at the same time.
- **Stereo pan** — Where the tone plays: Left, Center, or Right. Panning different meters to different ears helps you keep them apart.
- **Base frequency** — The base pitch of the tone in Hertz. The actual pitch varies up and down from this as the meter reading changes.
- **Enabled checkbox** — Turn individual slots on or off without deleting them.

Use the **Add** button to create a new slot and the **Remove** button to delete one. The **Test** button plays a 2-second preview of that slot's tone so you can hear what it will sound like before you go on the air.

## Meter Presets

Rather than configuring slots from scratch, you can use one of the built-in presets:

- **RX Monitor** — Focused on receive: S-meter and related RX meters.
- **TX Monitor** — Focused on transmit: forward power, ALC, SWR, compression.
- **Full Monitor** — Both RX and TX meters active at once.

Cycle through presets with the leader key: press `Ctrl+J` then `P`. The app announces which preset was loaded.

## Speak Meters

Press `Ctrl+J` then `R` to hear the current meter readings spoken. This gives you a snapshot of all enabled meters as speech — for example, "S7, forward power 50 watts, SWR 1.3."

## Reading the S-Meter

Press `Ctrl+Shift+S` to hear a full status report, which includes the S-meter reading spoken as a number (like "S7" or "20 over 9").

## Auto-Enable on Tune

When you start a tune carrier (Ctrl+Shift+T), meter tones automatically activate so you can hear SWR and power levels while tuning your antenna. They return to their previous state when you stop tuning.

## Peak Watcher

The Meters Panel includes a Peak Watcher that alerts you when ALC goes high. If ALC spikes above the safe zone, you'll hear a warning so you can back off the mic gain or adjust your levels.

## Uses for Meter Tones

Meter tones are especially useful for:

- **Antenna tuning** — Listen to the SWR tone as you adjust an antenna tuner or rotate a beam. Lower pitch means lower SWR.
- **CW pileup positioning** — Find where stations are calling by listening to the S-meter tone as you tune across the pileup.
- **Signal peaking** — Fine-tune frequency or antenna for maximum signal.
- **TX monitoring** — Keep an ear on power, ALC, and SWR while transmitting, without taking focus away from the QSO.
- **Contest operation** — Quick audio feedback without breaking your rhythm.
