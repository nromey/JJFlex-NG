# Sprint 33 Track B — Tier 2, promote the driver and prove every key routes

**Worktree:** `C:\dev\jjflex-33b` · **Branch:** `sprint33/track-b`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Part one — rescue the tooling

The UI Automation probe that located the silent Audio Workshop on 2026-08-19 is
loose PowerShell sitting in a session scratchpad under `%LOCALAPPDATA%\Temp`. That
directory gets wiped. **The capability that solved the hardest UI bug of the
sprint currently has the lifespan of a temp folder.**

Promote it into the repository as a versioned tool with a real name, documented
arguments, and a stated contract. Suggested home: `tools/uia-probe/`.

Source material, in the session scratchpad
`C:\Users\nrome\AppData\Local\Temp\claude\C--dev-JJFlex-NG\b06e594e-0593-4553-93ae-bf30e08d38ff\scratchpad\`:

- `uia.ps1` — shared helpers: `Add-Type` against UIAutomationClient plus a
  `Win32Win` P/Invoke class with `EnumWindows`, `GetClassName`, and a `Force()`
  that does the `AttachThreadInput` dance to reliably foreground a window. That
  foregrounding logic is the non-obvious part; keep it.
- `uia-dump.ps1`, `uia-dumpsub.ps1` — tree dumping.
- `uia-act.ps1` — invoking elements.
- `uiawalk/Program.cs` — a C# walker.

**Read them, understand what each solved, then design the real thing.** Do not
transliterate five ad-hoc scripts into five ad-hoc commands. Decide whether the
promoted tool is C# or PowerShell and commit to it; a C# console tool is easier to
test and gives you real types, but PowerShell is what already works — your call,
state the reasoning.

**Minimum capability:** attach to a running `jjflexible.exe` by process id,
enumerate top-level windows, dump an automation subtree with names, control types,
automation ids and focusability, send keystrokes to a named window, and read back
what changed.

## Part two — prove every key actually routes

For every binding in `JJFlexWpf/KeyInventory.cs`, press it for real and assert
something observable happened.

**Why this is not paranoia.** On 2026-08-13 an `Alt+L` binding shipped completely
dead, one build after being added. The handler tested `e.Key == Key.L`, which is
NEVER true while Alt is held: WPF reports `Key.System` and puts the real key in
`e.SystemKey`. It compiled. It reviewed clean. The chord was simply never handled,
so the screen reader read the focused control and the key appeared to do nothing.

**A keyboard change is not verified by compiling.** This part of the track is the
machine version of "press the key."

**Give particular attention to the 29 commands bound to `Keys.None`** (task
#130). Nothing in the codebase today distinguishes "menu-only on purpose" from
"nobody ever got round to assigning a key." Your harness should be able to tell
them apart, or at minimum produce the list so a human can.

**Watch for the Alt-chord trap specifically.** Any binding involving Alt is a
candidate for the `Key.System` mistake. If your harness can detect that pattern
statically as well as dynamically, do both — belt and braces on a bug that has
already shipped once.

## You are also the driving half of Tier 3 — agree the seam with Track C

Track C observes the radio. **You press the keys.** The highest-value test in the
whole sprint is the composition: a key pressed in the REAL running JJ Flexible,
and the radio then asked whether it actually did the thing. Noel's own framing —
*"exercise a hotkey or action, see if the radio did what it was supposed to."*

This is not optional politeness between tracks. It is the only arrangement that
proves the full chain, because a test tool driving the radio on its own
connection is a second MultiFlex client inspecting its own per-client state, and
would pass while proving nothing.

So your harness needs a clean "press this, and tell me when it has settled"
primitive that Track C can call. **Agree that seam with Track C early and report
what you agreed**, so the merge does not discover two different ideas of it.

## THE HANDSHAKE — this track drives the live UI, so this is binding

Noel is blind, uses NVDA, and is at the keyboard. On 2026-08-19 an agent drove
the interface while he was typing and the two collided. His rule, verbatim:

> *"Just simply, 'gonna run a UI probe tool' then I say 'cool have at it' and then
> 'Done' then 'keep going' from me."*

So: **full stop and ask before any run that drives the live UI. Full stop and
report when it finishes.** One authorisation covers ONE run — do not generalise a
yes into a standing permission. If he reports interference, stop immediately.

This means you cannot fire off UI runs freely. Batch your work: get as much built
and reasoned as possible between runs, then ask once for a substantial run rather
than repeatedly for small ones.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Do not touch files outside your worktree.
- You may add a project to `JJFlexRadio.sln`. Other tracks do too — expected,
  additive, visible.
- **Do not fix what you find.** Record findings; repairs get triaged one at a
  time afterwards.

## Commits

`Sprint 33 Track B: <description>`. Commit per meaningful chunk.

## Completion report

State: where the tool now lives and how it is invoked; how many bindings were
pressed and how many produced no observable effect; the `Keys.None` verdict; and
whether the Alt-chord trap exists anywhere else in the current key map.

---

## AUTHORISATION IS BROKERED — do NOT ask Noel directly

**Decided by Noel, 2026-08-20.** Five tracks want either the radio or the live
desktop, and five agents interrupting him independently would be worse than the
collision the handshake exists to prevent.

**So: when you are ready for a run that needs the radio or drives the UI, STOP
and report "ready for a radio run" (or "ready for a UI run") to the orchestrator,
with exactly what you intend to do and roughly how long it takes.** Do not ask
Noel. Do not proceed on your own initiative.

The orchestrator batches ready tracks, asks Noel once, runs them back to back,
and reports done. You will be told when your run is authorised and when it is
over.

**Priority when tracks contend for the 8600: G first** — a build going to Don
depends on its answer — then C, then D, then K.

**While you wait, keep working.** Do everything that does not need hardware:
build the harness, write the code, reason it through. Arrive at your run with the
maximum settled in advance, because run time is the scarce resource, not compute.
