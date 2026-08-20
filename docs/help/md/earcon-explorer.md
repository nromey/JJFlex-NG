# Earcon Explorer

Earcons are the short tones JJ Flexible Radio Access plays to confirm actions and report state changes. They are quick on purpose — a tone lands in a fraction of the time a spoken sentence takes, so you learn what happened without waiting to be told.

The Earcon Explorer is where you hear every one of them on demand, and where you find out whether a sound actually works in the conditions you operate in.

## Opening It

The Explorer is a section of the **Audio Workshop**. Open the Workshop with `Ctrl+Shift+W`, or from the **Audio** menu, or from **Tools**, then move to the **Earcon Explorer** section.

The Workshop is not modal, so it sits open quite happily while you are connected and listening to the band. That is the whole point of the bench below.

## What Earcons Are

Think of earcons as audio icons. Instead of a visual checkmark or a colour change, you hear a brief tone. Rising generally means "on" or "success"; falling means "off" or "cancel"; a low thunk means "that key means nothing here."

## How the List Is Organised

The Explorer is laid out in the same six families as the switches in **Settings, Notifications tab, Alert Sounds** — connection, transmit, dialogs and panels, tuning and filters, commands and confirmations, and warnings. One vocabulary in both places, so before you decide to silence a family you can hear exactly what you would be giving up.

A seventh group at the end holds a few calibration and bench sounds that deliberately sit outside those switches. They answer to the master earcon switch only.

Every sound the application can make is in the list. It is built from the sounds themselves rather than typed out by hand, so a tone added next month turns up here on its own.

## The Bench

The controls at the top apply to the sounds while you audition them. They change nothing permanently and touch none of your saved audio settings.

- **Bench level** — a multiplier on each sound's own level. 100 percent plays it exactly as it ships. Turning it up answers a question worth answering: is this sound too quiet, or is it too *similar* to the noise? Those are different problems. If a click gets easier to identify when you turn it up, it needed level. If it just becomes a louder crash you still cannot name, no amount of level will fix it and the sound itself has to change.
- **Bench pan** — moves the sound left or right. It is added to any panning a sound already does for itself, so a left-panned filter edge auditioned at pan right lands in the middle rather than leaping across.
- **Repeats** — how many times one press plays the sound. A single short click may or may not have got through the noise; four in a row tells you.
- **Gap between sounds** — the spacing between repeats, and between sounds when you play a whole family.
- **Stop anything playing** — the button to reach for when you have lost track. It stops a running series and both of the sounds that keep going until told otherwise.

Each family also has a **Play all in order** button. This is the comparison that settles arguments: two sounds heard a minute apart both seem fine, while the same two back to back either tell themselves apart or they do not. Press it again to stop partway through.

## Sounds That Keep Going

Two sounds run until stopped rather than ending on their own — the antenna tuner's progress tone, and the transmit test-tone monitor. Those get a **Start** and a **Stop** button instead of a single Play, because a button that starts something it cannot finish is a trap.

The transmit test-tone monitor is local. It lets you hear the tone; it does not key the radio.

## Judge It Against Real Noise

A tone that is unmistakable in a quiet room can vanish on a live band, and the quiet room will never tell you which ones. Open the Explorer while you are connected, receiver up, audio at the level you actually operate at. You do not need an antenna — the receiver's own noise at S2 is a perfectly good masker, and it is the *conservative* test, because real atmospheric noise is crashier and short clicks hide in it even better.

## Earcons and Screen Readers

Earcons play alongside your screen reader, not instead of it. After a toggle earcon, the application also speaks the change — "Noise reduction on." The tone gives you the answer immediately; the speech gives you the detail.

## Some You Will Recognise

- **Rising pair** — a feature turned on. **Falling pair** — turned off.
- **Two soft counting tones, then two more** — a connection is being made, then has landed.
- **Soft falling chirp** — the JJ Command layer gave up waiting.
- **Low thunk** — that key means nothing in the JJ layer.
- **Double chime** — help was requested with `?` after the JJ key.
- **Chirp** — the tune carrier went on or off.
- **Fast pulsing** — an antenna tuner cycle is running.
- **Rising triad** — the tuner found a match. **Falling minor** — it gave up.
- **One long tone with harmonics under it** — a warning. Nothing else in the set sounds like it, and the sentence after it is the part that matters.

## Making Your Own Sounds

The Explorer plays the sounds the application ships with. To build and compare tones of your own, the **Earcon Scratchpad** on the Audio menu is the place — frequency, duration, volume, pan, a choice of voices, and a tone you can hold steady while you listen to it against the band.
