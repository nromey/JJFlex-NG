# Trace Archive Navigation — Design

**Status:** DRAFT — for Track H executor to read before building
**Authored:** 2026-05-09 (orchestrator session)
**Spec drives:** Sprint 29 Track H (Trace browser UI in TraceAdmin.vb)
**Related:** `memory/project_trace_persistence_design.md`, `memory/project_user_initiated_feedback_session.md`

## What this is

The trace archive (LZMA-compressed manifest-indexed) accumulates every connection's trace from JJ Flexible. Today, Track A's Phase 2 functional layer exists — `PerformTraceArchivePrune` (manual prune helper, ships 88d51aa2), `SessionArchive.ArchiveSession` (writes), `TraceManifest.Load` (reads). What's missing: **the user-facing surface to actually browse, filter, and act on the archive.**

This design specifies that surface.

## Use cases (in priority order)

1. **Post-crash forensics.** A user has just experienced a crash; they (or Noel during triage) want to see what was happening across the last several sessions. Filter by date range; sort by date descending; view trace content.
2. **Bisect a recurring failure.** "I keep getting AS-retry-then-fail when connecting to Don's radio." Filter by outcome=`as_retry_failed`, optionally filter by connection target=`don's serial`. Find the most recent N occurrences; export selected as a sub-bundle for sharing.
3. **Periodic review.** "What failure modes are showing up this week?" Outcome distribution chart (success vs. killed vs. as_retry_failed vs. slice_unavailable etc.) over a date range.
4. **Disk space hygiene.** "Is my trace archive growing unboundedly?" Show total archive size, oldest entry, count by outcome. (Auto-prune at 30 days handles this in the background per Phase 1; the UI just makes it visible.)
5. **Manual one-off.** Copy a specific trace path to clipboard for ad-hoc attachment to an email or external bug report.

## Information architecture

Each archive entry has these fields in the manifest (per `project_trace_persistence_design.md`):

- **boot_time** (UTC) — when the JJF session started
- **duration_ms** — how long the session ran (synthetic for killed sessions; real for clean exits)
- **outcome** — enum: `success`, `killed`, `as_retry_then_success`, `as_retry_failed`, `slice_unavailable`, `network_failed`, `connection_dropped`, etc.
- **outcome_reason** — free-text detail
- **filename** — relative path of the archived `.lzma` file
- **size_bytes** — compressed size
- **connection_target** — radio serial + nickname if available
- **key_events** — array of significant events (`as_retry_attempt_2_remote`, `slice_in_use`, etc.)

The browser surfaces these as columns in a list view, with filterable subsets above.

## UI shape

Lives in **`TraceAdmin.vb`** as a new tab or section labeled "Trace Archive Browser." The existing `TraceAdmin.vb` already has the start/stop tracing controls; this is additive.

### Layout

```
┌─ Trace Archive Browser ──────────────────────────────────────────────┐
│                                                                      │
│ Filter:                                                              │
│   Date range:  [From: 2026-04-09]  [To: 2026-05-09]                  │
│   Outcome:     [Any ▼]                                               │
│   Search:      [_______________________]  (matches connection target│
│                                            and outcome_reason)       │
│                                                                      │
│ ┌─ Archive Entries (123 total, 47 shown) ─────────────────────────┐  │
│ │ Date           Duration  Outcome              Target    Size     │ │
│ │ 2026-05-09 14:23  3:42  success               Don 6300  12 KB   │ │
│ │ 2026-05-09 13:15  0:15  as_retry_then_success Don 6300  18 KB   │ │
│ │ 2026-05-09 11:02  1:23  killed                Don 6300  9 KB    │ │
│ │ ...                                                              │ │
│ └──────────────────────────────────────────────────────────────────┘ │
│                                                                      │
│ Selected: 2026-05-09 13:15 — as_retry_then_success — Don 6300        │
│   Reason: Remote retry attempt 2 succeeded                           │
│   Key events: as_retry_attempt_2_remote, as_retry_then_success_2_... │
│                                                                      │
│ [View Trace]  [Copy Path]  [Export Selected...]  [Delete Selected...]│
│                                                                      │
│ Archive total: 47 MB across 123 entries                              │
│ Auto-prune: entries older than 30 days are removed automatically    │
│ [Prune now...]                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

### Detailed component specs

- **Filter row** — three controls (date-from, date-to, outcome dropdown, search box). Filter applies live as user types/selects (with debounce on text). Status line shows "N total, M shown."
- **List view** — DataGrid or ListView with sortable columns. Columns: Date (UTC, ISO format), Duration (mm:ss), Outcome (with localized display name), Connection Target, Size. Date sort descending by default.
- **Selection detail panel** — shows full manifest entry for the currently selected row: outcome_reason, key_events list (each as a chip or comma-separated), full file path. Read-only.
- **Action buttons** (operate on selected row[s]):
  - **View Trace** — extract the LZMA archive to temp, open the resulting `.txt` in the user's default text viewer (Notepad or whatever's associated). Critical-level speech: "Trace opened in text viewer."
  - **Copy Path** — copies the full archive file path to clipboard. Useful for manual attachment in email/external bug reports. Speech: "Trace path copied to clipboard."
  - **Export Selected** — bundles selected trace(s) into a single zip with a naming pattern like `traces-export-YYYYMMDD-HHMMSS.zip`. User picks save location via SaveFileDialog. Speech: "N traces exported to [path]."
  - **Delete Selected** — confirms ("Delete N selected trace(s)?"), removes file and manifest entry. Speech: "N traces deleted."
- **Archive footer** — shows total size, total count, auto-prune retention. Includes a "Prune now..." button that calls `PerformTraceArchivePrune` with a configurable retention (defaults to 30 days; user can override for one-shot prunes).

## Accessibility specifications

- **List view must be screen-reader navigable** with arrow keys (DataGrid pattern). Each row is a single navigable unit; column headers announced once on Tab into the grid.
- **Live region** for filter changes — when user adjusts a filter, the status line ("N total, M shown") fires a polite announcement so the screen reader user knows the filter took effect without having to navigate elsewhere.
- **Selection detail panel** auto-announces when selection changes, with a chord summary: "as_retry_then_success on Don 6300, 1 minute 23 seconds, 2 key events." Verbosity-aware (Terse: outcome only; Normal: outcome + target + duration; Chatty: everything).
- **Keyboard shortcuts** (alongside the buttons):
  - **Enter** on selected row = View Trace
  - **Ctrl+C** with row selected = Copy Path
  - **Delete** key with row selected = Delete (with confirm)
  - **Ctrl+A** = select all
- **No silent keystrokes.** Every action announces outcome at Critical or Normal level depending on impact.

## Implementation hints for Track H executor

- **Trace contents are LZMA-compressed text via SharpCompress** (per `project_trace_persistence_design.md` and the `441608e6 switch trace archive to LZMA via SharpCompress` commit). For "View Trace," extract to a temp file, then `Process.Start` with no explicit verb (Windows uses default association).
- **Manifest reads should be cached.** The browser opens the manifest once, holds it in memory, and refreshes only on filter change or after destructive actions (delete, prune). Re-reading the manifest on every keystroke is wasteful.
- **Filtering should be in-memory LINQ** over the cached manifest entries, not re-reading the file. Today's manifest is small enough (hundreds of entries) that in-memory filtering is instant.
- **Existing `PerformTraceArchivePrune`** (in globals.vb, ships in 88d51aa2) is the right call for the "Prune now" button. It already announces via screen reader and accepts a custom retention count.
- **`SessionArchive.ManifestFileName`** and `TraceArchiveDir` are the canonical paths; reuse them rather than rebuilding the file path manually.
- **Don't break the existing TraceAdmin.vb start/stop controls** — the browser is additive. Existing tests for those controls must still pass.

## Open questions for Track H executor (or Noel)

1. **Tab vs. side-by-side?** The current TraceAdmin.vb is a Form with start/stop controls. Should the archive browser be a second tab (TabControl), or a panel below the existing controls (split layout)? Tabs are screen-reader-friendlier (clearer focus boundaries); split layout uses screen real estate better. Probably tabs.
2. **Outcome display name localization.** The outcome enum values (`as_retry_then_success`) are programmatic. The UI needs human-readable labels ("AS retry then success"). Should the labels live in a static dictionary (today) or in the centralized utterances file (Sprint 30+, per `project_localization_strings_file.md`)? Probably static dictionary for now; localize at the same time as the broader localization pass.
3. **Trace size for very large traces.** Some debug sessions produce traces in the megabytes when uncompressed. View Trace via Notepad will be slow for those. Consider: warn-on-large-extraction, or use a more capable viewer when available. Defer if it doesn't come up in testing.
4. **Multi-select.** The above design includes Ctrl+A and "Selected" plural in actions. Implementing multi-select properly in WinForms ListView is straightforward; the manifest entry detail panel needs to handle "(multiple selected)" gracefully.

## What this design does NOT include

- **Statistical / chart views** of outcome distribution over time. Use case 3 (periodic review) is mentioned but the design defers any chart UI to a follow-up. The list view + filter is enough for v1; charts are gravy.
- **Free-text search inside trace contents.** Searching across compressed-file contents is expensive. Defer until we have a tester saying "I need this." For v1, search is metadata-only (connection target, outcome reason).
- **NAS mirror.** `project_trace_persistence_design.md` mentions "local-first with optional NAS mirror." That's a separate config knob — Track H should NOT block on it. The browser shows local archive only.

## Cross-references

- `memory/project_trace_persistence_design.md` — Phase 1+2 design, manifest schema
- `memory/project_user_initiated_feedback_session.md` — feedback path that may share UX patterns
- `memory/project_no_silent_keystrokes_rule.md` — every action must announce
- `memory/project_dialog_escape_rule.md` — Escape closes / Cancel always works
- Track A commits: 69d80cfe, ff5992c8, 441608e6, ea11cb03, d6f219ab, 4f04a09b, 88d51aa2
