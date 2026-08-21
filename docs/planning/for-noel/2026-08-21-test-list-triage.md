# Which tests still need you — the master list, triaged

Written 2026-08-21, against `2026-08-19-master-test-list.md`. This is task #149:
*"most of it is not a job for Noel."* That turns out to be true, and more of it
than expected — because two things landed today that convert whole categories of
listening into assertions.

## The four buckets

**AUTO** — runs with nothing of yours involved. Asserts against the output
transcript, the UI Automation tree, config files, or process state. Can run
while you are working, because nothing sounds and nothing takes focus.

**FOREGROUND** — automated, but needs to inject keystrokes, so it needs the
desktop to itself. Same machinery, different scheduling problem. Candidate homes:
the laptop, a second Windows session, or a "desk is free" gate.

**RADIO** — needs the 8600 actually connected. Automatable in principle, but the
radio is the fixture.

**YOURS** — genuinely needs you. Does it *sound* right. Is the wording
comprehensible. Is the loudness correct. No instrument substitutes.

## The count

Of roughly 45 tests: about **20 AUTO**, **12 FOREGROUND**, **6 RADIO**, and
**4 YOURS**. So somewhere near three quarters of the list can run without you
present, and only about one test in ten actually requires your ears.

---

## AUTO — no desktop, no ears, runs any time

**A2** the app survives Escape from the picker — a process check.
**A3** rescue buttons explain on demand, not on focus — the transcript proves a
*negative*: no speech event fires on focus. That was previously "listen and
confirm nothing happened", which is the hardest thing to verify by ear.
**A4** the honest empty case. **A6** the Workshop makes no offline promises.
**A7** About reads the truth — easier as of today, since the About line now
carries the Prism version and live backend.
**A8** the diagnostics surface, cold.

**C1 — ten launches leave nothing behind.** Fully scriptable: launch, exit,
repeat, then check for strays. Worth flagging because this is **#21**, the
orphan-process bug that has never reproduced on this machine. A script can run
it a hundred times unattended rather than ten by hand.

**F1** a corrupt preset file says so — mangle the file, launch, assert the
transcript says it. **F2** preset round trip. **F3** mic profile meets the wrong
microphone. **F4** the cleanup chain rides the profile.

**G2** the capture is a findable session. **G5** disk-space honesty.

**H1** five switches under one master — a tree read. **H2** a category silences
its family. **H3** the master outranks. **H4** the quick mute is remembered.

**I1** the disconnect is heard. **I2** Settings is quiet on focus.
**I3** dialog titles queue instead of killing speech.

**E1** the default device names which audio system nominated it.

---

## FOREGROUND — automated, but needs the desktop

**A1** the rescue page and its exactly-five-button tab order. The tree read is
AUTO; the Escape keypress and the Tab walk are not.
**A5** Help opens at the page about this page.

**B1 through B7** — the whole `Ctrl+F1` pass. Seven tests, all the same shape:
press the chord, assert the transcript carries the right explanation. Nearly free
once the harness can press keys, and currently seven manual listening passes.

**G1** the capture chord from anywhere — and this one is newly assertable, because
the app now writes a `CaptureState:` marker, so "did the capture start" is a
file read rather than a judgement.
**G3** export and the bundle. **G4** the offer at the moment of failure.
**G6** F1 knows which tab you are on.

---

## RADIO — the 8600 is the fixture

**D1** not one word about SmartLink on a local connect. **D2** the local-only
offer appears once and sticks. **D3** full Home replaces the rescue page.
**D4** a feature you cannot have says so. **D5** REM ON speaks its state and
survives to the next connect. **D6** the picker learned your way in.

Most of these become assertions once a radio is present — D1 in particular is a
pure transcript check, since "not one word about SmartLink" is a search over the
speech events.

---

## YOURS — no instrument substitutes

**E2** the 44.1-locked device opens anyway. Needs the awkward hardware; skip if
you own none.
**E3** tone monitor without clicks. The transcript can prove the audio engine
reported no glitches, but whether it *sounds* clean is yours.
**E4** PC audio loudness sits right. Pure ear judgement against a reference, and
the note in the list is right that "too hot" is as much a failure as "too quiet".
**J1** fresh-VM install. Needs a real machine that has never had .NET 10, and a
human to confirm it launches without prompting.

---

## The one worth looking at twice

**I3 — dialog titles queue instead of killing speech.**

Until today this was pure ear: open a dialog while something is being said, and
judge whether the title cut the utterance off or waited its turn. Unreliable to
test, easy to regress, and the exact defect class that has bitten repeatedly —
#69 was controls speaking on focus and truncating the group announcement.

The transcript now records `intent` and `interrupt` per speech event, plus a
monotonic timestamp stamped inside the writer lock, so **line order is time
order**. "Did this interrupt or queue" stopped being a judgement and became a
field.

That is the clearest single example of what changed today. It is also why the
`Ctrl+F1` block is worth automating early: seven tests that are currently seven
listening passes, all reduced to one assertion each.

**Checked, and it was not true yet.** Written into this document in the morning
from reading the recorder's source; measured the same afternoon against a live
connect transcript, where `intent` appeared on **4 of 32** speech events. The
recorder omitted the field whenever the call site passed nothing, so an
assertion keyed on `intent` would have matched almost nothing — and matched it
*silently*, reporting a clean pass over an empty set.

Fixed the same day: `level`, `intent` and `origin` are now always emitted,
writing an explicit JSON null when there is no value. Three states stay
distinguishable — a string is a real value, an explicit null means the call
site supplied none, and an absent key means a transcript written before the
fix.

Worth stating plainly because it cuts against this document's own argument: a
field being in the schema is not the same as a field being populated, and the
whole case for automating I3 rested on the second. It held, but only after
someone looked.

---

## What I would do with this

1. **Point the runner at the AUTO bucket first.** Twenty tests, no scheduling
   problem, and they can run on every build starting immediately.
2. **C1 deserves priority out of order** — it is #21, it has never reproduced
   here, and a script can attempt it far more times than a person will.
3. **The FOREGROUND bucket is one scheduling decision, not twelve problems.**
   Solve where Tier 2 runs, and all twelve come along.
4. **Keep YOURS small and deliberate.** Four tests is a short sitting, and each
   is genuinely about how something sounds — which is the part that should cost
   your attention.
