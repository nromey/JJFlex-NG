# radiocheck — the build test runner (#172)

On a build: spawn the just-built app, run the test tiers against it, tear
it down, and report — `result.json` for machines, `summary.txt` for
humans. "Radio check" is the on-air question this automates: is this
station actually getting out?

The runner is the delivery vehicle around two things that landed on
2026-08-21 and made it possible: the output transcript (#171 — the app
can run with speech, CW and earcons RECORDED instead of SOUNDED, so a
launch needs no audio device and no ears) and the capped meter firehose
(#170 — trace reads no longer silently miss events). The transcript's
contract lives in `Radios/OutputChannelRecorder.cs` and this runner
treats it as one.

## Invocation

```
& tools\radiocheck\radiocheck.ps1
```

Run from anywhere; it finds the repo root from its own location, or takes
`-RepoRoot`. Useful arguments:

- `-Config Debug|Release`, `-Platform x64|x86` — which build to test.
  Default Debug x64.
- `-ExePath PATH` — test a specific binary (an installed copy, a staged
  nightly). Pointing it at a missing file is the documented way to prove
  the broken-instrument path works.
- `-SkipUnit`, `-SkipSmoke` — skip a tier. Skipped is reported as
  skipped, never as passed.
- `-DeskFree` — the human's declaration that the interactive desktop is
  available; enables the foreground tier. See below.
- `-AllowRadioReach` — permit the smoke spawn even when an operator
  profile has auto-connect armed. Without it the smoke tier defers
  itself rather than let a test instance grab the radio.
- `-UnitFilter EXPR` — a `dotnet test --filter` for targeted runs. Count
  tracking is suspended for filtered runs so a legitimate subset can
  neither false-alarm nor lower the stored baseline.
- `-SettleSeconds N` (default 10), `-MarkerTimeoutSeconds N` (default
  45), `-OutDir`, `-StateDir`.

Exit codes: `0` pass · `1` test failures · `2` usage error · `3` broken
instrument · `4` passed with warnings (a dropped test count or a stale
binary — nonzero on purpose, so automation cannot shrug it off).

## The tiers

**Unit — `Radios.Tests`.** Pure in-process, no desktop, no radio, safe
any time. Run via `dotnet test` with a TRX logger, and graded from the
TRX, not the exit code, because `dotnet test --no-build` exits 0 with
nothing to test. A missing TRX or a discovered-test total of zero is
BROKEN INSTRUMENT — never green. The runner never passes `--no-build`.

**Smoke — spawn the just-built app.** Launched with `--no-render
--record=<run dir>\transcript.jsonl`: nothing sounds, and render-off
instances are exempt from single-instance forwarding
(`Application.Designer.vb`), so the spawn cannot poke an operator
instance that happens to be running. The tier asserts, in order:

- the transcript's `session-start` marker appears within the timeout and
  matches — right event, `render=false`, and OUR pid (a wrong-pid marker
  means the wrong instrument is being read);
- the process survives the settle window (a marker followed by death is
  a launch failure the marker alone would hide);
- if `jjprobe` is built, at least one top-level window is visible to UI
  Automation — running-but-invisible-to-UIA is a real failure for an app
  whose users are screen readers;
- teardown completes: polite `CloseMainWindow` first, `Kill` as
  fallback, and only ever on the process object the runner spawned —
  never by name, because the operator may be running his own
  `jjflexible.exe`;
- the pid is actually gone afterwards. A survivor is reported as the #21
  orphan-process shape, loudly.

The whole transcript is then summarised into the report — event counts
by type, whether a `session-end` was written, and a warning if startup
recorded zero speech events (the greeting should appear even
verbosity-gated).

One preflight guards the spawn: if any operator profile's
`*_autoConnectV2.xml` would auto-connect, the tier DEFERS instead of
spawning, because the instance would connect to the radio as a second
MultiFlex client — and `FlexBase.setupFromScratch()` sets `RFPower=100`
unconditionally on a radio it has never seen (Sprint 33 Track G). The
app has no `--no-autoconnect` switch today, so reading the same config
the app reads is the only honest gate. If such a switch is ever added,
this preflight should become a launch argument instead.

**Foreground — `JJFlexWpf.Tests`, gated behind `-DeskFree`.** That
project constructs real WPF dialogs on the interactive desktop — it is
what put unwanted windows on the operator's screen on 2026-08-20 — so it
is the same category of operation as a key sweep: it needs the desk.
Without `-DeskFree` it is reported as DEFERRED, prominently, in its own
summary section, so a deferral can never be read as a pass.

## The test count is the headline

The summary prints the discovered-test count before any per-tier detail,
compares it per branch against the previous run (state in
`%LOCALAPPDATA%\jjflex-radiocheck\state.json`), and treats a DROP as a
warning that survives into the exit code. This is not hypothetical
caution: on 2026-08-20 two Sprint 33 tracks went unmerged and the only
symptom was a test suite that got smaller and greener — the missing
files were test files, so everything left passed. A falling count reads
as success unless something says otherwise. This runner says otherwise.

Broken or filtered runs never update the stored baseline — a run that
discovered zero tests must not become the number the next run compares
against.

## Markers, start to finish

`run-start.json` is written before anything executes, so a run directory
with a start marker and no `result.json` is a runner that died — 
distinguishable from a runner that never ran. The app-side
`session-start` marker carries the same logic one layer down; both exist
because on 2026-08-21 three sweep runs produced zero valid data and
every failure looked exactly like a quiet success until the marker
discipline separated them.

## The Tier 2 foreground question, and the standing recommendation

Tier 2 proper — injecting real keystrokes through `jjprobe press` /
`sweep` — takes the desktop. The transcript solved audio contention; it
does not solve foreground contention, and this is a blind operator's
only machine. Three options were weighed:

- **A separate Windows session or headless VM on this machine.** Weakest
  option. `SendInput` injects into the foreground queue of the active
  desktop; a second local session can only become active by taking the
  physical console away from the operator (fast user switching
  disconnects the console session), and RDP-to-self does the same. A
  headless VM avoids that but is real infrastructure — a Windows
  license, virtualized audio, its own maintenance — standing idle next
  to hardware that already exists.
- **The laptop.** The standing recommendation. It is idle, it has a real
  desktop with a real foreground that nobody is using, and it is where
  #21's orphan-process bug lives and has never been reproduced — so
  running Tier 2 there attacks two problems with one setup: every run
  is also an orphan-reproduction attempt on the machine that grows them.
  The runner already supports it: the nightly debug zip is an existing
  transfer artifact, and `radiocheck.ps1 -DeskFree -ExePath <unzipped
  exe>` is the whole remote invocation.
- **A desk-free gate on this machine.** Implemented, as `-DeskFree`, and
  it stays load-bearing even after the laptop takes Tier 2 — because
  Tier 3 (radio in the loop) composes key presses with the radio-side
  observer, and the radio is at THIS desk. Radio-composed runs will
  always need the gate and the four-beat handshake ("gonna run a UI
  probe tool" / "cool have at it" / "done" / "keep going").

So: Tiers unit and smoke run any time, unattended. `JJFlexWpf.Tests` and
key sweeps run on the laptop by default, or here behind `-DeskFree`
after the handshake. Nothing in this file pretends the foreground
problem is solved by software; it is solved by hardware that is not
being used and a human saying "have at it".

## What it composes with, and what it does not do

- `tools/uia-probe` (`jjprobe`) is used read-only (`windows`) by the
  smoke tier, and is the pressing half of any future Tier 2 wiring. The
  runner does not reimplement any of it.

  **Before wiring Tier 2, read the `--op toggle` section of
  `tools/uia-probe/README.md` (#176).** Driving a checkbox through UI
  Automation's `TogglePattern` moves its state without raising `Click`,
  so on a `Click`-wired control the test does nothing and passes. A
  vacuous toggle is indistinguishable from a successful one: the control
  reports the new state, the tree read confirms it, and an assertion on
  that state is green. Never assert on the control's own state after
  driving it — assert on the consequence, or press a real `Space`.
- The runner never builds the app. It tests the build that exists, and
  says exactly which one — path, timestamp, version — flagging it STALE
  when any source file in the working tree was saved after the exe was
  built, and naming the offending file.

  Note which instrument that is. The first version compared the exe
  against commit timestamps, and they lie in both directions: judged
  against HEAD it cried stale after a docs-only commit that could not
  have changed the binary, and judged against the last source commit it
  still cried stale, because build-then-commit is the normal order and
  the measured exe was 59 seconds older than the commit whose source it
  contained. File mtimes answer the question that actually matters, and
  they also catch the commonest case of all, which commit times cannot
  see at all — an edit that was saved and never built.
- It never transmits, tunes, changes slices, or touches ATU memories; it
  has no radio connection at all, and the auto-connect preflight exists
  precisely to keep the spawned app from acquiring one.
- Runs and state live under `%LOCALAPPDATA%\jjflex-radiocheck\`, outside
  the repo, so a run never dirties `git status`.
- Known hazard the runner cannot remove: every spawned instance shares
  `%AppData%\JJFlexRadio\` with the operator's own install, so a build
  that carries config migrations runs them against his LIVE settings (a
  worktree build rewrote `KeyDefs.xml` this way on 2026-08-21, which is
  why `backup-appdata-to-nas.ps1` exists). When the build under test is
  at the same commit as the operator's, the risk is nil; when testing a
  build with migration changes, take an AppData backup first. A
  config-redirect switch in the app would retire this hazard for good.
