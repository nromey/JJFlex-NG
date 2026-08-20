# Amplifier bench session — what the Power Genius XL actually publishes

**Date:** 2026-08-19
**Task:** #125 (amplifier and tuner support), plus one add-on for Track H
**Build:** any build carrying Sprint 32 Track D. The Audio Workshop has a new
**Amplifier** tab; if it is not there, the build is too old.
**Radio:** the 8600.
**Hardware needed:** the Power Genius XL, moved near the radio, on 120 volts
and on the same network as the radio. A dummy load, or an antenna you are
content to key into at low power.

Mark results inline with `**** `.

---

## Why this session exists

4O3A were asked directly for developer material on 2026-08-19. Their answer was
that it is all in FlexLib and they have no code to give — no SDK, no samples, no
spec document. That is a green light rather than a gap, and it has one hard
consequence: **FlexLib is the entire contract, and what the hardware publishes
at runtime is the only documentation that will ever exist.**

So everything built this sprint is written against FlexLib's headers, and not
one field of it has been seen arriving from a real amplifier. This session is
not a nice-to-have verification pass. It IS the specification, and the capture
you take is the document.

**There is no Tuner Genius XL on site, and this session cannot stand in for
one.** The tuner rides the same status stream as the amplifier, which is
suggestive and is not evidence. Everything about what a TGXL publishes stays
unverified until one exists in the shack, and nothing here should be read as
covering it.

---

## Where to look, and what to save

Two artifacts. Take both, every time the procedure says to.

**The Amplifier tab.** Audio Workshop, last tab, called **Amplifier**. It holds
one read-only edit called **Details** with the whole picture in it: model,
serial, network address, antenna map, state, and every meter the amplifier
publishes with its units and range. It is one tab stop, so you can read it at
your own pace with review keys, and **Ctrl+A then Ctrl+C copies the lot** into
whatever you want to paste it in. That copy is the primary artifact — please
paste it into a text file each time rather than reading numbers back to me.

**The trace.** `Ctrl+J, Ctrl+D` starts a detailed capture and the same chord
stops it. The meter inventory is written to the trace once per connect and again
whenever the set of meters changes, one line per meter, so an amplifier's meters
arriving mid-session shows up there as a second block. Save each session's trace
from the Saved Diagnostic Logs window (Settings, Diagnostics tab).

One warning about reading the trace yourself: the trace prints a meter's source
index in **decimal**, while a handle is written in hex everywhere else. The
Amplifier tab does that conversion for you and groups each amplifier's meters
under its own name. Trust the tab; keep the trace for me.

---

## Part 0 — Baseline, WITHOUT the amplifier

This is the half everybody forgets, and skipping it makes the rest of the
session almost worthless: without it there is no "before" to subtract, and a
meter list with an amplifier in it is just a long list.

1. Leave the amplifier **powered off and unplugged from the network.** Off is
   not enough on its own if it still holds a network address.

**** 

2. Start JJ Flexible and connect to the 8600 normally.

**** 

3. Start a detailed capture with `Ctrl+J, Ctrl+D`.

**** 

4. Open the Audio Workshop, go to the **Amplifier** tab, read the Details box.
   It should say plainly that no external amplifier and no external tuner is
   reported. **If it says anything else, stop and tell me** — that would mean
   we are detecting something that is not there, which is the single worst
   outcome this design can produce.

**** 

5. Copy the Details box (Ctrl+A, Ctrl+C) and paste it into a file called
   something like `amp-baseline.txt`.

**** 

6. Stop the capture and save the trace. Name it so you can tell it from the
   next one.

**** 

---

## Part 1 — Power the amplifier up and watch it arrive

1. Plug the amplifier into the network and into 120 volts, and power it on.
   Leave JJ Flexible connected and running while you do — **do not restart the
   app.** Whether the amplifier appears in a running session, without a
   reconnect, is one of the things being tested.

**** 

2. Start a fresh detailed capture BEFORE you power it on if you can manage the
   ordering; if not, start one now. The arrival is the interesting part.

**** 

3. Sit on the Amplifier tab and let it refresh. It re-reads once a second while
   the tab is on screen. **How long after power-on does the amplifier appear?**
   Roughly is fine — the useful distinction is "immediately", "after its
   self-check finishes", or "not until I reconnected".

**** 

4. When it appears, read the Details box. Answer these from it:

   - What **model** string does it report?

   **** 

   - What **serial number**?

   **** 

   - What **network address and port**?

   **** 

   - What **antenna map** does it show? The line reads "Antenna map, radio port
     to amplifier output" followed by the raw pairs the amplifier sent. **Does
     that pairing match how the amplifier is actually cabled and configured?**
     This one matters — the whole line is presented raw because nobody here has
     ever seen a real one, and if the halves are the other way round I need to
     know.

   **** 

   - What **state** does it report while idle and untouched?

   **** 

5. **The headline question: how many meters does it publish, and what are
   they?** Copy the whole Details box into a file called something like
   `amp-present.txt`. The difference between this and the baseline file is the
   answer to the question this entire session exists for.

**** 

6. If the meter list is **empty** — the box says it publishes no meters to this
   radio — that is a real and important result, not a failure. Say so and move
   on. It would mean the amplifier reports status but not measurements, and
   several assumptions downstream would have to change.

**** 

---

## Part 2 — Standby and operate

The Amplifier tab has one control, an **Operate** checkbox. It is the only
thing this build can command, and it sends the amplifier's own operate command.

1. With the amplifier idle, **check the Operate box.** What happens at the
   amplifier — physically, audibly, and on its own front panel?

**** 

2. Does the tab's state line follow within a second or so? The checkbox
   deliberately does not assume the state it asked for; it shows what the
   amplifier reports back. **If the box flips and the state line does not
   follow, tell me** — that gap is the failure mode worth catching.

**** 

3. **Uncheck it.** Does it go back to standby, and does the tab follow?

**** 

4. Try it once more while the amplifier is still finishing its power-up self
   check, if you can catch that window. Does the command get refused, queued,
   or ignored?

**** 

---

## Part 3 — Transmit, briefly, into a dummy load

Low power. This is about which meters move, not about output.

1. Put the amplifier in operate, select the antenna port it is configured for,
   and key up briefly at low power into the dummy load.

**** 

2. Watch the Amplifier tab's Details box during and just after. **Which meters
   moved?** Names and rough values are enough.

**** 

3. Does the state line change to a transmitting state while you are keyed, and
   **does it distinguish port A from port B**? FlexLib has separate states for
   the two and I would like to know they are real.

**** 

4. Is there anything in the amplifier's meter list that reads like forward
   power, reflected power, SWR, temperature, voltage or current? Name the ones
   you can identify from their names and units.

**** 

5. Do the amplifier's meters keep updating between transmits, or do they go
   quiet? The tab shows how long ago each meter last updated, so this is
   readable rather than a guess.

**** 

---

## Part 4 — Take it away again

1. Power the amplifier off while JJ Flexible stays connected.

**** 

2. Does the Amplifier tab go back to saying there is none? Does it take a while?

**** 

3. Do its meters disappear from the meter list, or do they linger as stale
   entries?

**** 

4. Stop the capture and save the trace.

**** 

---

## Part 5 — The one thing that is NOT about the amplifier

**Track H needs this and it cannot be got from reading our own source.** It is
short, and you are already sitting at the radio with a client connected.

Some background so the steps make sense. The radio has a profile autosave
setting. FlexLib has two ways to touch it and they disagree: a **property** that
sends the command unquoted and is wired to the status parser, and a **method**
that sends it quoted and has no status handling and no caller anywhere. The
property is the real one, and because the radio reports the setting back on its
own status stream, **"is autosave on?" is already answerable from the wire and
is not what I need from you.**

What nobody can answer from source is what autosave actually DOES.

1. Start a detailed capture. Most of what follows lands in the trace, so you do
   not have to judge any of it at the bench — you mostly have to make the
   events happen while the capture is running.

**** 

2. Turn profile autosave **on** using SmartSDR (that is where the setting has a
   real UI today). Note which **global profile** is selected at the time.

**** 

3. Change something distinctive and easy to describe — a slice frequency, the
   transmit power, a filter width. Something you would notice coming back.

**** 

4. Disconnect cleanly. Reconnect. **Did the change come back?** And is the
   global profile still the same one, or did the radio switch or create one?

**** 

5. **The high-value one, worth more than the other three together.** The global
   profile is global, but a disconnect is per client — so what happens when two
   clients are connected and only one leaves?

   - Connect a **second** client to the radio at the same time. SmartSDR on
     another machine is the easiest; a second JJ Flexible instance works too if
     that is more convenient.

   **** 

   - On the second client, change something distinctive that the first client
     is NOT touching.

   **** 

   - Disconnect **only the second client**, leaving the first one connected.

   **** 

   - Now reconnect the second client. **Did its change survive?** And more
     importantly: **did anything the FIRST client was doing get folded into the
     saved profile, or overwritten by it?**

   **** 

6. Turn autosave back off if that is how you normally run, and stop the capture
   and save the trace.

**** 

This decides whether Track H can ever turn autosave on safely on the operator's
behalf. If a second client's disconnect writes the global profile, then an
autosave that looks harmless in a single-client shack quietly rewrites state out
from under whoever is still connected.

---

## What I am NOT asking you to do

- **Nothing with a tuner.** There is no TGXL here. When one arrives this
  procedure gets a sibling; the amplifier capture does not substitute for it.
- **Nothing that makes the amplifier fault.** You cannot make an amplifier fault
  to order and you should not try. Fault handling is exactly the path a
  simulator earns its place on, which is a later piece of work.
- **No judgement on whether the meter list is "right".** There is nothing to
  compare it against. Whatever it reports IS the specification, and my job is
  to write it down accurately.
