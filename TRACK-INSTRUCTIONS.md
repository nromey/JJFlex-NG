# Sprint 33 Track C — Tier 3, the radio surface: 3a runnable, 3b parked

**Worktree:** `C:\dev\jjflex-33c` · **Branch:** `sprint33/track-c`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A. Track D depends on your snapshot helper.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## The point

Tiers 1 and 2 prove the UI is reachable and the keys route. Neither proves the
RADIO DID ANYTHING. This track closes that: command it, read the radio's own
state back, assert it changed.

Radio on the bench: a FLEX-8600. **No antenna is connected.**

## Part 3a — exercise the whole non-transmitting surface

Mode, filters, slices, AGC, noise blanker, noise reduction, ANF, preamp,
attenuator, antenna selection, band, VFO, split, RIT and XIT.

For each: read the current value, command a change, read it back from the radio,
assert it took, restore.

**Read the radio's state, not our cached copy.** A test that asserts our own
property returns what we just set it to proves nothing. The value must come back
from the radio.

## The guards, and none of these are optional

**Snapshot everything before. Restore after. Restore on failure too.** This is
Noel's own station. A harness that abandons a half-configured radio is worse than
no harness. Use try/finally or an IDisposable scope — not a restore call at the
end of a happy path.

**Verify not-transmitting before every assertion, not once at the start.** State
can change under you. Cheap check, catastrophic omission.

**Refuse to run under MultiFlex when another operator is connected.** Transmit is
a genuine mutex; the rest of the surface is merely shared, and mutating another
operator's slices while they work is unacceptable. Detect it and refuse with a
clear reason.

**Slice changes do not persist** — this is known, task #117. A released slice
comes back on reconnect because the radio's global profile still has it. **Do not
be surprised by this and do NOT try to "fix" it by saving a profile.** Saving a
profile is station state and writing it is not this harness's business.

## Part 3b — build the transmit harness, run nothing

The Palstar DL-2000 dummy load is on order and is not here. **Build the harness
now; the tests wait.** Building it calmly, in advance, is much better than
writing it in a hurry next to a hot load.

What the harness needs:

- **An explicit consent gate per run.** Never automatic, never a side effect of
  constructing something. It must say it will transmit and roughly for how long.
- **A power ceiling the operator sets**, enforced in code, approached from below.
- **A duty-cycle budget with enforced cooling gaps.** The DL-2000 handles 400 W
  continuous and 2 kW for a minute. An iterative harness keys many times; track
  the budget rather than trusting the author to be careful.
- **Snapshot and restore of every transmit-affecting setting**, same discipline
  as 3a.
- **Refusal under MultiFlex with another operator connected.**

**The ATU is rationed by RELAY WEAR, not RF.** It tunes without an antenna
connected — Noel has done it. The cost of exercising it is mechanical: physical
relays with a finite number of operations. So give it a **hard budgeted count per
run, enforced in code**, not a comment asking nicely.

State this plainly in the harness documentation so nobody later expects
otherwise: **a dummy load cannot meaningfully test the ATU.** Into a matched 50
ohms it finds a match instantly, so all you exercise is the command path. Real
tuning behaviour needs a real mismatch, which means a real antenna.

**1 watt is acceptable for a smoke test** with no antenna, used sparingly. Do not
build anything that keys repeatedly at any power until the load is here.

## Coordinate with Track D

Track D verifies the analyzer's fact collection against the same radio. It will
reuse your snapshot-and-restore helper rather than growing a second one.

**So: if you conclude the helper should move, be renamed, or change signature,
REPORT IT — do not do it.** Sprint 32 lost a build to exactly this. Two tracks
merged with zero textual conflict and the result would not compile, because one
had moved a method the other was told to call. Git cannot see that class of
collision and will not warn you.

Commit the helper EARLY, as its own commit, and say so in your progress report so
Track D can start.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Noel is at the keyboard and may be using the radio. Anything that changes radio
  state while he is operating collides with him — coordinate before a run that
  takes the radio, the same way UI runs are coordinated.
- Do not touch files outside your worktree.
- **Do not fix what you find.** Record findings; repairs get triaged afterwards.

## Commits

`Sprint 33 Track C: <description>`. Commit the snapshot helper separately and
early.

## Completion report

State: which surfaces were exercised and which asserted from the radio's own
state versus our cached copy; anything that did not take; where the snapshot
helper lives and its exact signature (Track D needs this verbatim); and the ATU
budget you enforced.
