# Earcon Control

"Earcons" are the short alert sounds that JJ Flexible Radio Access plays for events such as a dialog opening, a feature toggling, transmit starting, or a connection landing. They are useful, but they are also audio — and sometimes you just want them quiet without digging into the Settings dialog.

## Quick Mute via the JJ Key

The fast way to mute every earcon at once is through the JJ key. Press `Ctrl+J` to enter the JJ Command layer, then press `Shift+T`. Every earcon mutes (or unmutes again if you press the same sequence a second time), and the app tells you which way it went.

- The quick mute only affects the earcon layer. It does not touch your meter tones, the radio audio, or the speech output — just the alert-sound layer.
- It does silence CW notifications along with everything else — they ride the same alert channel. Their own separate on/off switch lives on the Audio tab in Settings.
- **Your choice is remembered.** The quick mute and the master switch in Settings are the same switch, and it is saved the moment you press it — quick-mute tonight and earcons are still off tomorrow. If things have gone mysteriously quiet, check `Ctrl+J` `Shift+T` first; a mute from last week may still be doing its job.

## When to Use the Quick Mute

- While contesting with headphones, where every extra sound competes for your attention.
- While recording a session or a demo, when you want a clean audio track.
- During a Zoom or Teams call, when you are sharing your audio and the earcons would leak out to the other participants.
- Any situation where you want to hear the radio without application chrome.

## Turning Off Just Some of Them

Under **Settings, then the Notifications tab, under Alert Sounds** you will find the master switch plus six category switches, so you can quiet the sounds you do not want and keep the ones you rely on:

- **Connection sounds** — the counting tones while a connection is being made, and the double-beep when it lands.
- **Transmit sounds** — transmit start and stop, the hard-kill warning, the tune carrier, and the antenna tuner's progress and verdict.
- **Dialog and panel sounds** — the dings when a dialog opens or closes, and the sweeps when a Home panel expands or collapses.
- **Tuning and filter sounds** — filter-edge clicks and sweeps, the band-boundary beep, and the frequency-entry ding.
- **Command and confirmation sounds** — the JJ key layer's tones, feature on and off beeps, mute-all, and confirmations. Think twice before turning these off: without them the JJ key gives no sign it is listening.
- **Warning sounds** — the warning alarm, and the quieter two-note tone that says a problem was recorded and is waiting in the Problems list. This is the one category worth leaving on. Every other sound here is the app answering a key you just pressed; these two fire when the app has something to tell you that you did not ask about — such as transmit audio that would not have gone out.

The warning alarm is deliberately unlike anything else the app plays. It is a single long tone at 800 hertz with harmonics stacked on it, about three quarters of a second, where every other sound in the set is one or two short pure beeps. You should never have to work out whether you just heard a warning or a toggle. When you hear it, the sentence that follows is the part that matters — the tone exists to make sure you are listening for it.

The master switch outranks the categories — master off means silence, whatever the categories say. And the quick mute is that same master switch, not a separate layer, so flipping either one flips both.

## Changing How They Sound, Not Just Whether

The alert sounds were rebuilt: they picked up a shaped attack, a fade-out on the ones that should feel struck, harmonics that help them survive being played over receive audio, and a transmit-warning family that escalates in three different ways instead of just getting higher in pitch.

If you preferred the plain tones they replaced, you can have them back. Under **Settings**, then **Audio**, under **Alerts and CW Notifications**, look for **Alert tone set**:

- **Modern** — the rebuilt sounds. This is the default.
- **Classic** — the plain tones as they sounded before. Same pitches, same rhythms, same loudness; just simpler sounds.

Arrow between the two to hear a sample — a press, a ding and a warning nudge, back to back. Every other detail is untouched by this setting, so a sound you already recognise stays recognisable either way.

One thing worth knowing before you pick Classic: the three transmit warnings become pure tones that differ only in pitch, which is exactly what they were before and exactly why they were changed. If you've never had trouble telling the first warning from the last one, Classic will suit you fine.

## What Earcons Are Not

- **Not meter tones.** Meter tones are the continuous audio that represents SWR, ALC, and forward power during transmit. They have their own on/off controls — see the Meter Tones help page.
- **Not typing sounds.** The frequency-entry keystroke sounds have their own mode setting, including off.
- **Not the radio audio.** You cannot mute earcons to silence the radio — you would be muting a tiny fraction of your audio while the big signal still plays through.
- **CW notifications are half-in, half-out.** They have their own enable switch on the Audio tab, but they play through the alert channel, so the master earcon mute silences them too.

## Finding the Hotkey Again

If you forget the exact key sequence, open the Command Finder (press `Ctrl+/`) and search for "earcon" or "mute." The Command Finder lists every JJ Command layer combination along with the regular hotkeys, and it will bring you straight to this one.
