# Meter Tones and the Meters Panel

Meter tones give you audio feedback for signal strength, power output, SWR, ALC, and other readings — no need to look at a visual meter. The Meters Panel lets you configure exactly what you hear and how.

## Opening the Meters Panel

Press `Ctrl+M` to open (or close) the Meters Panel. This opens and closes the panel and nothing else — it does not start or stop your meter tones. The tone switch is `Ctrl+J` then `T`, or `Ctrl+Alt+M`.

The panel starts with a **Meter** list: pick which of your meters you want to work on, and every control below it belongs to that meter. You are not tabbing through one set of controls per meter — there is one set, and it points at whichever meter you chose. Press `Delete` on the Meter list to remove the one you are on; it asks first, because there is no undo.

## Meter-Related Hotkeys

A few shortcuts work on meters from anywhere in the application:

| Key | Action |
|-----|--------|
| Ctrl+M | Open or close the Meters Panel |
| Ctrl+Alt+M | Toggle meter tones on or off (quick mute without opening the panel) |
| Ctrl+Alt+P | Cycle to the next meter preset |
| Ctrl+Alt+V | Speak the current meter readings |
| Ctrl+J, T | Toggle meter tones on or off (JJ key form of Ctrl+Alt+M) |

## How Meter Tones Work

When meter tones are enabled, JJ Flexible Radio Access converts meter readings into audio tones. The pitch of the tone rises and falls with the meter reading, giving you an intuitive sense of signal strength, power output, or whatever meter you are monitoring at the time.

## What You Can Set Per Meter

You can have up to 8 meters at once. Each one has these settings:

- **Source** — which of the radio's own meters this tone follows, named the way the radio names it. The short list holds the ones most operators want. Tick **Show every meter this radio reports** to reach all of them — on an 8600 that is over a hundred — grouped by where they come from: the radio itself, each slice, and any amplifier or tuner. Underneath the list, a line of detail tells you that meter's range, its units, and what it is reading right now.
- **Active slice versus a numbered slice.** Some meters, the S-meter among them, belong to a slice rather than to the radio as a whole. Those appear twice: once as **Active slice**, which follows whichever slice you are listening to, and once per numbered slice if you would rather pin it to one receiver. Active slice is listed first because it is what most people want.
- **Voice** — the timbre this meter speaks with: Pure, Hollow, Reedy, Organ, Bell, Trill, Raspy, Thin, Square, Breath, Ring, Two-Tone, Swell, Pulsing and Urgent. Timbre is what tells two meters apart, so give meters you listen to together clearly different voices.
- **When it sounds** — always, only while receiving, or only while transmitting. A transmit meter that sounds on receive is just noise.
- **Pan** — where the tone sits in the stereo field, running smoothly from full left through centre to full right, and announced in words rather than as a bare number. Never make pan the only difference between two meters: mono listeners and anyone with asymmetric hearing loss lose it completely. That is what voices are for.
- **Volume** — how loud this meter is, before the master meter volume is applied.
- **Lowest pitch and highest pitch** — the tones you hear at the bottom and the top of this meter's range, in hertz.
- **This meter sounds** — whether this meter's tone plays at all, without having to delete it.

Use **Add Meter** to create another, and **Delete** to remove the one you are on. **Test** plays a two-second preview of that meter's tone, so you can hear what it will sound like before you go on the air.

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

`Ctrl+S` reads the meter on its own, and Space on the S Meter field on the Home does the same thing. Both of them say what the number counts, so you never get a bare figure: "S 7", or "S 9 plus 20 dB" once you are over S9, or "S meter minus 97 dBm" if you have set this radio to dBm. The word "minus" is spoken as a word rather than left as a dash, because a dash reads differently on every voice and punctuation setting and your signal strength is not a thing to guess at.

Which unit you get is your choice per radio, and it stays chosen until you change it — `Ctrl+S` switches between S-units and dBm, and the Radios tab in Settings shows where it stands. One S-unit is 6 dB, so "S7" covers a lot of ground; dBm is the one to reach for when you are asking whether an antenna change actually helped.

While you are transmitting, the same key answers with real forward power in watts instead. Keyed up, the S-meter is not describing anybody's signal.

## Auto-Enable on Tune

When you start a tune carrier (`Ctrl+Shift+T`), the meter tones automatically activate so you can hear your SWR and power levels while you are tuning. When you stop tuning, meter tones return to whatever state they were in before.

## Transmit level alerts

The Meters Panel can warn you while you are transmitting, so you hear a problem rather than having to watch for it.

It watches your transmit drive. If the drive runs high, or past full scale, you hear a warning earcon — a signal to back off the microphone gain or your drive level before the signal distorts.

If you have an amplifier wired to the radio's ALC jack, it also watches that line. That half only means anything with a cable actually connected to the jack, so it is of no use to a station without one.

## Where Meter Tones Really Shine

Meter tones are especially useful in these situations:

- **Antenna tuning** — listen to the SWR tone as you adjust an antenna tuner or rotate a beam. Lower pitch means lower SWR.
- **CW pileup positioning** — find where stations are calling by listening to the S-Meter tone as you tune across the pileup.
- **Signal peaking** — fine-tune frequency or antenna position for maximum signal strength.
- **TX monitoring** — keep an ear on power, ALC, and SWR while transmitting, without taking any attention away from the conversation itself.
- **Contest operation** — quick audio feedback without breaking your rhythm.
