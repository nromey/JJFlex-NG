# The open list, sorted by what closing it actually takes

Written 2026-08-21 evening, in answer to a fair worry: the backlog grows, some
things get closed, and older items — Track F receiver simulation was the example
— quietly stop being looked at.

## First, the count is better than it feels

**179 tasks exist. 121 are done. 58 are open.** That is two thirds closed, not a
runaway list. Today alone closed five.

The number felt like "seventy-something" because an open list is read as one
undifferentiated queue, and a queue of 58 sounds hopeless. It isn't a queue.
Sorted by what closing actually requires, it looks like this:

- **6 are waiting on a sentence from you**, not on work
- **12 are gated on hardware or a window**, and this weekend retires most of them
- **14 are small and fully specified** — one sitting each, like #174 tonight
- **7 need investigating before they can even be scoped**
- **17 are genuine multi-session builds**
- **2 may already be resolved and nobody updated them**

Only that last group is actually "forgotten". The rest are correctly waiting.

---

## Waiting on you — six sentences, and they unblock real work

These cost nobody any effort. They have been open only because nobody asked.

- **#114** — the confirmation tones are bland next to the new alarm. Worth a pass,
  or leave them?
- **#124** — the meter UI is stuck on an eight-value enum while the scalable model
  sits unused beside it. Migrate now, or leave until the meter analyzer (#123)
  forces it?
- **#142** — Earcon Explorer buttons are named after radio actions, so auditioning
  a sound reads like performing the action. Rename to sound-names?
- **#146** — CW notification pitch: follow the sidetone, or configured separately?
  The task title already leans "let the operator choose"; it needs confirming.
- **#155** — on-air testing. Needs your call on frequency, conditions and the
  identification question before it can be planned at all.
- **#161** — the CW notification vocabulary as a grammar. Needs you to rule on the
  shape before anyone writes the words.

Two more were answered tonight: **#147** is now Simple and Rich, **#116** ducks on
warnings only.

## Gated on hardware or a window — twelve, and the weekend clears most

The dummy load arrives Saturday. These are not waiting on capacity:

**#163**, **#164**, **#139**, **#150** are literally the bench plan's Tests 1
through 6. **#27** and **#56** are the transverter and Track F unblocking
sessions, both zero-keying, both doable at the desk. **#108** and **#95** are
single questions a connected radio answers. **#125** needs the amplifier cabled.

**#21** is different — it needs the *laptop*, not the radio, and it is the orphan
bug that has never reproduced on this machine. The new runner makes it scriptable,
so it becomes "run it two hundred times unattended" rather than "try it ten times
by hand".

## Small and fully specified — fourteen, one sitting each

Each of these has a written spec and no open questions. #174 was one of these
tonight and took under an hour end to end.

**#127** (meters expander has no earcon), **#133** (build-debug.bat cannot zip and
blames the wrong thing), **#135** (SharpCompress ships twice, 3.6 MB), **#136**
(audioConfig.xml has one odd hook), **#137** (FlexLib formats an amp handle
unpadded), **#141** (a Radios namespace shadows System), **#143** (the farewell
timeout is a flat 5000 ms chosen for the wrong case), **#109** (TX Controls opens
onto nothing — a delegate declared, called, never assigned), **#120** (extend the
Earcon Scratchpad), **#160** (nothing announces the slices you are not on),
**#176** (the UIA TogglePattern trap), **#175** (jjprobe key injection).

**#175 deserves priority**, because it blocks all twelve FOREGROUND tests from
this morning's test triage — including the whole seven-test Ctrl+F1 block.

## Need investigating before they can be scoped — seven

Not work yet, because nobody knows how big they are: **#58** (the CW connect storm
guard that shipped and did not work), **#59** (four slices on connect), **#93**
(re-survey the connect-cluster speech), **#110** (inventory the unwired surfaces
properly), **#121** (audio lives on six surfaces), **#83** (five kept warning
categories), **#115** (earcon camouflage — needs measurement before design).

## Genuine builds — seventeen, correctly deferred

**#65** (the string store, ~2,031 sites), **#123** (meter analyzer), **#122** (the
TX chain walk), **#151** (station messages), **#152** (transmit-audio wizard),
**#177** and **#178** (the keying paths and automatic identification), **#10**
(receiver simulation on IQ playback), **#113** and **#119** (the Earcon Explorer
becoming a real bench), **#153**, **#154**, **#156**, **#157**, **#158**, **#162**,
**#57**.

**#10 is the one you named**, and it is not forgotten so much as never started: play
captured IQ through a simulated receiver with AGC mirroring the rig's live
settings, a receive filter, and a mandatory noise floor. It is a soft dependency
of preset tuning. It is a real feature, not a leftover.

## Possibly already resolved — two, and this is the actual rot

**#140** — "the TX stream is created with no compression parameter". The task calls
it the strongest available lead. The 2026-08-22 bench plan says it was
**falsified** — that an 8600 answers `compression=OPUS` to the bare command and
shipping SmartSDR sends the identical command, which is precisely the dead-end
branch #140's own "how to settle it" describes. **These two documents disagree and
I wrote both.** A control-channel capture during a PC-audio transmit settles it in
about a minute. Until then neither should be trusted.

**#148** — "automated test suite in three tiers". #172 built the runner tonight
with exactly those three tiers. #148 is probably now either done or reduced to
"write the remaining AUTO assertions". It needs re-reading, not re-doing.

---

## What would actually stop things being forgotten

The triage is a snapshot; it rots. Two habits would not:

1. **When a task closes, check what it closed *beside* it.** #172 almost certainly
   subsumed #148 and nothing noticed. The seal already sweeps memory for resolved
   entries; the task list deserves the same sweep.
2. **A task that has been open a long time is not automatically stale.** #10 has sat
   because it is genuinely a build, not because it was dropped. Age is not the
   signal — the signal is whether anything still depends on it. That is the same
   rule the memory archive sweep uses, and it works there.
