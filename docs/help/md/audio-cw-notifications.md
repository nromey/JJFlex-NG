# CW Notifications

JJ Flexible Radio Access sends you short bursts of Morse for certain application events — connecting, disconnecting, and changes to your slices. It's a quiet parallel channel alongside the speech output, using the Morse your brain already knows how to filter in and out of attention.

## What Gets Sent as CW

- **AS** (wait, attention) — something is in flight and you should hold for an answer.
- **BT** (break) — a transition where nothing is wrong, just switching tracks.
- **SK** (end of work) — a clean disconnect. It doesn't go out bare, either: you get "73 SK" and a little hand-wave of two dits, and above 25 words per minute the app signs with its own callsign first.
- **A slice census** — when the number of slices in use changes, you get "SL" and then the fraction. "SL 3/4" means three of your radio's four slices are up; "SL 4/4" means the radio is full.
- **A slice identity** — when you move to another slice, or change the mode on one, you get "SL" then the slice letter then the mode. "SL A USB".

Both slice messages open with "SL", so you know what is being counted before the numbers arrive.

## Why CW and Not More Speech

Speech is busy. Between status announcements, mode changes, screen-reader chatter, and the actual radio audio, there's a lot happening at your ears already. CW sits in its own mental channel — you can hear SK or AS in the background and know what happened without interrupting whatever else is being said.

## Missed One? Echo It

Press `Ctrl+J` then `E` and the app re-sends the most recent CW message. Press it again and you step back another one, and another, through the last ten. It's the CW twin of `Ctrl+F4`, which does the same for speech, and the two lists are kept separate so that running with speech off doesn't leave you pressing a key that has nothing to say.

The prosigns stay out of the echo list on purpose. AS, BT and SK are punctuation, and there's nothing you can do with "closing" arriving out of the blue.

## Tuning the Sound

Under **Settings**, then **Audio**, look for **Alerts and CW Notifications**:

- **Enable CW notifications** — the master switch, in case you'd rather have a quieter application.
- **CW pitch** — either follow the radio's own CW sidetone, so the app keys at the pitch you already have your ears set for, or use the frequency you set below. Nothing clever happens in between. If you pick "follow the radio" and no radio is connected, you get the frequency below and no fuss about it.
- **CW tone shape** — what the Morse is *made of*. Sine is a single pure tone, which is what the app has always used. The others stack harmonics on top: Square and Sawtooth are the brightest and hardest for band noise to bury, Reed and Hollow are richer without being harsh, and Bell has a slightly metallic ring that nothing else on the band sounds like. Arrow through the list to hear each one — it plays a "V" as you land on it. They're all the same loudness on purpose, so what you're comparing is the character of the sound and not its level.
- **Sidetone frequency** — what pitch the Morse keys at, between 400 and 1200 Hz. Used unless you've told it to follow the radio.
- **Speed** — how fast the Morse is keyed, between 10 and 60 words per minute. The default is 20. If you're a seasoned CW operator and want it snappy, turn it up. If you're newer to Morse, dial it down.
- **Use CW to announce mode changes** — turns the slice census and slice identity messages on.

## If It's Hard to Pick Out

A pure sine is the easiest thing on earth to lose in band noise — and worse, it can be hard to tell apart from actual received CW or from your own sidetone. If that's happening to you, reach for **CW tone shape** before you reach for the volume. Turning a buried sine up just gives you a louder buried sine, whereas changing what the tone is made of moves it somewhere the noise isn't. Setting **CW pitch** to a frequency well away from your radio's sidetone helps for the same reason.

## When You Will Hear It

CW notifications are event-driven, not status-driven — you hear them on *changes*, not while things are steady. Connected and running happily? Silence. Reconnect after a drop, add a slice, change a mode, and you'll hear about it.
