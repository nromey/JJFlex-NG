# Earcon masking test — do they survive a live noise floor?

**Date:** 2026-08-19
**Build:** Debug x64, `bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe` (built 16:20:36)
**Radio:** 8600, PC audio ON. No antenna is fine — S2 receiver noise is the masker.
**Time:** short. Four sections, and section 1 is the one that matters.

Mark results inline with `**** ` right under each step. Out-of-order is fine;
the sections are independent except that all of them need the setup done.

---

## TL;DR — what this is settling

You said the short mechanical sounds "sound similar to what I might hear on the
bands." That is a different problem from "too quiet", and the two need
different fixes:

- **Too quiet** → fix with gain, or by ducking PC audio under the earcon.
- **Camouflaged** → gain makes it WORSE (a louder click is a louder-sounding
  static crash). Only changing the *kind* of sound fixes it.

The test tells me which. The bucket that decides it is bucket 3 below.

**Sort every sound into one of three buckets:**

1. **SURVIVES** — you hear it and you know what it was.
2. **GONE** — buried, you would have missed it.
3. **HEARD SOMETHING, COULDN'T SAY WHAT** — you know a sound happened but it
   did not read as a specific sound.

**Bucket 3 is the whole point.** It means loud enough but not *distinct*
enough, which is camouflage, not level. If most of the clicks land in bucket 3,
the fix is timbre and #115 is right. If they land in bucket 2, it is mostly
gain and ducking will carry it.

**One honest caveat, and it makes the test stronger:** with no antenna you are
hearing stationary receiver hiss. Real QRN adds impulsive crashes, and clicks
hide *better* in impulsive noise. So this is the conservative case — anything
that vanishes into flat S2 hiss is worse on 40m at night.

---

## Setup — do this once

1. Launch the Debug build and connect to the 8600 with **PC audio ON**.

2. Set AF to **the level you would actually operate at**. Not cranked to prove
   a point, not backed off to be polite. The honest level is the test.

3. Open the **Audio Workshop**, go to the **Earcon Explorer** tab. It is
   non-modal, so it coexists with the connection.

4. Confirm you can hear the noise floor and the app at the same time before
   starting.

**** SETUP RESULT:

---

## SECTION 1 — CRITICAL: Warnings, against noise

This is the comparison that settles it. The alarm and Feature Off are at the
**same volume, 0.30.** Any difference you hear is duration and harmonics, not
level.

Find the **Warnings** section of the explorer. Play each twice.

1. **Warning Alarm** — expected to survive easily. 750 ms, harmonic, sustained.

**** 

2. **Problem Recorded** — 270 ms, two pure sines falling. Expected: marginal.

**** 

3. **Feature On** — 160 ms, two pure sines rising.

**** 

4. **Feature Off** — 160 ms, two pure sines falling.

**** 

5. **The question that matters:** with noise running, can you still tell
   Feature On from Feature Off? You said in the quiet room this was never an
   issue. Is it still not an issue with the band up?

**** 

---

## SECTION 2 — HIGH: Filter sounds and clicks

These are the ones you flagged. Find the **Filter Sounds** section.

1. **Filter Edge Enter** and **Filter Edge Exit** — three-note runs, 55 ms per
   note, 625/785/940 Hz. Every note is under the 50 ms-ish threshold where the
   ear stops resolving pitch and hears an onset instead.

**** 

2. **Filter Edge Move** — the repeated one you would hear while dragging.

**** 

3. **Filter Squeeze** and **Filter Stretch** — 600/900 Hz, volume 0.25, the
   quietest of the group.

**** 

4. **Judgement call:** if you were actually riding a filter edge with this
   noise, would these tell you what you needed, or would you be flying blind
   and relying on speech?

**** 

---

## SECTION 3 — MEDIUM: The routing question

Ducking is only possible where the app owns the audio path, so your setup
determines whether it even applies to you.

1. Are your **earcons** and your **PC audio** on the **same Windows output
   device**, or different ones? Settings, Audio tab has both.

**** 

2. If different: are they both reaching the same ears (both into your headset),
   or is one on speakers and one on the headset?

**** 

---

## SECTION 4 — LOW: Meter tones, if you have the patience

You predicted meter tones would be less affected. I think you are right, and
for a reason worth confirming: they are continuous, periodic and sustained,
which is exactly what makes a sound separate from aperiodic noise.

1. Turn on meter tones (S-meter is the obvious one) with the noise running.
   Does the tone stay readable?

**** 

2. Turn on a **second** meter tone. You said "listening to multiple could get
   dicey." Does it get dicey? At what point do two tones stop being two things
   and start being one mush?

**** 

That second answer feeds the waterfall work directly — it sets the budget for
how many simultaneous streams the design can spend.

---

## Anything else you notice

Especially: any sound that was fine in the quiet room and is now useless, or
anything that turns out to be *more* audible than expected.

**** 
