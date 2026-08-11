---
status: spawn-ready — Noel's go required to create branch + worktree
spec date: 2026-05-08
parent branch: sprint28/home-key-qsk
proposed branch: sprint28/bug-bundle-design-followup
proposed worktree: C:\dev\jjflex-bug-bundle (worktree was cleaned up after the original bundle merge — needs re-add)
estimated LOC: ~80-100 across 4 files
---

# Sprint 28 Design Followup — Track Instructions

## Why this track exists

The 2026-05-04 sprint28 bug-bundle pull-doc surfaced two DESIGN entries from the 2026-04-28 morning briefing that were deferred pending Noel's call:

- **(a) `RunsWithoutRadio` opt-out for `KeyTableEntry`** — let specific commands run with no radio connected.
- **(b) Action-aware no-radio announcement** — replace the generic *"JJ Flexible Home, no radio connected"* with *"Unable to [change band], JJ Flexible Home no radio connected"* sourced from `KeyTableEntry.ShortActionLabel`.

Noel ACK'd both 2026-05-08 evening. This track ships them together as a single ~80-100 LOC merge to `sprint28/home-key-qsk` (4.1 line per `memory/project_main_branch_41_posture.md` — main is 4.1 working branch until 4.2 ship gates clear).

## Spawn commands (Noel runs when ready)

```batch
REM From C:\dev\JJFlex-NG (the main checkout)
git fetch origin
git worktree add ..\jjflex-bug-bundle sprint28/home-key-qsk -b sprint28/bug-bundle-design-followup
copy docs\planning\active\sprint28-design-followup-track-instructions.md ..\jjflex-bug-bundle\TRACK-INSTRUCTIONS.md
```

Then in a fresh CLI session:

```
cd C:\dev\jjflex-bug-bundle
```

Prompt: `Start Sprint 28 Design Followup track from TRACK-INSTRUCTIONS.md`

## Scope — Item (a): `RunsWithoutRadio` flag

### What

Add a `bool RunsWithoutRadio` property to the `KeyTableEntry` type. Default `false`. The dispatcher's no-radio guard (currently in `ApplicationEvents.vb:273` and `JJFlexWpf/KeyCommands.cs:1677-`) consults this flag *before* speaking the no-radio announcement and consuming the keystroke. If the entry sets `RunsWithoutRadio = true`, the dispatcher routes the command normally; the *handler* is then responsible for deciding what to do when there's no radio.

### Files

- **`Radios/KeyCommandTypes.cs`** — add `public bool RunsWithoutRadio { get; init; }` to `KeyTableEntry`.
- **`JJFlexWpf/KeyCommands.cs:107`** — wherever `KeyTableEntry` instances are constructed for the two opt-in commands (see opt-in list below), set `RunsWithoutRadio = true`.
- **`JJFlexWpf/KeyCommands.cs:1677-`** — dispatcher guard checks `entry.RunsWithoutRadio` *before* calling `SpeakNoRadioConnected`. If true, fall through to the handler.
- **`ApplicationEvents.vb:273`** — same guard pattern. Mirror the WPF behavior.
- **`globals.vb` — `WriteFreq` handler** — when invoked with no radio, the handler now needs to handle that case explicitly: easter-egg matching (e.g., `cqtest`) still runs; "just typing a frequency to remember it" still works; only at *apply* time does it speak "no radio, can't tune" if the user actually tries to tune.

### Opt-in list (commands that set `RunsWithoutRadio = true`)

- **`SetFreq`** — primary motivation. Lets `Ctrl+F` open the frequency-input dialog with no radio so easter eggs and just-type-and-remember workflows still function.
- **`ShowMemory`** — view saved memories without a connected radio.

That's the locked list. Do **not** opt other commands in without explicit Noel ACK; most Radio-scope commands genuinely need a radio (band keys, tune, RIT/XIT toggle, etc.).

### LOC estimate
~30-50 LOC.

## Scope — Item (b): Action-aware no-radio announcement

### What

Today's `SpeakNoRadioConnected()` helper (`Radios/ScreenReaderOutput.cs`) speaks one of three verbosity-scaled strings: "JJ Flexible Home, no radio connected" / "Home, no radio" / "No radio connected". Extend it to take an optional action label so the failure announcement names what the user just tried to do.

### Behavior

- **Verbose:** *"Unable to [change band], JJ Flexible Home no radio connected."*
- **Terse:** *"[change band], no radio."*
- **Critical-only:** *"[change band], no radio"* (or whatever the agreed Critical phrasing settles on — match existing pattern in the helper).

If the dispatcher passes no label (entry has no `ShortActionLabel`), fall back to today's plain-form output.

### Files

- **`Radios/ScreenReaderOutput.cs`** — extend `SpeakNoRadioConnected` signature to take `string? actionLabel = null`. Build the verbosity-scaled string with the label prepended when provided.
- **`JJFlexWpf/KeyCommands.cs:1677-`** — when the dispatcher hits the no-radio path, pass `entry.ShortActionLabel` into the helper.
- **`ApplicationEvents.vb:273`** — mirror the WPF call.
- **`Radios/KeyCommandTypes.cs`** — `ShortActionLabel` field already specced in `memory/project_short_action_labels_vocabulary.md`. **Verify whether it has been added to `KeyTableEntry` yet.** If not, add it as part of this track (the vocabulary work is a Sprint 29 priority, but the field is cheap to add now and is the load-bearing dependency for this announcement). Populating labels for all ~80 commands is *not* in this track's scope — populate only the ~10-15 commands that need the no-radio guard tested. Sprint 29 picks up the bulk of label population.

### LOC estimate
~50 LOC.

## Combined estimate
~80-100 LOC across 4 files (`KeyCommandTypes.cs`, `KeyCommands.cs`, `ApplicationEvents.vb`, `ScreenReaderOutput.cs`, possibly `globals.vb` for `WriteFreq`).

## Test matrix (run before reporting completion)

### Item (a) — `RunsWithoutRadio`
- [ ] No radio + `Ctrl+F` (SetFreq) → freq-input dialog opens silently. No "no radio" announcement.
- [ ] In freq dialog, type `cqtest` and confirm → easter egg fires.
- [ ] In freq dialog, type a real frequency and confirm → speaks "no radio, can't tune" (or equivalent apply-time error).
- [ ] No radio + ShowMemory hotkey → memory dialog opens. No "no radio" announcement.
- [ ] No radio + Ctrl+B (ChangeBand) → still announces no-radio with action label (item b verifies this; item a only verifies the *opt-out* doesn't accidentally fire here).

### Item (b) — Action-aware announcement
- [ ] No radio + Ctrl+B (ChangeBand) at Verbose verbosity → speaks "Unable to change band, JJ Flexible Home no radio connected."
- [ ] No radio + Ctrl+B at Terse → speaks "change band, no radio." (or specced terse form).
- [ ] No radio + Ctrl+B at Critical → speaks the Critical form.
- [ ] No radio + a command with no `ShortActionLabel` populated → falls back to plain-form output (today's behavior).
- [ ] Screen-reader pass with NVDA: announcements are not duplicated, not truncated, not interrupted.

### Regression check
- [ ] With radio connected: all band keys, tune, RIT/XIT, etc. work normally. The flag-check shouldn't add a perceptible latency or alter the success path.

## Commit style

Per CLAUDE.md sprint-N-track-X commit convention, plus the sub-items:

- `Sprint 28 design followup: add RunsWithoutRadio flag to KeyTableEntry`
- `Sprint 28 design followup: SetFreq opts in to RunsWithoutRadio`
- `Sprint 28 design followup: ShowMemory opts in to RunsWithoutRadio`
- `Sprint 28 design followup: action-aware SpeakNoRadioConnected with ShortActionLabel`
- `Sprint 28 design followup: populate ShortActionLabel for no-radio test commands`

Squash into a single merge commit at orchestrator merge time.

## What gets deleted on merge

After merging into `sprint28/home-key-qsk`:

- The `JJFlex-TODO.md` entries that flagged these two design items as DEFERRED (lines 287-365 region — verify line numbers at merge time, the file is fluid).
- This `TRACK-INSTRUCTIONS.md` copy in the worktree (the planning-doc copy in `docs/planning/active/` can stay until sprint28 is closed and archived).

## Cross-references

- `memory/project_short_action_labels_vocabulary.md` — Sprint 29 vocabulary work this track depends on.
- `memory/project_no_silent_keystrokes_rule.md` — the architectural rule the no-radio guard implements.
- `memory/project_independent_merge_events.md` — why this track branches from `sprint28/home-key-qsk`, not main.
- `docs/planning/active/sprint28-bug-bundle-triage.md` — the working dashboard that surfaced these design items.
- `docs/planning/vision/JJFlex-TODO.md` lines 287-365 — original bug entries with full root-cause context.
