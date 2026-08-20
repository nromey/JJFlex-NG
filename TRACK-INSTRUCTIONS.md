# Sprint 33 Track H — the meters panel: two confirmed breaks and a model that is already built

**Worktree:** `C:\dev\jjflex-33h` · **Branch:** `sprint33/track-h`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Scope: six tasks, one panel

**#129** the panel builds its slot UI once at construction and never resyncs.
**#131** the meter slot Test button starts a tone that never stops. **#124** the
meter UI is stuck on an 8-value enum while the scalable model sits unused beside
it. **#126** `Ctrl+M` does two jobs and is the only way into the panel. **#127**
the meters expander is the only one on Home with no expand/collapse earcon.
**#138** the Earcon Scratchpad mutes the radio, defeating the bench it now
contains.

They are one track because they are one surface. Split, they would collide on
every file.

## Start with #129 and #131 — both are CONFIRMED breaks, not suspicions

**#129: slots exist with no controls.** The panel builds slot UI once at
construction and never resyncs, so a slot added later has no controls in it. For
a blind operator that is a tab stop that announces nothing and does nothing —
indistinguishable from the application being broken, which in that moment it is.

**#131: a tone that never stops.** The slot Test button starts a tone with no
stop, and per the task the normal route into the panel *guarantees* you hit it.
A stuck tone over receive audio is not a cosmetic bug; it makes the radio
unusable until the app is restarted.

**Fix these two first.** Everything else in this track is improvement; these two
are damage.

## #124 — the model is already built, the UI just never moved onto it

Sprint 32 Track A shipped `MeterInventory` on `FlexBase`: every meter the radio
declares, with source, units, range, value and staleness. The meters UI still
runs on an 8-value `MeterType` enum that flattens all of it away.

**This is the lossy-adapter pattern.** `FlexLib.Meter.DataReady(Meter, float)`
carries full identity; `FlexBase` flattens it into the enum, and the flattened
form is then re-declared twice more up the stack. Every layer loses information
the layer below had.

**Move the UI onto `MeterInventory`.** The radio names its own meters — use those
names rather than a fixed set of eight. Related: the radio publishes over a
hundred meters and the operator can currently reach eight of them.

**Coordinate with Track D**, which is verifying analyzer facts that come off the
same meter plumbing. If you change how meters are read, say so in a progress
report so D is not measuring a moving target.

## #126 — Ctrl+M does two jobs

Opening the panel and enabling meter tones are one key today, so you cannot look
at your meters without also switching your audio. Two separate intents that
happen to be adjacent. Split them, and mind the keyboard audit below.

## #127 and #138 — small, and both about sound

**#127:** the meters expander is the only expander on Home with no
expand/collapse earcon. Consistency matters more than the sound itself; an
operator who has learned that expanders make a noise reads silence as failure.

**#138:** the Earcon Scratchpad mutes the radio so earcons are audible — which
defeats the point now that the scratchpad is a bench for judging earcons AGAINST
BAND NOISE. Muting removes the exact thing you need to hear them against.

## THE COLLISION YOU MUST NOT CAUSE — read this before touching any earcon file

**Track F owns `EarconVoices.cs` and the voice definitions in `EarconPlayer.cs`.**
It is rebuilding the whole voice table for the sine-versus-modern setting.

**You may ADD earcon CALL SITES (#127). You may NOT edit voice definitions,
`EarconVoices.cs`, or `EarconPlayer`'s voice choices.** If you conclude one
should change, REPORT IT — do not do it.

**And the partial-class trap specifically.** `AudioWorkshopDialog` is split
across several partials. Track F works in `AudioWorkshopDialog.Earcons.cs`; the
meters work lives in `AudioWorkshopDialog.MeterInventory.cs`. **Two tracks adding
a handler with the same name in different partials produces ZERO git conflict and
a broken build** — that happened in Sprint 32 and cost a build. Name anything new
distinctively enough that a collision is impossible, and do not add members to a
partial you do not own.

## Keyboard audit — #126 changes a binding, so this is definition of done

Splitting `Ctrl+M` means `docs/help/md/keyboard-reference.md`, Command Finder
search keywords, F1 context help, and a changelog line. **And PRESS THE KEYS on a
real build** — an `Alt+L` binding shipped completely dead in 2026-08-13 because
the handler tested `e.Key == Key.L`, which is never true while Alt is held.
Compiling is not verification.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Do not touch files outside your worktree.
- Noel is blind, at the keyboard, and may be operating the radio. Anything that
  plays audio or takes focus collides with him — coordinate before a run.
- Track A is writing tests that construct this panel. It asserts INVARIANTS, not
  specifics, so a redesign should not break it — but say in your report what you
  changed structurally.

## Commits

`Sprint 33 Track H: <description>`. Commit the two confirmed breaks separately
and early so they can be merged ahead of the larger rework if needed.

## Completion report

State: the fix for each of the six, or why not; what the meters UI now reads from
and how many meters the operator can reach; the new key assignments and
confirmation you pressed them; and anything you concluded should change in
Track F's files, reported rather than done.
