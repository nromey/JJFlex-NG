# rigmeter

A curiosity-driven CLI for source statistics across the JJ Flexible Radio Access codebase. Counts lines, words, and files by project and category; reports git activity over time windows; diffs releases and other repos; computes growth deltas across spans (code base AND doc base, separately); persists JSON snapshots to NAS for a durable time series; provides playful comparisons (braille volumes, Moby Dicks, King James Bibles).

The name is a ham-pun: rig meter = code meter.

## Quick start

You need Python 3.7 or later. No `pip install` is required — the tool is single-file and uses only the Python standard library.

From the repo root:

```
python tools/rigmeter/rigmeter.py all
```

That prints the full state of the repo right now: authored vs vendor rollups, per-project breakdown, per-category totals with per-language breakdown inside `code`, grand totals (authored only as the headline), fun comparisons, and a file-accounting summary.

## What v1.1 changed

v1.1 separates **authored** work (what Noel and Jim wrote) from **vendor** work (FlexLib_API, P-Opus-master, PortAudioSharp-src-0.19.3) and reports them as distinct rollups. The headline number now means "what we wrote." v1's headline was inflated by vendor code, derived/regenerated artefacts, untracked build outputs, and binary files that were silently skipped without being audited.

Other v1.1 changes:

- Live-tree mode walks `git ls-files` instead of `os.walk`. Untracked files (build outputs, scratch files) are no longer counted, and live snapshots match ref-mode snapshots for the same SHA.
- Explicit `BINARY_EXTENSIONS` deny-list catches `.dll`, `.exe`, `.pdb`, `.chm`, `.png`, `.ico`, `.zip`, `.pdf`, `.wav`, `.snk`, `.dat`, etc. — checked before category lookup, so a binary file accidentally added to a category's extension set still gets excluded.
- Per-language breakdown within `code` (% VB, % C#, % XAML, % Python), reported both as authored-only and across all files.
- Project buckets are auto-discovered from `git ls-files` output rather than maintained as a hand-curated list (which had drifted in v1).
- New `growth`, `snapshot`, `backfill`, and `installers` subcommands. `--explain` flag on `all`/`growth`/`release` shows file accounting.

## Subcommands

### Snapshot of the repo right now

- `all` — full repository statistics. Authored vs vendor headlines, per-project breakdown (vendor projects below a separator), per-category totals (authored), per-language breakdown within code, grand totals, fun comparisons, file accounting summary. Add `--explain` to see which files were included/excluded for each category.
- `fun` — just the fun-comparisons overlay, computed from the authored-only totals.

### Git activity over time windows

Each of these reports commits, unique files touched, insertions, deletions, net line change, files in diff, and authors over the corresponding window. Each output also includes a one-line cross-reference to `growth` for size-delta queries (since these commands report **activity**, not **size growth** — different mental model).

- `today` — since midnight local time
- `week` — last 7 days
- `month` — last 30 days
- `year` — last 365 days
- `start` — cumulative since the very first commit reachable from HEAD

### Release info

- `releases` — list every release tag (anything matching `v*`) with the days-since-prior-release column.
- `release <tag>` — stats for one release vs prior tagged release. Add `--growth` to also build full snapshots and report per-category growth %s.

### Growth deltas across spans

- `growth <ref-a> <ref-b>` — explicit pair (e.g. `growth v4.1.12 v4.1.16`)
- `growth <ref-a>` — shorthand for `<ref-a>..HEAD` (the 80% case)
- `growth --since <date>` — date sugar (e.g. `growth --since 2026-01-01`)
- `growth --use-snapshots <date-a> <date-b>` — fast path that reads JSON snapshots from NAS/local instead of walking git. `<date-a>` and `<date-b>` become date prefixes (`2026-03-08`).

Output highlights **code base growth %** and **doc base growth %** separately as headline lines (the two numbers Noel asked for). For spans of 1+ days, also shows per-day normalized rate so spans of different lengths are comparable. Vendor growth shows separately when non-zero. Hot files in span (top 10 by churn) appended at the end.

### Cross-repo comparison

- `compare <path>` — side-by-side authored totals against another local repo (e.g. Jim's JJ Radio). Vendor row hidden when both sides have zero vendor.

### JSON snapshot persistence

- `snapshot` — write a structured JSON snapshot to NAS at `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json`. Falls back to `%LOCALAPPDATA%\rigmeter\snapshots\` if NAS is unreachable. Wired into the end-of-day seal workflow (CLAUDE.md step 4a).
- `snapshot --sync` — copy local-only snapshots up to NAS. Run after a NAS-down window to reconcile.
- `backfill --refs <pattern>` — walk matching git refs and write a snapshot JSON for each. Default pattern `v*` (all release tags). Long-running but cheap (~0.7s per ref); use to seed the historical time series.

### Installer-size trend (bonus)

- `installers` — version-by-version installer size trend, parsed from `\\nas.macaw-jazz.ts.net\jjflex\historical\<version>\installers\Setup JJFlex_*.exe`. Only release-built versions appear (debug-only versions don't have installers). Builds-bloat watchdog.

## Categorisation (v1.1)

Files are bucketed into four categories by extension:

- **code** — `.vb`, `.cs`, `.xaml`, `.py`
- **text_data** — `.txt`, `.json`, `.xml`, `.yml`, `.yaml`, `.resx`, `.config`, `.settings`
- **docs** — `.md`, `.htm`, `.html`, `.rst`
- **build** — `.bat`, `.ps1`, `.sln`, `.vbproj`, `.csproj`, `.pyproj`, `.nsi`, `.hhc`, `.hhp`, `.props`, `.targets`, `.manifest`, `.editorconfig`, `.gitattributes`, `.gitignore`

Files with extensions in the `BINARY_EXTENSIONS` deny-list are excluded with reason `"binary"`. Files in vendor directories (`FlexLib_API/`, `P-Opus-master/`, `PortAudioSharp-src-0.19.3/`) are tagged `is_vendor=True` and report in the vendor rollup. Files matching `DERIVED_PATH_PREFIXES` (e.g. `docs/help/pages/`, `docs/help/md/whats-new.md`) are excluded with reason `"derived"`. Any file may opt out of stats by including the literal string `rigmeter:generated` in its first ~500 bytes (comment-syntax-agnostic — works for HTML/XML, Python, VB, etc.).

Use `--explain` on `all` to see exactly which files landed in which bucket and why.

## Per-project breakdown

Project buckets are auto-discovered from top-level directories that contain at least one git-tracked file. Files at the repo root land in `main_app`. No hand-curated list to maintain.

## JSON snapshot schema

Snapshots are pretty-printed with sorted keys for diff-friendly output:

```
{
  "schema_version": 1,
  "generated_at": "2026-04-26T00:01:23-05:00",
  "rigmeter_version": "1.1.0",
  "head_sha": "<full sha>",
  "head_ref": "HEAD" | "<tag>",
  "head_describe": "<git describe output>",
  "repo_root_name": "JJFlex-NG",
  "totals": {
    "authored": {
      "files": N, "lines": N, "words": N, "chars": N,
      "by_category": {
        "code":      {"files": N, ..., "by_language": {"vb": {...}, "cs": {...}, "xaml": {...}, "py": {...}}},
        "text_data": {...}, "docs": {...}, "build": {...}
      }
    },
    "vendor":   { ... same shape ... },
    "combined": { ... same shape, convenience ... }
  },
  "by_project": {
    "Radios":      {"is_vendor": false, "files": N, ..., "by_category": {...}},
    "FlexLib_API": {"is_vendor": true,  ...}
  },
  "file_accounting": {
    "included": N,
    "excluded": {
      "binary":  {"count": N, "examples": ["...", "..."]},
      "derived": {"count": N, "examples": [...]},
      "unknown_extension": {"count": N, "examples": [...]},
      "excluded_dir":      {"count": N, "examples": [...]},
      "unreadable":        {"count": N, "examples": [...]}
    }
  },
  "fun_stats": {
    "braille_volumes": F, "moby_dicks": F, "kjv_bibles": F,
    "read_aloud_hours": F, "printed_pages": N, "stack_inches": F
  }
}
```

`combined` is intentionally redundant with `authored + vendor` so consumers can skip the addition. `examples` (first 5 paths per skip reason) makes snapshots debuggable without becoming a full file manifest. `head_describe` lets a human eyeball staleness without resolving the SHA.

## Fun-stat formulas

These are the constants the playful comparisons use:

- **Braille volume** ≈ 100,000 cells (one cell = one character, crude approximation)
- **Moby Dick** ≈ 210,000 words
- **King James Bible** ≈ 783,000 words
- **Speaking rate** = 150 words per minute
- **Printed page** = 50 lines
- **Stack height** = 250 pages per inch

If anything looks off, those constants are at the top of `rigmeter.py` and can be tweaked.

## Notable quirks

- **`.dat` is treated as binary.** `cty.dat`, `cty_wt.dat` are upstream country-files data (downloaded periodically from cty.com), functionally opaque to authorship — including them in the line count would inflate the headline with non-authored data. If you specifically want them counted, add a `data_blob` category in v1.2.
- **Python lines bounce around as you edit rigmeter itself.** Live-tree mode reads from your working tree, so uncommitted rigmeter.py edits show up in `code/py` totals immediately. Reproducible numbers come from `growth <tag>` queries that walk a tree at a specific ref.
- **Year-window deletions can look enormous.** The repo has had bulk vendor-tree removals over the year; `year` may report tens of thousands of deletions across thousands of files. That is real history, not a bug.
- **`start` may show 0 deletions.** `git diff first_commit..HEAD` reports the *net* state difference between the two refs; if a file's first-commit content is entirely subsumed by HEAD content, the diff is all insertions.
- **Windows console encoding.** The script forces stdout AND stderr to UTF-8 with `errors='replace'` at startup so the em-dashes and `≈` glyphs render on cp1252-default Windows terminals.
- **`git cat-file --batch` protocol care.** Reading content from a git ref uses the binary batch protocol — the wrapper class reads exactly N bytes per response (NOT line-based, which would desync on files without trailing newlines). Self-tested on construction against a known small blob.
- **Snapshots live OUTSIDE the source tree.** NAS primary, `%LOCALAPPDATA%\rigmeter\snapshots\` fallback. Repo carries the *code* of rigmeter; NAS carries the *data*. The repo never accumulates JSON history.

## What's planned but not in v1.1

Per the design memory `project_rigmeter_stats_tool.md`, future versions add:

- **Author attribution** via `git blame` — % of current code authored by Noel vs Jim vs others. Per-project, per-file. (Jim never saw what Noel built; this number means something.)
- **Sprint auto-detection** — `sprint <N>` subcommand scanning for `sprintN/track-*` branch patterns and reporting per-sprint deltas.
- **TODO/FIXME/HACK counter** — accountability metric, with trend over snapshots.
- **Docs-to-code ratio over snapshots** — turns "are we maintaining doc parity?" from a vibe into a number.
- **ASCII sparkline trend** — once 5+ snapshots exist, render a tiny bar-graph of code-base growth in `all` output.
- **`forgotten` subcommand** — files not touched in N days; dead-code detector.
- **`serve` subcommand** — render stats to HTML for `jjflexible.radio/stats` once the VPS is up.
- **External tool integration** — optionally shell out to `cloc` or `tokei` if installed, for sharper language detection.

## Naming note

This tool is named `rigmeter` per the design memory's ham-pun rationale (rig meter = code meter, mirroring the project's other `JJ`-prefixed tool names like `JJTrace` and `JJLogLib`).
