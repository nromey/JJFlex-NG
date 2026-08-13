# Laptop microphone and headset tests — 2026-08-13

**Build 4.1.16.842, Debug x64.** This replaces the earlier 829 pull — everything
you found this morning went into this build, so the tests changed with it.

    \\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.842\x64-debug\

Answer in the `**** ` slots. Or say "walk me through these" and I'll run them
one at a time in chat instead.

Tests 1 through 12 need no radio. Tests 13 onward do.

## What changed since you last looked

Every one of these came from you sitting in front of 829 this morning:

- Workshop sections are real groups now, announced as you tab into and out of
  them, with heading navigation on top
- The device picker hides loopbacks and virtual cables in basic mode
- Device rows lead with the device name instead of "System default:"
- The picker no longer says every row twice
- A **Windows input level** slider lives in the Microphone Check, plus
  Microphone Boost when your driver exposes it
- The check reports **loudness** as well as peak
- Seven peak bands instead of three, in a human voice, with rotating phrasing
- Settings can no longer be lost between the two config directories

---

## Section A — the Workshop, no radio

### Test 1 — you can hear the sections now

Press Ctrl+Shift+W. Tab slowly from the top through the whole TX Audio tab.

You should hear yourself entering and leaving named groups: This Computer,
Microphone, Processing, TX Filter, TX Monitor, Test Tone, Audio Check.

This is the one I most want your read on. The order was right this morning and
you couldn't feel the boundaries — that's what changed.

**** 

### Test 2 — heading navigation

From inside the Workshop, press H and Shift+H.

You should jump section to section. This is new; there was nothing for H to
find before.

**** 

### Test 3 — Ctrl+Tab still does tabs

Confirm Ctrl+Tab still moves between the three tabs and hasn't been stolen for
section movement. It shouldn't have been — you confirmed this morning it moves
tabs, and overloading it would break that.

**** 

---

## Section B — the picker

### Test 4 — how many inputs now?

Open Change Audio Devices. With advanced OFF, count the input devices.

It was 8 this morning. Loopbacks and virtual cables should be gone. Tell me the
new number and whether anything you actually use disappeared.

**** 

### Test 5 — the microphone array survived

Specifically confirm your laptop's **microphone array** is still listed in basic
mode. That's the one a stale filter used to drop entirely, and a new filter is
exactly how it would come back.

**** 

### Test 6 — one row, said once

Arrow through the input list. Each row should be spoken **once**.

**** 

### Test 7 — press H

With the input list focused, press H — or the first letter of any device you
can see.

It should jump to it. Type-ahead was always on; the rows just weren't
distinctive at the front. They lead with the device name now.

**** 

### Test 8 — Alt+S still reveals everything

Turn advanced on. Everything hidden should come back, host API and all. Hidden,
never removed.

**** 

---

## Section C — the Microphone Check, and your 0 dBFS problem

### Test 9 — there's a level control now

Select your USB headset, press Alt+M.

There should be a **Windows input level** slider right under the reading. Move
it and watch the peak follow.

This is the fix for this morning: you were pegged at 0 dBFS and the remedy was
in a Windows dialog you had no reason to know about.

**** 

### Test 10 — is there a Boost control?

Look for a **Microphone Boost** slider next to the level.

It appears only when your driver exposes one. Your headset may not have it —
that's a real answer, not a failure. Tell me either way, because we've only
been able to verify the *absence* case on the bench.

**** 

### Test 11 — get into the Good band

Using the slider, bring your peak down until the verdict says **Good**.

Read me what it says. The wording rotates now, so if you run the check a few
times you should hear different phrasings that all start with the same word.

**** 

### Test 12 — loudness appears

Once you're out of clipping, the check should report **loudness** as well as
peak. It's deliberately withheld while clipping, because a clipped signal
reads louder than it is.

**** 

---

## Section D — with the 8600

### Test 13 — connect and hear nothing about your mic

Connect, PC Audio on. Silence about your microphone is the pass.

**** 

### Test 14 — transmit monitor, listening for a rhythm

Monitor on, key up, talk. Listen for a **regular** stutter, not random
dropouts. That rhythm was the signature of the sample-rate bug.

**** 

### Test 15 — the two numbers

While transmitting, Alt+Shift+S. Read me exactly what it says.

**** 

---

## Section E — settings

### Test 16 — change something and restart

Change a setting you'll recognise — CW speed, verbosity, PC output volume.
Close JJ Flex completely. Reopen.

It should still be there. Some settings were being written to one directory and
read from another; which ones survived depended on which code path ran last.

**** 

### Test 17 — save a preset

In the Workshop, adjust something, save a preset, close the Workshop, reopen,
load it back.

No preset file has ever existed on any machine here, so this path is
unproven on disk.

**** 

---

## Anything else

**** 
