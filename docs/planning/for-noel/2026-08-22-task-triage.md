# Task triage — 2026-08-22

72 tasks are open. Nothing here is proposed for dropping; you ruled that every
one is within reach, so this sorts by **what is actually blocking each** rather
than by whether it deserves to exist.

Two things prompted it: you asked whether closure should speed up, and you
mentioned you thought the three-tier test suite was done. The second turned out
to be the more useful question.

---

## Part one — what is believed done and is not

**The string store, #65, WAS done and was still marked open.** Verified tonight:
2,342 keys across six partitions, 2,692 call sites. Now closed. That one was
stale in your favour.

**The three-tier test suite, #148, is roughly half built — and the half that is
missing is the half you would most want.** This is worth reading carefully,
because the way it fails is exactly the failure mode this project keeps
rediscovering.

### What the harness genuinely does today

`tools/radiocheck` is real, works, and is better than I remembered:

- **Unit tier** — 351 tests, in-process, no desktop, no radio, safe to run
  unattended at any time. Graded from the TRX file rather than the exit code,
  deliberately: `dotnet test --no-build` exits 0 with nothing to test, so a
  discovered-test total of zero is reported as BROKEN INSTRUMENT and never as
  green.

- **Smoke tier** — spawns the actual just-built app with `--no-render` and
  `--record`, so nothing sounds and no audio device is needed. It then asserts,
  in order: the transcript's `session-start` marker appears, carries
  `render=false`, and carries OUR process id (a wrong pid means the wrong
  instrument is being read); the process survives a settle window (a marker
  followed by death is a launch failure the marker alone would hide); at least
  one top-level window is visible to UI Automation (running-but-invisible-to-UIA
  is a real failure for an app whose users are screen readers); teardown
  completes politely; and the pid is actually gone afterwards, with a survivor
  reported loudly as the #21 orphan shape.

- **Test-count tracking** — a per-branch baseline, where a DROP is a warning
  that survives into the exit code. This exists because on 2026-08-20 two Sprint
  33 tracks went unmerged and the only symptom was a suite that got *smaller and
  greener*, since the missing files were test files.

- **A preflight that refuses to spawn** when an operator profile has
  auto-connect armed, because that instance would join the radio as a second
  MultiFlex client and `setupFromScratch()` sets RFPower to 100 unconditionally
  on a radio it has not seen before.

- **Distinct exit codes** — 0 pass, 1 failures, 2 usage, 3 broken instrument,
  4 passed-with-warnings. A stale binary or a dropped count is nonzero on
  purpose, so automation cannot shrug it off.

That is a serious piece of work, and it is why it reads as finished.

### What the harness lacks

**Tier 2 — "the keys actually route to the actions" — has never produced a
single valid result.** The pressing half exists: `jjprobe` implements `windows`,
`tree`, `focus`, `press`, `sweep` and `invoke`. But two things stop it:

- It needs an interactive desktop it can have to itself. `SendInput` injects
  into the foreground queue of the active desktop, so a human and the harness
  cannot share a machine — whichever box runs it is out of service while it
  runs. The harness's own README recommends the laptop; **that recommendation
  rests on a wrong premise and is corrected in Part two.**
- **#175: injection is broken on this machine anyway** — it reports
  `foregrounded true, routed empty`, even with a radio connected.

So the runner runs, reports, and passes, while the tier that would have caught a
dead `JJ ?` or a dead Alt+L has never once executed. **A green run today means
"the code compiles and the app starts", not "the interface works."** That is the
same shape as the SWR meter reading 1.008 — a true statement about a narrower
question than the one being asked.

**#176 is a correctness hole in the method itself**, not merely missing work:
UIA's TogglePattern bypasses Click-wired handlers, so a test can flip a checkbox
and change nothing while reporting success. That has to be solved before Tier 2
results mean anything, or Tier 2 will produce confident false greens.

**Tier 3 — "the radio actually did it" —** exists as `tools/RigSurface` and
needs both the desk and the radio, and composes with Tier 2, so it inherits both
blockers.

**Four gaps beyond the tiers:**

- **#185** — nothing tests the menu route or Command Finder route. Keys only.
  A command reachable by key and broken by menu passes today.
- **#183** — nothing compares the leader help text against the leader switch.
  That is precisely how `JJ ?` stayed dead while its help still advertised it.
- **#186** — no audit of which controls have context-sensitive help.
- **NEW, found tonight (#197)** — the transcript proves an utterance was
  *emitted*. It does not prove it was *heard*. Tonight's reflected-power warning
  was recorded correctly, with `rendered:true`, and you missed it entirely
  because it queued behind 25 words. Every automated check passed. The
  transcript has timestamps and an interrupt flag, so this IS checkable — a
  Critical warning that lands behind more than a second of pending speech should
  fail the run.

---

## Part two — the single highest-leverage unblock

**Correction from you, 2026-08-23: the ms-02 is the disposable machine and the
laptop is your daily driver.** I had that backwards, and it was load-bearing —
the previous draft of this section recommended moving the harness onto the
laptop, which is the one machine it should not live on.

Two different questions got collapsed into one. Separated:

**Which machine can be interrupted right now?** Whichever one you are not at.
That is already settled and needs no policy — you say you are stepping away, and
the standing keying authorization covers either machine. It is a per-run answer.

**Which machine should absorb the harness's damage?** The ms-02, permanently.
Tier 2 is machine-hostile by design: it takes the foreground, injects keystrokes
into whatever has focus, spawns app instances that can orphan (#21), and — found
yesterday — rewrites `%AppData%\JJFlexRadio\KeyDefs.xml` against live settings
whenever an agent launches its own build. `JJFLEX_CONFIG_DIR` now contains the
config half of that. It does not contain the foreground-theft half or the orphan
half. That residue belongs on the box you would not miss.

So the arrangement inverts: **the ms-02 is the test rig, the laptop is your
seat.** That is also the cheaper arrangement, because the ms-02 already holds the
worktrees, the build, the NAS mount and the radio on the LAN. The "one afternoon
of setup" I costed into the previous draft disappears — there is nothing to set
up.

**What the laptop is still genuinely needed for, and only this:** #175.
Injection reports `foregrounded true, routed empty` here, and one machine cannot
distinguish "injection is broken" from "injection is broken *on this box*". That
wants one build on a second machine, run once, as a diagnostic — not the laptop
becoming the harness's home.

---

## Part three — grouped by what is actually blocking

### Waiting on the PGXL amplifier

#125 amplifier support · #180 load declaration · #187 power JJ key ·
#192 automated sweeps · #193 APD · #195 drive curve · #139 TX peak watcher

These are not slow, they are queued. They close in a burst. Two can start now
regardless: #193's read-only APD readout needs no amplifier, and #180's load
declaration is UI that should exist before the first sweep runs.

### Waiting on a free desktop, not on code

#21 · #148 Tier 2 — these need the ms-02 to itself, which is now one sentence
from you rather than a second machine and a setup afternoon.

#175 — this is the one that genuinely wants a second machine, once, to tell a
broken mechanism apart from a broken box.

### Waiting on the radio and your presence

#27 transverter session · #56 bench session · #58 CW connect storm ·
#59 unrequested slices · #95 register/unregister · #108 empty mic profile ·
#160 slices you are not on · #163 transverter in the power rules ·
#164 acked-but-unapplied writes · #188 antenna tracing · #190 detect antennas ·
#191 frequency park-and-restore

### Waiting on a decision from you

#191 — the parking frequency. The beacon sub-band question is still open.
#147 — Simple and Rich, two definitions of the seven voices.
#114 — the three-note confirmation tone you proposed.
#116 — ducking under warnings, ruled but not built.

### Ready to build now, no blockers

Audio and earcons: #113 · #115 · #119 · #120 · #121 · #127 · #142 · #145 ·
#150 · #152

Correctness and code health: #83 · #93 · #109 · #110 · #133 · #135 · #136 ·
#137 · #140 · #141 · #143 · #176 · #181

Operator features: #122 · #123 · #124 · #151 · #153 · #154 · #155 · #156 ·
#157 · #158 · #161 · #162 · #177 · #178 · #182 · #184 · #194

Testing: #185 · #186 · #183 · #196 · #197

Deferred by design: #10 · #57

**#140 deserves calling out of that list.** It is flagged HIGH and reads: the TX
stream is created with no compression parameter while we send Opus. If that is
real it may be the root cause of the entire honest-tx-audio saga this branch is
named after. It is ready to investigate now and has been sitting since it was
filed.

---

## Part four — a proposed feature-lock gate for 4.2.0

This is a proposal for you to ratify, cut or reorder. Nothing below is decided.

**Must be true, because shipping without them risks the operator:**

- #148 Tier 2 actually running, with #176 solved so its greens mean something
- #196 gallop diagnosed and fixed — it affects every monitored transmission
- #140 investigated, since it may be the root cause of the branch's namesake
- #109 TX Controls opening onto something
- #194 instrumentation announcing itself
- #181 crash reports surviving a terminating crash
- #179 Don's build gate

**Should be true, because they are promises already made in help or UI:**

- #113 Earcon Explorer reaching all sounds · #121 Workshop read-only surfaces ·
  #183 help matching the switch · #184 Ctrl+F1 on Home · #186 context help audit

**Can follow 4.2.0 without embarrassment:**

- Everything amplifier-gated, the transverter work, WSPR and WebSDR, the CW
  vocabulary grammar, and the Simple/Rich voice split.

---

## Part five — the honest answer on closure rate

It should speed up, and unevenly.

The amplifier cluster closes in a burst when hardware arrives. The laptop clears
three tasks in an afternoon. Tier 2, once real, closes or de-risks a class
rather than an item.

What will NOT speed up is the discovery work, and that is correct rather than a
problem. Audio has been swept hard, which is why its remaining tasks are
refinements. Transmit is mid-sweep now, which is why it produced six tasks
today. The logger and the rest of the Jim-era code have not been swept at all,
and will behave exactly the same way when their turn comes.

The number to watch is not the total. It is whether the tasks being filed are
getting *smaller* — six filed today, and every one came from looking at
something real rather than from speculation.
