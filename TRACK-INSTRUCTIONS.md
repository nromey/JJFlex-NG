# Sprint 32 Track H — Profiles and persistence

**Worktree:** `C:\dev\jjflex-32h` · **Branch:** `sprint32/track-h`
**Full design:** `docs/planning/active/elmer-meter-pileup.md`, section "Track H".
**Read that first.** It carries the reasoning; this file carries the contract.

You share no files with the meter tracks. Nothing blocks you; start immediately.

## H1. Understand what is actually broken before changing anything

**The slices are not ours.** JJ Flexible contains **zero** slice-creation calls —
no `RequestSlice`, no `CreateSlice`, no `new Slice(`. Verify that yourself before
you do anything else. The radio restores its slice layout from its **global
profile** on connect. A live slice release changes nothing that survives.

Slice create and delete **work correctly**. Do not "fix" them. The original task
framing said "slices are unmanageable" and that was wrong — an earlier session's
reading of the complaint, not the complaint. Noel: *"I never thought that slice
creation was a problem, or deletion, slice management worked, it just didn't
stick."*

## H2. The actual defect: nothing says the change is provisional

Releasing a slice succeeds, sounds successful, and is silently discarded at
disconnect. The persistence step **exists and is fully wired**:

Radio menu > Profiles (`NativeMenuBar.cs:1296`, `ShowManageProfilesDialog`)
→ `OnSave` → `Rig.SaveProfile(profile, immediately: true)` (`NativeMenuBar.cs:2583`)
→ `FlexBase.SaveProfile` (`FlexBase.cs:11702`)
→ `theRadio.SaveGlobalProfile(name)` (`Radio.cs:8621`)
→ radio command `profile global save "<name>"`

Noel — who knows this codebase better than anyone — could not find it. **That is
the bar the fix has to clear.**

### Evaluate `AutoSaveProfile` FIRST, and know what you can and cannot learn

`Radio.AutoSaveProfile(string state)` exists (`Radio.cs:8616`, command
`profile autosave "<state>"`) and JJ Flexible never calls it. It is the radio's own
save-on-disconnect concept and may be the right answer instead of anything we
build.

**Its semantics are radio-side and undocumented. Our method is one line that sends
a command, so reading our source proves nothing about what the radio does with it.**
That is a bench observation. **Report what you need observed; do not guess and
build on the guess.**

### Two traps in the obvious design — Noel raised the idea and spotted the flaw himself

He suggested offering to save on change: *"generally, if you tune the radio or any
radio, when you turn it off, it saves stuff. Of course, for connect that won't be
the case."*

- **The disconnect moment is not the power-off moment.** A standalone radio
  powering down has one operator and one state. A networked radio does not power
  down when a client leaves, and under MultiFlex another operator may still be on
  it. The global profile is global; the departure is per-client. **Auto-saving on
  disconnect can capture another operator's slice layout and silently overwrite a
  profile with a state this operator never chose.** Any automatic save must be
  gated on being the only client, or must not exist.
- **A prompt at exit is the wrong instrument even single-client.** "Save changes
  before disconnecting?" fires whether or not anything meaningful changed, so
  operators learn to dismiss it reflexively — and one day it eats the change they
  wanted. A prompt trained to be dismissed is worse than none, because it creates
  the belief that the operator was asked.

**BUILD THIS: a receipt at the moment of change, not a question at the moment of
departure.** "Slice D released — this will not survive disconnect unless you save
the profile" costs one utterance, arrives while the operator has full context, and
demands no decision. Notify where there is context; prompt only where there is a
real choice.

Noel on the prompt: *"I'm not sure I'd do this."* Trust that. It does not cost the
receipt.

## H3. Un-stub profile creation

In `NativeMenuBar.cs` around 2550, the Profiles dialog wires Select, Save and
Delete for real, but:

```
OnAdd    -> SpeakAfterMenuClose("Profile creation not yet available")
OnUpdate -> SpeakAfterMenuClose("Profile update not yet available")
```

So **Save overwrites the profile you are on.** There is no Save As, and an
operator cannot keep a four-slice layout and build a one-slice layout beside it.

Two things to fix, and the second matters as much:

1. Build the missing verbs.
2. **Stop presenting verbs that announce their own absence only after the operator
   has navigated to them and pressed.** A sighted user often sees a greyed control
   and does not try; a keyboard and screen-reader operator pays the full
   round-trip first. Disable, hide, or label — any of the three beats this.
   **Sweep for other instances of the pattern while you are in here and report
   what you find.**

## H4. The CW slice vocabulary — Noel has specified it, build to the spec

His ruling, and it **supersedes** the `ReferenceEquals(s, theRadio?.ActiveSlice)`
guard currently at `FlexBase.cs` ~6151:

- **On connect, and on any bulk slice change: a CENSUS.** Format `<used>/<total>`
  — three slices open on a 4-slice radio sends `3/4`; a full radio sends `4/4`.
  **NOT one message per slice.**
- **On a mode change, or on moving to another slice: `SL <letter> <mode>`.**
  `SL A USB`; change the mode and it sends `SL A LSB`.

Both formats are **approved copy**. Do not reword them.

Take TOTAL from `Radio.MaxSlices` — **not** `AvailableSlices`, which is remaining
capacity and is the wrong number for a denominator. `Slice.Letter` is already a
FlexLib property; `FlexBase.ActiveSliceLetter` (`FlexBase.cs:7860`) already uses
it, so there is no index-to-letter mapping to write.

**Why the guard failed, and why you should not try to repair it.** It announced a
per-slice property during a **bulk state replay**, so four individually-correct
announcements answered a question nobody asked. Filtering picks one member and
calls it representative, which is arbitrary. Summarising describes what actually
happened. Replace, do not repair.

**One unexplained contradiction, recorded so you do not trip on it:** static
reading of `_slices.Add` (appends) plus `Radio.ActiveSlice` (returns the FIRST
Active slice) predicts ONE announcement, and Noel heard four. **Do not invent a
mechanism.** If cheap, log what `theRadio.ActiveSlice` returns at each `DemodMode`
alongside `s.Index`, `s.Active`, and `s.ClientHandle` versus
`theRadio.ClientHandle`. Optional now that the guard is being replaced.

## H4a/H4b. The exit farewell — TWO defects, fix in this order

Noel's evidence, each repeated twice:
- **Not connected, close:** `73 SK dit` — nearly complete, missing only the final
  dit. The app sends `73 <SK> ee`, which has **two** trailing dits.
- **Connected, close with Alt+F4 without disconnecting:** **"dah dah"** and nothing
  else. `--...` is the digit 7, so that is the first two elements of a ~2-second
  string.

**One mechanism, two symptoms.** `FlexBase` ~2085 on the disconnect path:

```csharp
_ = ScreenReaderOutput.PlayCwSK?.Invoke();
ScreenReaderOutput.SkAlreadyPlayedThisSession = true;
```

The `_ =` **discards the task** — nothing awaits it. Then the flag makes
`ApplicationEvents.MyApplication_Shutdown` skip its own
`PlayCwSK.Invoke().Wait(5000)`. **The wait lived only in the path the guard
suppresses.** So the double-fire guard — added for a real complaint, and it works
— does not merely prevent a second farewell, it removes the only code that was
waiting for the first one.

**Fix order, and it matters:**

1. **Make the disconnect path await its own farewell**, bounded the way Shutdown
   already bounds its call. Whoever plays it owns waiting for it. Do not move the
   wait around — the next path that plays SK would inherit the same trap. There
   are two today; assume a third.
2. **Then fix the completion contract.** `EarconCwOutput.PlayElementsAsync`
   (`EarconCwOutput.cs:225-232`) completes on a **computed** duration:
   `int waitMs = totalMs + 50; await Task.Delay(waitMs...)`. It never asks the
   device whether the buffer drained, so `Wait(5000)` is **satisfied early** rather
   than expiring, and `EarconPlayer.Dispose()` on the next line tears down the
   device while the tail is still in hardware. 50 ms is less than a typical NAudio
   output buffer.

**RAISING THE TIMEOUT CANNOT FIX EITHER OF THESE.** The window is already 5000 ms
for a sub-second string. Anyone who tries `Wait(10000)` will see no change and
wrongly conclude the diagnosis was wrong. **Do not start there.**

**Do not delete the double-fire guard.** That restores the doubled 73 Noel
complained about — trading a truncated farewell for a repeated one.

Fix by **observing drain**: query playback position, or wait on `PlaybackStopped`,
before resolving the completion. A trailing silence pad would mask this one string
but leaves every other exit-time utterance exposed; the defect is in the
completion contract.

**Also sweep** the disconnect and shutdown paths for other discarded tasks
(`_ = ...Invoke()`) that start work and never wait. The farewell is just the one
loud enough to notice. **Report what you find; this is a check, not a claim.**

**Verification is TWO cases:** close while connected, and close while not
connected. A fix tested only disconnected looks complete and leaves the worse bug
standing — which is how this shipped.

## H5. #70 — repeat-last-message becomes a short history

`_lastMessage` is a single string. The coalescer's `_lastByKey` is per-key dedup
state cleared on urgent flush and unusable as history.

This was stranded in Sprint 30's Track F purely because of a speech-core
quarantine. **That quarantine is DROPPED for this sprint** (Noel, 2026-08-19). No
track holds an exclusive lock on `Radios\Speech\*` or `ScreenReaderOutput.cs`.

## You own these files

`Profile_t`, `ProfileReporter`, the Profiles dialog wiring in `NativeMenuBar`, the
slice and profile sections of `FlexBase`, `EarconCwOutput`, `MorseNotifier`, and
`Radios\Speech\*` / `ScreenReaderOutput` for #70.

**Track G also edits `NativeMenuBar`** for navigation. Different regions, but
**flag any `NativeMenuBar` edit in your completion report** so the merge knows to
look. Track A also edits `FlexBase` — its **meter subscription** section, not
yours. Stay out of the meter code.

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.** A clean `git merge` with zero
  textual conflict still broke the build in Sprint 30 for exactly this reason.
- **NO tables, diagrams or ASCII art** in anything you write. Prose or bullets.
  The primary user is blind and uses NVDA.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error" — that matches warning prose.
- **Do not invent user-facing wording beyond what is approved above.** Propose it
  in your report instead.
- Commit per logical chunk with `Sprint 32 Track H: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks.

## Definition of done

The provisional-change receipt exists; profile creation un-stubbed and the
stub-verb pattern swept; the CW census and per-slice announce built to spec; both
farewell defects fixed in the stated order and **verified by ear in both the
connected and disconnected cases**; #70 done; clean x64 build verified by the
error-count line. **Report what needs bench observation rather than guessing at
it.**
