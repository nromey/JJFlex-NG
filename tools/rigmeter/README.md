# rigmeter

A curiosity-driven CLI for source statistics across the JJ Flexible Radio Access codebase. Counts lines, words, files, and (in v1.2) pure code vs comments vs blanks across every project; reports git activity over time windows; diffs releases and other repos; computes growth deltas across spans (code base, doc base, comments, separately); persists JSON snapshots to NAS for a durable time series; surfaces author attribution, sprint stats, technical-debt markers, and dead-code candidates; and provides playful comparisons (braille volumes, Moby Dicks, King James Bibles).

The name is a ham-pun: rig meter = code meter.

## Quick start

You need Python 3.7 or later. No `pip install` is required — the tool is single-file and uses only the Python standard library.

From the repo root:

```
python tools/rigmeter/rigmeter.py all
```

That prints the full state of the repo right now: authored vs vendor rollups, per-project breakdown, per-category totals with per-language breakdown inside `code`, code-structure (pure code vs comments vs blanks via tokei), grand totals, fun comparisons, file-accounting summary, docs-to-code ratio, and (when ≥5 historical snapshots exist) a sparkline trend.

Run with no arguments to print the full help. Run `--interactive` for a numbered menu wizard.

## What v1.2 changed

v1.2 layers three new dimensions on top of the v1.1 line counter:

- **Pure code vs comments vs blanks** via [tokei](https://github.com/XAMPPRocky/tokei). Auto-fetched on first run (one-time ~5 MB download to `%LOCALAPPDATA%\rigmeter\tools\`); falls back to lines-only when `--no-fetch` is set or download fails.
- **Author attribution** via `git blame`. Run `rigmeter authors` for a per-project + repo-wide breakdown.
- **Multiple new subcommands** — `authors`, `sprint <N>`, `debt`, `forgotten` — for slicing the codebase by author, sprint, technical-debt markers, or last-touch staleness.

Other v1.2 additions:

- `--verbose` / `-v` on every subcommand for stdlib-`logging` debug output.
- `--format text|json|markdown|csv` on `all` (and structurally similar commands). Default `text` stays the v1.1.1 screen-reader bullet output; `markdown` and `csv` use real tables; `json` emits the underlying data structure.
- `--interactive` launches an opt-in numbered menu wizard. No-args invocation prints help (script-safe).
- ASCII sparkline trend in `all` once ≥5 NAS snapshots exist, paired with a numeric bullet list for screen-reader users.
- Docs-to-code ratio surfaced in `all` and as a delta in `growth`.
- Pure-code growth %, comment growth %, and comment-density shift in `growth` output (when both snapshots have tokei data).
- Schema bumped to **v2**: `pure_code`, `comments`, `blanks` fields appear inside each `CategoryCounts` dict (and inside its `by_language` map) when tokei data was layered on. v1 snapshots still load — missing fields default to 0 — so the time series stays continuous.

## What v1.1 changed (recap)

v1.1 separated **authored** work (Noel and Jim) from **vendor** work (FlexLib_API, P-Opus-master, PortAudioSharp-src-0.19.3). Headline number means "what we wrote." See git history for the full v1.1 commit if you need the older shape.

## Subcommands

### Snapshot of the repo right now

- `all` — full repository statistics. Authored vs vendor headlines, per-project breakdown, per-category totals, per-language breakdown within code, code-structure (tokei), grand totals, fun comparisons, file accounting summary, docs-to-code ratio, sparkline trend (when ≥5 snapshots). Add `--explain` to see which files were included/excluded for each category. Honors `--format` and `--no-fetch`.
- `fun` — just the fun-comparisons overlay, computed from the authored-only totals. (Skips tokei since fun stats don't use it.)

### Git activity over time windows

Each of these reports commits, unique files touched, insertions, deletions, net line change, files in diff, and authors over the corresponding window:

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

Output highlights **code-base growth %**, **doc-base growth %**, **pure-code growth %** and **comment growth %** (when both snapshots have tokei data) separately as headline lines. For spans of 1+ days, also shows per-day normalized rate. Vendor growth shows separately when non-zero. Hot files in span (top 10 by churn) appended at the end. Docs-to-code ratio shift reported as well.

### Cross-repo comparison

- `compare <path>` — side-by-side authored totals against another local repo (e.g. Jim's JJ Radio).

### JSON snapshot persistence

- `snapshot` — write a structured JSON snapshot to NAS at `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json`. Falls back to `%LOCALAPPDATA%\rigmeter\snapshots\` if NAS is unreachable. Wired into the end-of-day seal workflow (CLAUDE.md step 4a).
- `snapshot --sync` — copy local-only snapshots up to NAS. Run after a NAS-down window to reconcile.
- `backfill --refs <pattern>` — walk matching git refs and write a snapshot JSON for each. Default pattern `v*` (all release tags).

### Installer-size trend

- `installers` — version-by-version installer size trend, parsed from `\\nas.macaw-jazz.ts.net\jjflex\historical\<version>\installers\Setup JJFlex_*.exe`. Builds-bloat watchdog.

### v1.2 new subcommands

- `authors` — author attribution via `git blame`. Reports per-author line counts repo-wide and per project (authored code only). Aliases collapse multiple emails for the same human; extend via `RIGMETER_AUTHOR_ALIAS` env var.
- `sprint <N>` — auto-detects `sprint<N>/track-*` branches; reports aggregate growth from merge-base to newest tip plus per-track activity. Falls back to commit-message scanning when branches are gone. Points at the per-sprint test matrix when one exists.
- `debt` — counts TODO/FIXME/HACK/XXX markers in authored code/docs/text_data. Reports per-marker and per-project counts, plus example lines. `--examples N` controls how many examples are shown per marker (default 5).
- `forgotten --days N` — lists tracked files with no commit touch in N+ days, sorted oldest-first. Dead-code candidates. `--exclude prefix` filters paths (e.g. `--exclude FlexLib_API/`).

## Common flags

These work on every subcommand:

- `--verbose` / `-v` — debug logging to stderr (subcommand decisions, tokei invocations, snapshot writes).
- `--no-fetch` — skip auto-fetch of tokei. When tokei is missing and `--no-fetch` is set, code-vs-comment splits are omitted.
- `--format text|json|markdown|csv` — output format. Default `text` is the screen-reader-friendly bullet output. `markdown` and `csv` use real tables (opt-in). `json` emits the underlying data structure.
- `--explain` — file accounting (which files were included/excluded and why). Available on `all`, `growth`, `release`, `compare`.

## Interactive mode

Run `python tools/rigmeter/rigmeter.py --interactive` for a numbered menu wizard. Pick any subcommand by number; menu prompts for any arguments inline; output identical to direct CLI use. Loop continues until you pick `0`, type `exit`, or press Ctrl+C.

The menu is stdlib-only (no third-party dependencies) and works in a screen-reader-compatible terminal. A future v1.3 may add a richer TUI; the current menu wizard covers the accessibility-first case without adding dependencies.

## Categorisation

Files are bucketed into four categories by extension:

- **code** — `.vb`, `.cs`, `.xaml`, `.py`
- **text_data** — `.txt`, `.json`, `.xml`, `.yml`, `.yaml`, `.resx`, `.config`, `.settings`
- **docs** — `.md`, `.htm`, `.html`, `.rst`
- **build** — `.bat`, `.ps1`, `.sln`, `.vbproj`, `.csproj`, `.pyproj`, `.nsi`, `.hhc`, `.hhp`, `.props`, `.targets`, `.manifest`, `.editorconfig`, `.gitattributes`, `.gitignore`

Files with extensions in the `BINARY_EXTENSIONS` deny-list are excluded with reason `"binary"`. Files in vendor directories (`FlexLib_API/`, `P-Opus-master/`, `PortAudioSharp-src-0.19.3/`) are tagged `is_vendor=True` and report in the vendor rollup. Files matching `DERIVED_PATH_PREFIXES` (e.g. `docs/help/pages/`, `docs/help/md/whats-new.md`) are excluded with reason `"derived"`. Any file may opt out of stats by including the literal string `rigmeter:generated` in its first ~500 bytes.

Use `--explain` on `all` to see exactly which files landed in which bucket and why.

## Per-project breakdown

Project buckets are auto-discovered from top-level directories that contain at least one git-tracked file. Files at the repo root land in `main_app`. No hand-curated list to maintain.

## Tokei integration (v1.2)

When tokei is available, code-category records get `pure_code`, `comments`, `blanks` dimensions on top of the existing line/word/char counts. Detection precedence:

1. `RIGMETER_TOKEI_PATH` environment variable
2. `%LOCALAPPDATA%\rigmeter\tools\tokei.exe` (cache from prior auto-fetch)
3. `tokei` on `PATH`

If none match and `--no-fetch` is not set, rigmeter prompts to download tokei from `github.com/XAMPPRocky/tokei/releases` on first run (in non-TTY contexts the prompt is bypassed since invoking rigmeter is itself the user-consent signal).

Tokei numbers are reported observation-first ("17% of your code is comments"), never as a quality stick. Noel likes commenting; the data is for curiosity, not prescriptions.

## JSON snapshot schema

Snapshots are pretty-printed with sorted keys for diff-friendly output. **Schema v2** (v1.2) extends each `CategoryCounts` dict with optional `pure_code`, `comments`, `blanks` fields:

```json
{
  "schema_version": 2,
  "rigmeter_version": "1.2.0",
  "head_sha": "...",
  "head_ref": "HEAD",
  "totals": {
    "authored": {
      "by_category": {
        "code": {
          "files": 430, "lines": 101561, "words": 337307, "chars": ...,
          "pure_code": 78940, "comments": 14820, "blanks": 7801,
          "by_language": {
            "vb": {"files": ..., "pure_code": ..., "comments": ..., "blanks": ...},
            "cs": {...}, "xaml": {...}, "py": {...}
          }
        },
        "text_data": {...}, "docs": {...}, "build": {...}
      }
    },
    "vendor":   {...},
    "combined": {...}
  },
  "by_project": {
    "Radios":      {"is_vendor": false, ...},
    "FlexLib_API": {"is_vendor": true,  ...}
  },
  "file_accounting": {...},
  "fun_stats": {...}
}
```

Schema v1 snapshots still load — missing tokei fields default to 0 — so the time series stays continuous. `combined` is intentionally redundant with `authored + vendor` so consumers can skip the addition.

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

- **`.dat` is treated as binary.** Country-files data (cty.dat, cty_wt.dat) are functionally opaque to authorship; including them would inflate the headline.
- **Python lines bounce around as you edit rigmeter itself.** Live-tree mode reads from your working tree, so uncommitted rigmeter.py edits show up immediately. Reproducible numbers come from `growth <tag>` queries that walk a tree at a specific ref.
- **Tokei runs against the live working tree only.** Ref-mode snapshots (built via `git cat-file`) don't carry tokei dimensions, because tokei needs a real on-disk tree. Growth queries that use a ref-mode `before` snapshot will show line-deltas only; pure-code/comment deltas appear when both sides have tokei data.
- **`start` may show 0 deletions.** `git diff first_commit..HEAD` reports the *net* state difference between the two refs.
- **Windows console encoding.** The script forces stdout AND stderr to UTF-8 with `errors='replace'` at startup so the em-dashes, sparkline bars, and `≈` glyphs render on cp1252-default Windows terminals.
- **Snapshots live OUTSIDE the source tree.** NAS primary, `%LOCALAPPDATA%\rigmeter\snapshots\` fallback. Tools (tokei) cache in `%LOCALAPPDATA%\rigmeter\tools\`. Repo carries the *code* of rigmeter; NAS / local cache carries the *data*.

## What's planned but not in v1.2

Per the design memory `project_rigmeter_stats_tool.md`, future versions add:

- **Tokei against arbitrary refs** — would require checking out each ref into a temp directory; deferred until needed.
- **TUI stats explorer** (`--interactive=tui`) using `rich`/`textual`. Deferred to v1.3 because it would introduce the first third-party dependency. The v1.2 stdlib menu wizard already covers the accessibility-first use case.
- **`serve` subcommand** — render stats to HTML for `jjflexible.radio/stats` once the JJ Flexible Data Provider VPS is up. Effort isn't the blocker; infrastructure is.

## Naming note

This tool is named `rigmeter` per the design memory's ham-pun rationale (rig meter = code meter, mirroring the project's other `JJ`-prefixed tool names like `JJTrace` and `JJLogLib`).
