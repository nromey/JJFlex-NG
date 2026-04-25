# rigmeter

A curiosity-driven CLI for source statistics across the JJ Flexible Radio Access codebase. Counts lines, words, and files by project and category; reports git activity over time windows; diffs releases and other repos; provides playful comparisons (braille volumes, Moby Dicks, King James Bibles).

The name is a ham-pun: rig meter = code meter.

## Quick start

You need Python 3.7 or later. No `pip install` is required — the tool is single-file and uses only the Python standard library.

From the repo root:

```
python tools/rigmeter/rigmeter.py all
```

That prints the full state of the repo right now: per-project breakdown, per-category totals, grand totals, and the fun-comparisons overlay.

## Subcommands

### Snapshot of the repo right now

- `all` — full repository statistics. Per-project breakdown across the ten known top-level directories, per-category totals (code / text-data / docs / build), grand totals, and the fun-comparisons overlay.
- `fun` — just the fun-comparisons overlay, with a single one-line summary of the totals it is computed from. Use this when you want the playful one-liner without the full breakdown.

### Git activity over time windows

Each of these reports commits, unique files touched, insertions, deletions, net line change, files in diff, and authors over the corresponding window:

- `today` — since midnight local time. (Uses git's local-time interpretation of `midnight`, so this works correctly across timezones.)
- `week` — last 7 days.
- `month` — last 30 days.
- `year` — last 365 days.
- `start` — cumulative since the very first commit reachable from HEAD. Useful for seeing the project's total growth since inception.

### Release diffs

- `releases` — list every release tag (anything matching `v*`) that exists on the repo, sorted by version.
- `release <tag>` — stats for one release tag versus the previous tagged release. For example: `python tools/rigmeter/rigmeter.py release v4.1.16` shows what changed between `v4.1.15.1` and `v4.1.16`. Files changed, insertions, deletions, net line change.

### Cross-repo comparison

- `compare <path>` — side-by-side totals against another local repo. Use this to compare JJ Flexible Radio Access against, say, Jim Shaffer's original JJ Radio codebase (when you have the path to it).

## Categorisation

Files are bucketed into four categories by extension:

- **code** — `.vb`, `.cs`, `.xaml`, `.py`
- **text_data** — `.txt`, `.json`, `.xml`, `.yml`, `.yaml`
- **docs** — `.md`, `.htm`, `.html`, `.rst`
- **build** — `.bat`, `.ps1`, `.sln`, `.vbproj`, `.csproj`, `.pyproj`, `.nsi`, `.hhc`, `.hhp`

Files with extensions outside these buckets are skipped. Build artefact directories (`bin`, `obj`, `Debug`, `Release`, `node_modules`, `packages`, `__pycache__`, `.git`, `.vs`, etc.) are skipped at any depth.

## Per-project breakdown

The script knows about the following top-level directories and reports them as separate projects:

- `Radios` — radio abstraction layer
- `FlexLib_API` — vendor FlexLib + wrappers
- `JJPortaudio` — PortAudio wrapper
- `P-Opus-master` — Opus codec wrapper
- `JJLogLib` — logging library
- `JJTrace` — trace / debug utilities
- `JJFlexWpf` — WPF main UI
- `UiWpfFramework` — WPF MVVM helpers
- `docs` — documentation
- `tools` — developer tools (including this one)

Anything outside these ten directories is grouped under `main_app` (the root VB.NET application).

## Fun-stat formulas

These are the constants the playful comparisons use:

- **Braille volume** ≈ 100,000 cells (one cell = one character in our crude approximation).
- **Moby Dick** ≈ 210,000 words.
- **King James Bible** ≈ 783,000 words.
- **Speaking rate** = 150 words per minute.
- **Printed page** = 50 lines.
- **Stack height** = 250 pages per inch.

If anything looks off, those constants are at the top of `rigmeter.py` and can be tweaked.

## Notable quirks

- **Year-window deletions can look enormous.** The repo has had bulk vendor-tree removals over the year, so `year` may report tens of thousands of deletions across thousands of files. That is real history, not a bug. The `Files in diff` number includes every file that was ever touched in the window, not just files that exist now.
- **`start` may show 0 deletions.** `git diff first_commit..HEAD` reports the *net* state difference between the two refs, so if a file's first-commit content is entirely subsumed by HEAD content, the diff is all insertions. For repos that started near-empty (which JJ Flexible Radio Access did) this is normal.
- **Windows console encoding.** The script forces stdout to UTF-8 with `errors='replace'` at startup so the em-dashes and `≈` glyphs in headers render on cp1252-default Windows terminals. If you still see boxes for those glyphs, your terminal font is the limiting factor.

## What's planned but not in v1

Per the design memory `project_rigmeter_stats_tool.md`, future versions add:

- **`snapshot` subcommand** — daily JSON write to NAS so a time-series accumulates over days/weeks/months.
- **`serve` subcommand** — render stats to HTML for `jjflexible.radio/stats` once the VPS is up.
- **`between <tagA> <tagB>`** — diff any two arbitrary tags (today's `release` only does single-release-vs-prior).
- **`sprint <N>`** — stats inferred from `sprint<N>/...` branch name patterns.
- **External tool integration** — optionally shell out to `cloc` or `tokei` if installed, for sharper language-detection edge-case handling.

## Naming note

This tool is named `rigmeter` per the design memory's ham-pun rationale (rig meter = code meter, mirroring the project's other `JJ`-prefixed tool names like `JJTrace` and `JJLogLib`). Noel has also referred to it colloquially as `jj-codestat` — if a rename is preferred at any point, it is a single `git mv` of this directory.
