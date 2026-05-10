# Sprint 29 Track H — Trace Archive Browser UI

**Branch:** `sprint29/track-h-trace-browser`
**Worktree:** `C:\dev\jjflex-trace-browser`
**Spawned:** 2026-05-09 (evening session)
**Target ship:** 4.2.0
**Target merge:** main

## Scope summary

Build the user-facing trace archive browser surface in `TraceAdmin.vb`. **The full design lives in `docs/planning/active/trace-archive-navigation-design.md`** (committed today on main). Read that document first — it specifies use cases, information architecture, UI shape with ASCII layout, accessibility specs, implementation hints, and open questions.

This TRACK-INSTRUCTIONS.md is the launch wrapper; the design doc is the spec.

## Quick orientation

What's already built (you'll consume these):

- **`SessionArchive.ArchiveSession`** + **`SessionArchive.PruneOlderThan`** — write/prune (Track A Phase 1 + Phase 2)
- **`TraceManifest.Load`** — parsed manifest read
- **`PerformTraceArchivePrune`** in `globals.vb` — public Friend function for one-shot prune with screen-reader announcement (ships in `88d51aa2`)
- **LZMA compression via SharpCompress** (per `441608e6`)
- **Manifest schema** — see `memory/project_trace_persistence_design.md` for fields

What you're building:

- A new tab or section in `TraceAdmin.vb` labeled "Trace Archive Browser"
- Filter row (date range, outcome dropdown, search)
- ListView (sortable columns)
- Selection detail panel
- Action buttons (View, Copy Path, Export, Delete, Prune)
- Archive footer (total size, count, retention info)

See `docs/planning/active/trace-archive-navigation-design.md` for the ASCII layout and detailed component specs.

## Implementation order

1. **Tab vs. split decision** — design doc's open question 1. Recommend tabs (TabControl or equivalent) for cleaner screen-reader focus. Implement that.
2. **Manifest cache + filter pipeline** — load on tab activation, in-memory LINQ for filter changes, refresh on destructive actions.
3. **ListView with columns** — Date, Duration, Outcome, Connection Target, Size. Sortable. Default sort: date descending.
4. **Filter row** — date pickers, outcome dropdown, search textbox with debounce. Live region announces "N total, M shown" on changes.
5. **Selection detail panel** — outcome_reason, key_events list, full file path. Verbosity-aware auto-announce on selection change.
6. **Action buttons** — View Trace (extract LZMA + Process.Start), Copy Path (clipboard), Export Selected (multi-zip), Delete Selected (confirm + remove + manifest update).
7. **Footer + Prune now button** — calls `PerformTraceArchivePrune` with custom retention if user changes default 30-day value.
8. **Outcome localization dictionary** — static map from enum to display name (`as_retry_then_success` → "AS retry then success"). Per design doc open question 2, defer broader localization to Sprint 30+.
9. **Keyboard shortcuts** — Enter (View), Ctrl+C (Copy Path), Delete key (Delete with confirm), Ctrl+A (select all).
10. **CHANGELOG entry** — Foundation Phase. Warm tone, screen-reader-friendly framing ("Browse the trace archive from Settings → Tracing → Archive Browser").

## File touch list (estimated)

- `JJTrace/TraceAdmin.vb` (or `.Designer.vb`) — main implementation
- Possibly new files in `JJTrace/` if the browser warrants its own class (TraceBrowserPanel, TraceFilterModel, etc.)
- `docs/CHANGELOG.md` — Foundation Phase entry
- `docs/help/md/keyboard-reference.md` — TraceAdmin shortcuts (Tab in/out, View, Copy Path)

## What MUST NOT regress

1. **Existing TraceAdmin.vb start/stop tracing controls work unchanged.** Browser is additive.
2. **No silent keystrokes.** Every action announces at appropriate verbosity.
3. **Auto-prune (30 days) keeps running in the background** independent of the browser.
4. **The selection detail panel speaks on selection change** — accessibility-critical.

## Build + commit + DoD

Standard. See design doc cross-references and the Sprint 29 commit conventions (`Sprint 29 Track H:` prefix).

## Resume hint

> Resume Sprint 29 Track H from TRACK-INSTRUCTIONS.md
