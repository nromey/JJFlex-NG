# CW Notifications

JJ Flexible Radio Access sends you short CW prosigns for certain application events — connecting to a radio, disconnecting, changing modes, and a few others. This is a quiet parallel channel to the speech output, using the Morse your brain already knows how to filter in and out of attention.

## What Gets Sent as CW

- **AS** (wait, attention) — fires while something is in flight and you should hold for an answer.
- **BT** (break) — fires on a transition where nothing is wrong, just switching tracks.
- **SK** (end of work) — fires on a clean disconnect.
- **Mode abbreviations** — when you switch modes, the name of the new mode is sent in Morse (USB for Upper Side Band, LSB for Lower Side Band, CW, FM, AM, DIG for digital modes, and so on). The prosigns are short, recognisable, and do not collide with whatever speech was already in flight.

## Why CW and Not More Speech

Speech is busy. Between status announcements, mode changes, screen-reader focus chatter, and the actual radio audio, there is a lot happening at your ears already. CW sits in its own mental channel — you can hear SK or AS in the background and know what happened without interrupting whatever else is being said.

## Tuning the Sound

Under **Settings > Audio** you will find a CW Notifications section with the following controls:

- **Speed (WPM)** — how fast the Morse is keyed. The default settles around 25 WPM. The soft upper cap is 60 WPM. If you are a seasoned CW operator and want it snappy, turn it up. If you are newer to Morse, dial it down.
- **Tone frequency** — what pitch the Morse plays at. The default sits at a comfortable mid-range sidetone pitch. Adjust if it is colliding with something else in your audio stack.
- **Enabled** — a master on/off switch, in case you would rather have a silent application.

## When You Will Hear It

The CW notifications are event-driven, not status-driven — you will hear prosigns on *changes*, not while things are steady. If the radio is connected and running happily, no CW plays. If you just reconnected after a drop, you will hear SK after the reconnect-attempt sequence completes. If a mode change happens, the new mode name is keyed. Otherwise, quiet.
