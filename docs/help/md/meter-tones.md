# Meter Tones and the Meters Panel

Meter tones give you audio feedback for signal strength, power output, SWR, ALC, and other readings — no need to look at a visual meter. The Meters Panel lets you configure exactly what you hear and how.

## Opening the Meters Panel

Press `Ctrl+M` to open (or close) the Meters Panel. When the panel is open, you will see a set of meter slots you can configure. Each slot maps a single radio meter to an audio tone.

## Meter-Related Hotkeys

A few shortcuts work on meters from anywhere in the application:

| Key | Action |
|-----|--------|
| Ctrl+M | Open or close the Meters Panel |
| Ctrl+Alt+M | Toggle meter tones on or off (quick mute without opening the panel) |
| Ctrl+Alt+P | Cycle to the next meter preset |
| Ctrl+Alt+V | Speak the current meter readings |
| Ctrl+J, T | Toggle meter tones on or off (leader-key form of Ctrl+Alt+M) |

## How Meter Tones Work

When meter tones are enabled, JJ Flexible Radio Access converts meter readings into audio tones. The pitch of the tone rises and falls with the meter reading, giving you an intuitive sense of signal strength, power output, or whatever meter you are monitoring at the time.

## Meter Slots

The Meters Panel supports up to 8 meter slots. Each slot has these configurable settings:

- **Meter source** — what the slot monitors: S-Meter, forward power, ALC, SWR, compression, microphone level, and others.
- **Waveform type** — the shape of the audio tone: Sine (smooth), Square (buzzy), or Sawtooth (bright and edgy). Different waveforms make it easier to distinguish multiple meters playing at the same time.
- **Stereo pan** — where the tone plays in the stereo field: Left, Center, or Right. Panning different meters to different ears helps you keep them apart in your head.
- **Base frequency** — the base pitch of the tone in hertz. The actual pitch varies up and down from this base as the meter reading changes.
- **Enabled checkbox** — turn individual slots on or off without having to delete them.

Use the **Add** button to create a new slot, and the **Remove** button to delete one. The **Test** button plays a two-second preview of that slot's tone, so you can hear what it will sound like before you go on the air.

## Meter Presets

Rather than configuring slots from scratch every time, you can use one of the built-in presets:

- **RX Monitor** — focused on receive: the S-Meter and related receive-side meters.
- **TX Monitor** — focused on transmit: forward power, ALC, SWR, and compression.
- **Full Monitor** — both RX-side and TX-side meters active at once.

Press `Ctrl+Alt+P` to cycle through the presets. JJ Flexible Radio Access announces which preset was loaded when you cycle.

## Speak Meters on Demand

Press `Ctrl+Alt+V` to hear the current meter readings spoken out loud. You will hear a snapshot of every enabled meter as speech — for example, "S7, forward power 50 watts, SWR 1.3."

## Reading the S-Meter

Press `Ctrl+Shift+S` to hear the full radio status report, which includes the S-Meter reading spoken as a number (like "S7" or "20 over 9").

## Auto-Enable on Tune

When you start a tune carrier (`Ctrl+Shift+T`), the meter tones automatically activate so you can hear your SWR and power levels while you are tuning. When you stop tuning, meter tones return to whatever state they were in before.

## Peak Watcher

The Meters Panel includes a Peak Watcher that alerts you when ALC runs high. If ALC spikes above the safe zone, you will hear a warning earcon — a signal to back off the microphone gain or adjust your levels before you distort your signal.

## Where Meter Tones Really Shine

Meter tones are especially useful in these situations:

- **Antenna tuning** — listen to the SWR tone as you adjust an antenna tuner or rotate a beam. Lower pitch means lower SWR.
- **CW pileup positioning** — find where stations are calling by listening to the S-Meter tone as you tune across the pileup.
- **Signal peaking** — fine-tune frequency or antenna position for maximum signal strength.
- **TX monitoring** — keep an ear on power, ALC, and SWR while transmitting, without taking any attention away from the conversation itself.
- **Contest operation** — quick audio feedback without breaking your rhythm.
