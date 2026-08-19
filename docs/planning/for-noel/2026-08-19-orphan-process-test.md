# Orphan process test — does jjflexible.exe outlive its window?

**Date:** 2026-08-19
**Task:** #21
**Build:** any current build. The fix under test is #14 (the `Audio.Finished()`
timeout loop that could never time out) plus the foreground-thread and
teardown-order work.
**Radio:** any. PC audio is the part that matters, not which rig.

> **RUN THIS ON THE LAPTOP.** Corrected 2026-08-19 — Noel: "shouldn't we be
> doing all these tests on the laptop itself since that was the problem
> machine?" Yes. The laptop is where the survivor has actually been seen; the
> ms-02 never showed it. Testing on the machine where a bug does not occur is
> testing where the bug is not.
>
> **The real prerequisite is therefore a current build ON THE LAPTOP**, which
> does not exist yet. That is a distribution step, not a testing one, and it
> blocks everything below.
>
> Run order once the laptop has a build: **laptop first**, ms-02 only as a
> comparison afterwards. If the laptop is dirty and the ms-02 clean, that is a
> timing race and the difference between the machines is the clue.
>
> **SHELVED 2026-08-19** pending a laptop build.

Mark results inline with `**** `.

---

## Why the setup is fussy

`startRemoteAudioThread()` has **exactly one caller** — the `PCAudio` property
setter. If PC audio is off, that thread never starts, so there is nothing that
could be orphaned and **a clean result proves nothing at all**.

That is why this test has sat unrun: every casual attempt happened to have PC
audio off.

**Second reason it is fussy:** you have never seen the ghost on the ms-02, only
on the laptop, and not on the laptop recently either. So a single clean pass is
weak evidence. The variants below exist to make a negative result mean
something.

---

## Setup — do this once

1. Settings, Audio: set PC audio for this radio to **Always on for this radio**
   (not "as I left it"). That removes any chance of connecting with it off.

**** 

2. Note the exact time. Useful when reading the trace afterwards.

**** 

3. Have a PowerShell window open and ready with this, so you can run it the
   instant the app window disappears:

```
Get-Process jjflexible -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

Empty output means no process. Any line means a survivor.

**Task Manager works just as well** — look for `jjflexible.exe` under Details or
Processes once the window is gone. Use whichever you would rather navigate; the
PowerShell version is only quicker because it prints nothing when the answer is
"no survivor".

**** 

---

## RUN 1 — CRITICAL: close while connected, audio flowing

The canonical case.

1. Launch, connect to the radio.
2. **Confirm you can actually hear radio audio.** Do not skip this — it is the
   only proof PC audio really started. Audio being *switched on* is not the same
   as audio *running*.
3. Leave it playing for at least 30 seconds.
4. Close the app the way you normally would.
5. Run the PowerShell check.

**** RUN 1 RESULT (process survived: yes / no):

---

## RUN 2 — HIGH: disconnect first, then close

Distinguishes "teardown on disconnect" from "teardown on exit". If Run 1 is
clean and this one is not, the bug moved rather than went away.

1. Launch, connect, confirm audio, leave 30 seconds.
2. **Disconnect from the radio** and wait a few seconds.
3. Close the app.
4. Check.

**** RUN 2 RESULT:

---

## RUN 3 — HIGH: close by a different route

The exit path matters — a window close, a menu exit and an Alt+F4 do not
necessarily run the same teardown.

1. Launch, connect, confirm audio.
2. Close using a **different method** from Run 1 — if you used the window close,
   use File then Exit, or Alt+F4.
3. Check.

**** RUN 3 RESULT (and say which method you used):

---

## RUN 4 — the ms-02, for comparison only

Runs 1 to 3 belong on the LAPTOP (see the note at the top). This run repeats
Run 1 on the ms-02 purely to compare.

It matters only if the laptop was dirty: a laptop-dirty, ms-02-clean pair means
a timing race, and the difference between the two machines — speed, audio device
count, driver stack — is where the answer lives.

**** RUN 4 RESULT:

---

## If a process DOES survive

Do not kill it immediately — it is more useful alive for a minute:

1. Note its **Id** and **StartTime** from the check above.
2. Grab a snapshot of what it is doing:

```
Get-Process jjflexible | Select-Object Id, CPU, Threads, WorkingSet
```

A survivor burning CPU is a spinning loop; one at zero CPU with threads alive is
a thread that never got the signal to stop. Different bugs.

**** SURVIVOR DETAIL:

3. Then kill it: `Stop-Process -Name jjflexible`

---

## Afterwards

Send the trace. The teardown sequence is logged, and with the time noted in
setup step 2 the relevant section is easy to find.

**** ANYTHING ELSE NOTICED:

---

## What the results will mean

- **All four clean** — good evidence the fix holds, and the strongest statement
  we can make without a reproduction. #21 closes as fixed-not-reproduced, with
  the runs recorded so a future regression has a baseline.
- **Any run dirty** — we have a live reproduction with a known exit path, which
  is far more valuable than four clean passes. That is the outcome to hope for
  if the bug still exists at all.
- **Laptop dirty, ms-02 clean** — a timing race. Machine speed or audio device
  count is the variable, and that narrows the search considerably.
