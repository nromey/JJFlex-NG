# Sprint 32 Track A — The meter inventory (foundation)

**Worktree:** `C:\dev\jjflex-32a` · **Branch:** `sprint32/track-a`
**Full design:** `docs/planning/active/elmer-meter-pileup.md`, section "Track A".
**Read that first.** It carries the reasoning; this file carries the contract.

## You are the blocking track

Tracks B, C and D cannot start until you report **Phase 1 committed**. Track E is
waiting specifically on your A4 commit. Get Phase 1 done and reported before you
touch Phase 2 — a fast Phase 1 unblocks four other agents.

## Phase 1 — do these four, commit each separately, then REPORT

### A1. Apply the FlexLib meter-list patch

`MIGRATION.md` has a section headed **"Not yet applied: a public accessor for the
meter list (reviewed 2026-08-16)"**. It contains the exact patch, already
reviewed. **Apply it as written. Do not redesign it.**

It goes in `FlexLib_API/FlexLib/Radio.cs` next to `FindMeterByName`:

```csharp
/// <summary>JJFlex patch: enumerate the radio's meter inventory.</summary>
public ImmutableList<Meter> GetMeters()
{
    lock (_meters)
        return _meters.ToImmutableList();
}
```

Same commit, all of it:
- Mark it `// JJFlex patch`, the way the VitaSocket edits in that file are marked.
- Add it to MIGRATION.md's numbered reapply list (it becomes item 11) and move the
  "Not yet applied" section into the applied list, dated today.
- **Delete the reflection block inside `FlexBase.traceMeterInventory`.** It reaches
  the same private field by `GetField("_meters", NonPublic | Instance)`. Two routes
  to one field is how one of them rots. The method keeps its job — tracing the
  inventory when the set changes — only its access route changes.

This is a vendor-tree edit. It is purely additive, so a future 3-way merge cannot
conflict with it; it will simply need re-adding if a merge takes the vendor file
wholesale. That is why MIGRATION.md's reapply list matters.

### A2. Identity-preserving meter subscription in `FlexBase`

FlexLib exposes `Meter.DataReady(Meter meter, float data)` — a generic per-meter
event carrying the meter itself. `FlexBase` currently subscribes to ten *named
convenience* events (`MicDataReady`, `SWRDataReady`, …), discards the meter
identity, and re-emits `MeterType`, an 8-value enum.

Subscribe generically and raise a new event that carries the `Meter`.

**DO NOT delete `MeterType` or `MeterChanged`.** `MeterToneEngine` and other
callers read them and **Track B is the only track permitted to retire them**.
Leaving the old path alive as a shim is deliberate. Removing it here breaks Track
B mid-flight.

### A3. The `MeterInventory` service

**Put it in `Radios`, not `JJFlexWpf`.** The layering runs `Radios` *below*
`JJFlexWpf`, so anything placed in `JJFlexWpf` is unreachable from the radio layer.

Per meter, carry: name, `Meter.Source` (`SLC` / `AMP` / `HAAPI`), `SourceIndex`,
units, range low and high, current value, and a **last-update timestamp**.
Staleness is a reading, not an absence — Track C's rules depend on being able to
say "this meter stopped updating."

Expose the inventory **partitioned by source and source index**. That is how
amplifier, tuner and per-slice meters separate, and `Meter.Source` already tags
every meter this way. Track D needs no new concept, only this.

**Change notification is the load-bearing part, do not skip it.** FlexLib raises
NOTHING when a meter appears, and the list GROWS DURING REGISTRATION — an 8600
reported 102 meters, and an early snapshot catches eleven with the TX-side ones
still to arrive. `traceMeterInventory` already does a count-and-set comparison for
exactly this reason. Do that once, centrally, in the service, and raise an event
when the set changes. Everything downstream binds to the event rather than
sampling at construction.

### A4. Split `AudioWorkshopDialog.xaml.cs` — Track E is waiting on this

4,866 lines in one file, already declared `partial`, never split. `SettingsDialog`
in the same folder is already split into six per-tab partial files. Follow that
existing convention: one file per tab, `AudioWorkshopDialog.<Tab>.cs`.

**Pure mechanical move. No behaviour change. Its own commit.** Three tracks add a
Workshop tab this sprint and a fourth restructures its navigation; without this
split that is four agents editing one region.

**Do this commit EARLY in Phase 1** — ideally first — and say so in your report,
because Track E is blocked on it specifically and on nothing else of yours.

## REPORT NOW — then continue to Phase 2

Report: each commit SHA, confirmation that the reflection is deleted, the
`MeterInventory` public surface (so B, C and D can code against it), and
explicitly that A4 has landed.

## Phase 2 — after you have reported

### A5. Meter Inventory tab in the Workshop

Read-only. Which meters this radio actually has, what each reads now, grouped by
source, with staleness shown. Ship this BEFORE any decision tree exists — it
closes the invisible-meter-list finding (commit `d5aecf2b`) and produces the real
data needed to write good rules.

### A6. Copyable text export of the inventory

Seeds Track C's evidence block; useful alone.

## You own these files — no other track touches them

`FlexLib_API/FlexLib/Radio.cs` (the patch), `MIGRATION.md`, `FlexBase`'s meter
subscription section, the new `MeterInventory`, and the `AudioWorkshopDialog`
partial-file split.

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.** A clean `git merge` with zero
  textual conflict still broke the build in Sprint 30 because one track moved a
  symbol another was told to reuse. Git cannot see that class of collision.
- **NO tables, diagrams or ASCII art** in any doc or comment you write. Prose or
  bullets. The primary user is blind and uses NVDA.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error" — that matches warning prose and has produced a false "it built"
  report before.
- Commit per logical chunk with `Sprint 32 Track A: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks. If the build reports
file-lock errors, that is a running app, not a code defect.

## Definition of done

Phase 1 reported with SHAs; Phase 2 committed; clean x64 build verified by the
error-count line; no behaviour change in A4; `MeterType`/`MeterChanged` still
present and working.
