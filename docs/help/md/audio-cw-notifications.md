# CW Notifications

JJ Flex sends you short CW prosigns for certain app events — connecting to a radio, disconnecting, changing modes, and a few others. It's a quiet parallel channel to the speech output, using the Morse your brain already knows how to filter in and out.

## What gets sent as CW

- AS (wait, attention) — fires while something is in flight and you should hold for an answer.
- BT (break) — fires on a transition where nothing's wrong, just switching tracks.
- SK (end of work) — fires on clean disconnect.
- Mode abbreviations — when you switch modes, the new mode's name is sent in Morse (USB, LSB, CW, FM, AM, DIG, etc). Short, recognizable, and doesn't collide with whatever speech was already in flight.

## Why CW and not more speech

Speech is busy. Between status announcements, mode changes, screen-reader focus chatter, and the actual radio audio, there's a lot happening at the ears already. CW sits in its own mental channel — you can hear SK or AS in the background and know what happened without interrupting whatever else is being said.

## Tuning the sound

Settings > Audio has a CW Notifications section:

- Speed (WPM) — how fast the Morse is keyed. Default settles around 25 WPM. Soft cap is 60. If you're a seasoned CW operator and want it snappy, turn it up. If you're newer to Morse, dial it down.
- Tone frequency — what pitch the Morse plays at. Default sits at a comfortable mid-range sidetone pitch. Adjust if it's colliding with something else in your audio stack.
- Enabled — master on/off if you'd rather have a silent app.

## When you'll hear it

Event-driven, not status-driven — you hear prosigns on *changes*, not while things are steady. If the radio's connected and running happily, no CW. If you just reconnected after a drop, SK after the reconnect-attempt sequence. If a mode change happens, the new mode name. Otherwise, quiet.
