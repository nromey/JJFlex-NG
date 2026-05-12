---
date: 2026-05-11
type: audit / punch-list
audit-scope: docs/help/md/keyboard-reference.md vs JJFlexWpf/KeyCommands.cs
status: findings captured, fixes deferred
triggered-by: Shift+M (Mute All Slices) found bound in code but missing from doc during foundation testing on 2026-05-11
---

# Keyboard reference audit — 2026-05-11

## What triggered this audit

During foundation regression testing on 2026-05-11, Noel reached for the
"mute all slices" hotkey and couldn't find it in `keyboard-reference.md`.
Confirmed in `KeyCommands.cs:1099-1102` that `Shift+M` is bound to
`MuteAllSlices` (Radio scope) but the doc only documents plain `M` in the
Home Slice Operations section. That drift triggered a full audit of the
keyboard reference against the code's `KeyTableEntry` registrations.

The CLAUDE.md "Keyboard Audit — Definition of Done for key-map changes"
section is the procedural mechanism this drift was supposed to be caught
by; the audit landed sometime after a binding was added without the
corresponding doc-edit step being run.

## Findings

### Category 1: Bound in code, missing from doc (8 entries)

| Binding | Action | Scope | Where it should appear in doc |
|---|---|---|---|

Actually deliberately not using a table here — Noel's "no tables in
screen-reader artifacts" rule (`feedback_no_tables_in_screen_reader_artifacts`).
List form below.

- **`Ctrl+Alt+M`** — Toggle meter sonification tones. Radio scope. Should appear in Audio Controls or Global Hotkeys. Today only the leader-key form `Ctrl+J, M` is documented.
- **`Ctrl+Alt+P`** — Cycle meter tone preset (RX / TX / Full Monitor). Radio scope. Missing entirely; only `Ctrl+J, P` is documented.
- **`Ctrl+Alt+V`** — Speak current meter values. Radio scope. Missing.
- **`Ctrl+Shift+F`** — Speak current frequency and mode. Radio scope. The doc shows `Ctrl+Shift+F` only as the Logging-scope `SearchLog` binding; the Radio-scope version is invisible.
- **`Ctrl+F4`** — Repeat the last spoken message. Global scope. Missing.
- **`Ctrl+Shift+B`** — Toggle braille status line. Global scope. Diagnostic-only; acceptable omission from user-facing docs.
- **`Shift+M`** — Mute or unmute every slice at once. Radio scope. The doc shows plain `M` in the Home Slice Operations section but not the all-slices variant. This is the canary that triggered the audit.
- **`Shift+,`** (Shift+Comma) — Release every slice except the first, back to one slice. Radio scope. Not documented anywhere.

### Category 2: In doc but not bound in code

None found. Every binding the doc lists is actually registered in code, so
this is one-sided drift: the doc lags the code, never leads it.

### Category 3: Mismatched description / scope collision

- **`Ctrl+Shift+N` is dual-bound but doc only shows the Logging case.** In Radio scope it maps to `ToggleDspExpander` (line 1079 of `KeyCommands.cs`); in Logging scope it maps to `LogCharacteristicsDialog` (line 1052). The dual binding works correctly per the scope-aware validator, but a user reading the doc only sees the Logging behavior and would be surprised when the same key opens the DSP expander while a radio is connected.
- **`Ctrl+Shift+U`** is documented (line 201 of the doc) as a within-Audio-Expander navigation behavior but the binding itself (the key that opens the Audio Expander) isn't called out as a separate row in the Global / Radio table. Minor — Noel can probably leave this alone — but flagging for completeness.

## Underlying pattern

Most of the missing bindings (`Ctrl+Alt+M/P/V`, `Ctrl+Shift+F`, `Ctrl+F4`,
`Shift+M`, `Shift+,`) share a shape: they're Radio-scope bindings that
*work anywhere a radio is connected* — not Home-only. The doc documents
several of these only under their Home sub-field section, implying they're
sub-field-local. That's the user-experience trap Noel fell into: read the
doc, see `M` documented under Slice Operations, assume `Shift+M` is also
sub-field-local, fail to discover that it works radio-wide.

The structural fix is to add scope labels to each doc entry (Global /
Radio / Logging / Home sub-field). That's also what the keyboard-manifest
automation referenced in CLAUDE.md would emit if it were built.

## Fix path

Three escalating options, not mutually exclusive:

1. **Doc-edit pass** (small, deferred). Add the 8 missing rows to
   `keyboard-reference.md` under their correct scope sections. Add a
   scope label to each entry. Add the `Ctrl+Shift+N` dual-binding note.
   Roughly 20-30 minutes of focused doc writing.
2. **Cross-link from Command Finder** (small, deferred). The Command
   Finder (`Ctrl+/`) is the live, always-correct source of truth for
   what's bound today. Doc should explicitly point at the Command
   Finder for "if you can't find a binding here, search there." Lowers
   the cost of future doc drift since users have a reliable escape
   hatch.
3. **Build-time keyboard-manifest automation** (Sprint 29+ candidate
   per CLAUDE.md). Introspect the `KeyCommands` registry at build
   time, emit a canonical manifest, fail the build if
   `keyboard-reference.md` is out of sync. Eliminates this entire
   class of drift permanently.

## Status

Findings captured; fixes deferred. Noel's call on when to pull #1 forward.
Most of the missing bindings have been bound for months (the audit didn't
find any from yesterday's work) — the drift is structural, not urgent.

## Companion audit

The JJ+H context-help audit (`2026-05-11-jj-h-context-help-audit.md`) ran
in parallel today and surfaces a complementary discoverability issue:
even when a binding *is* documented, users sometimes reach for the wrong
help mechanism. Reading both audits together is the better entry point
for whoever does the doc-edit pass.
