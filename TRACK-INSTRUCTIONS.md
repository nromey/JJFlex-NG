# Sprint 33 Track J — three surfaces that are dead, and the script that ships the build

**Worktree:** `C:\dev\jjflex-33j` · **Branch:** `sprint33/track-j`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Scope

**#133** `build-debug.bat` cannot zip on this machine, and blames a running app
for it. **#109** TX Controls opens onto nothing — the delegate was declared,
called, and never assigned. **#132** the destructive remove option is unreachable
by Tab, so confirming commits the safe default. **#110** the unwired-surface
inventory, which needs a better instrument than grep.

All four are ALREADY DIAGNOSED. This track is repair, not investigation — with
one exception noted under #110.

## DO #133 FIRST — it gates shipping a build to Don today

**The goal of this entire sprint is confidence to send Don a build.** If the
script that packages that build does not work, everything else is academic.

`build-debug.bat` zips with `Compress-Archive` (around line 238) and, on failure,
prints `ERROR: zip failed (is jjflexible.exe locked by a running instance?)`.

**That error message is a guess, and the task says it is the wrong guess.**
Diagnose the actual failure rather than trusting the message — an error string
that names a cause nobody verified is how a false explanation survives for
months. `Compress-Archive` has real limitations that bite exactly this shape of
input: the publish tree is roughly 180 to 190 MB across 364 files, self-contained
with the whole .NET runtime in it.

**Fix the cause, and fix the message.** If the message can only ever be a guess,
it should say so rather than asserting a lock.

**A note on what a nightly IS**, so the fix does not quietly change it: a nightly
build is a DEBUG build, stamped with the full four-part version, and the version
comes from the exe's `FileVersion` — computed from `<Version>` in the vbproj plus
`git rev-list --count HEAD` plus `BUILDNUM_OFFSET`. Release installers go through
`build-installers.bat` into a different folder in the same historical tree. Do not
conflate them.

## #109 — TX Controls opens onto nothing

The delegate was declared, called, and never assigned. So the surface opens and
is empty — for a blind operator, a window that announces nothing and contains
nothing, with no way to tell a bug from a missing feature.

Find the declaration and every call site, work out what it was meant to be
assigned to, and assign it — or, if the surface was genuinely abandoned, say so
and recommend removing the route in rather than leaving a door onto nothing.
**Report which it is; do not silently pick.**

## #132 — the destructive option is unreachable by Tab

Already diagnosed. The remove dialog offers two radio buttons — remove the radio
and its settings, or remove just the radio — and **the destructive one cannot be
reached by keyboard**, so confirming always commits the safe default.

**Two things wrong, and fix both.** The reachability bug is the obvious one. The
subtler one: an option the operator cannot choose should not be presented as a
choice. A dialog that offers two options and can only ever perform one is lying
about what it does.

Noel also observed the edit box in that dialog should be read-only and reads as
editable. Worth fixing while you are in there.

**This is a destructive-action dialog, so be careful.** Making the destructive
option reachable means it can now actually be chosen. Confirm the wording makes
the consequence unmistakable before it becomes selectable — and that wording
needs Noel's approval.

## #110 — inventory the unwired surfaces, properly

The task itself says a naive grep produces mostly phantoms, and that is the whole
point: **the right instrument for "does this surface actually contain anything"
is a tree walk, not a text search.** Track A is building exactly that.

**So do not brute-force this one.** Do the static half — find delegates declared
and never assigned, handlers referenced and never wired, the #109 pattern
generalised — and produce a CANDIDATE list with your confidence in each. Then say
plainly in your report that confirmation belongs to Track A's harness.

**A candidate list honestly labelled as candidates is the deliverable.** A
confident list full of phantoms is worse than nothing, because someone will spend
a day chasing them.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- User-facing prose — especially the destructive-remove wording — needs Noel's
  approval before it ships.
- Do not touch files outside your worktree.
- **Do not touch earcon voice definitions.** Track F owns `EarconVoices.cs` and
  the voice choices in `EarconPlayer.cs`. If a fix here needs a sound, add a call
  site or report it.
- `AudioWorkshopDialog` is split across partials owned by other tracks. **Two
  tracks adding a same-named handler in different partials produces zero git
  conflict and a broken build** — that happened in Sprint 32. Name anything new
  distinctively, and do not add members to a partial you do not own.

## Commits

`Sprint 33 Track J: <description>`. Commit the `build-debug.bat` fix first and on
its own — it may need to be cherry-picked ahead of the merge train if a build has
to go out today.

## Completion report

State: what was actually wrong with the zip and what the error message says now;
whether TX Controls was wired or recommended for removal; the remove-dialog fix
and the wording you want Noel to approve; and the #110 candidate list with
confidence levels and an explicit note that Track A confirms it.
