#!/usr/bin/env python3
"""rigmeter — JJ Flexible Radio Access source statistics tool.

A curiosity-driven CLI for counting lines, words, and files across the
JJFlex codebase, with per-project breakdown, time-window git activity,
release diffs, cross-repo comparison, and playful comparisons.

The name is a ham-pun: rig meter = code meter. v1 is single-file,
Python-stdlib only — no `pip install` required to run.

Usage:
    python tools/rigmeter/rigmeter.py all
    python tools/rigmeter/rigmeter.py today
    python tools/rigmeter/rigmeter.py week
    python tools/rigmeter/rigmeter.py month
    python tools/rigmeter/rigmeter.py year
    python tools/rigmeter/rigmeter.py start
    python tools/rigmeter/rigmeter.py release v4.1.16
    python tools/rigmeter/rigmeter.py releases
    python tools/rigmeter/rigmeter.py compare /path/to/other/repo
    python tools/rigmeter/rigmeter.py fun

See the project_rigmeter_stats_tool.md memory note for the full design.
"""

import argparse
import os
import re
import subprocess
import sys
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

# --- Configuration ---------------------------------------------------------

# Repo root is two levels up from this file: tools/rigmeter/rigmeter.py → repo root.
REPO_ROOT = Path(__file__).resolve().parent.parent.parent

# File extension → category. Anything not listed is skipped from counting.
CATEGORIES = {
    "code":      {".vb", ".cs", ".xaml", ".py"},
    "text_data": {".txt", ".json", ".xml", ".yml", ".yaml"},
    "docs":      {".md", ".htm", ".html", ".rst"},
    "build":     {".bat", ".ps1", ".sln", ".vbproj", ".csproj", ".pyproj", ".nsi", ".hhc", ".hhp"},
}

# Per-project root directories. Files outside these go into "main_app".
# Order matters only for display.
PROJECT_DIRS = [
    "Radios",
    "FlexLib_API",
    "JJPortaudio",
    "P-Opus-master",
    "JJLogLib",
    "JJTrace",
    "JJFlexWpf",
    "UiWpfFramework",
    "docs",
    "tools",
]

# Directory names skipped at any depth (build artefacts, vendored deps, VCS).
EXCLUDE_DIRS = {
    ".git", ".vs", ".vscode", ".idea",
    "bin", "obj", "Debug", "Release",
    "node_modules", "packages",
    "__pycache__", ".pytest_cache",
    "Old-Readme",
}

# --- Fun-stat constants ----------------------------------------------------

BRAILLE_VOLUME_CELLS = 100_000   # ≈ standard braille volume capacity
MOBY_DICK_WORDS      = 210_000   # Melville's Moby Dick word count
KJV_BIBLE_WORDS      = 783_000   # King James Bible word count
SPEAKING_WPM         = 150       # average read-aloud speaking rate
PRINTED_LINES_PER_PAGE = 50      # for stack-of-pages estimate
PAGES_PER_INCH       = 250       # ream height ≈ 2 inches per 500 pages

# --- File walking and counting --------------------------------------------

def categorize(filepath: Path):
    """Return the category for a file based on extension, or None to skip."""
    suffix = filepath.suffix.lower()
    for cat, exts in CATEGORIES.items():
        if suffix in exts:
            return cat
    return None


def walk_files(root: Path):
    """Yield (filepath, category) for every categorised file under root."""
    for dirpath, dirs, files in os.walk(root):
        # In-place filter so os.walk doesn't descend into excluded subtrees.
        dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS]
        for f in files:
            fp = Path(dirpath) / f
            cat = categorize(fp)
            if cat:
                yield fp, cat


def count_file(filepath: Path):
    """Return (lines, words, chars) for a file, swallowing read errors."""
    try:
        with open(filepath, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except Exception:
        return 0, 0, 0
    if not text:
        return 0, 0, 0
    lines = text.count("\n") + (0 if text.endswith("\n") else 1)
    words = len(text.split())
    chars = len(text)
    return lines, words, chars


def assign_project(filepath: Path, repo_root: Path):
    """Return the project name a file belongs to."""
    try:
        rel = filepath.resolve().relative_to(repo_root.resolve())
    except ValueError:
        return "main_app"
    if not rel.parts:
        return "main_app"
    top = rel.parts[0]
    return top if top in PROJECT_DIRS else "main_app"


# --- Stats aggregation -----------------------------------------------------

def collect_stats(repo_root: Path):
    """Walk the repo, return nested dict project → category → counts."""
    stats = defaultdict(
        lambda: defaultdict(lambda: {"lines": 0, "words": 0, "chars": 0, "files": 0})
    )
    for fp, cat in walk_files(repo_root):
        proj = assign_project(fp, repo_root)
        lines, words, chars = count_file(fp)
        bucket = stats[proj][cat]
        bucket["lines"] += lines
        bucket["words"] += words
        bucket["chars"] += chars
        bucket["files"] += 1
    return dict(stats)


def aggregate(stats):
    """Aggregate per-project / per-category stats into category totals + grand."""
    totals = defaultdict(lambda: {"lines": 0, "words": 0, "chars": 0, "files": 0})
    grand = {"lines": 0, "words": 0, "chars": 0, "files": 0}
    for proj, cats in stats.items():
        for cat, m in cats.items():
            for k, v in m.items():
                totals[cat][k] += v
                grand[k] += v
    return dict(totals), grand


# --- Git helpers -----------------------------------------------------------

def git_run(repo_root: Path, *args: str) -> str:
    """Run git -C <repo> args, return stdout text. Returns '' on any failure."""
    try:
        result = subprocess.run(
            ["git", "-C", str(repo_root)] + list(args),
            capture_output=True, text=True, timeout=60,
        )
        return result.stdout
    except Exception:
        return ""


def git_first_commit_date(repo_root: Path) -> str:
    """Return the ISO date of the repo's earliest commit, or '' if unknown."""
    out = git_run(repo_root, "log", "--reverse", "--format=%ai", "--date-order")
    for line in out.splitlines():
        if line.strip():
            return line.split()[0]
    return ""


def git_commits_since(repo_root: Path, since_iso: str) -> int:
    """Return the number of commits since the given ISO date string."""
    out = git_run(repo_root, "log", "--since", since_iso, "--oneline")
    return sum(1 for line in out.splitlines() if line.strip())


def git_files_changed_since(repo_root: Path, since_iso: str) -> int:
    """Return the number of unique files changed since the given date."""
    out = git_run(repo_root, "log", "--since", since_iso, "--name-only", "--pretty=format:")
    files = {line for line in out.splitlines() if line.strip()}
    return len(files)


def git_authors_since(repo_root: Path, since_iso: str):
    """Return a list of (author, commit_count) since the given date."""
    out = git_run(repo_root, "shortlog", "-sn", "--since", since_iso, "HEAD")
    rows = []
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        # Format: "  <count>\t<author name>"
        parts = line.split(None, 1)
        if len(parts) == 2 and parts[0].isdigit():
            rows.append((parts[1], int(parts[0])))
    return rows


def git_diff_shortstat(repo_root: Path, ref_a: str, ref_b: str):
    """Return (insertions, deletions, files_changed) for ref_a..ref_b."""
    out = git_run(repo_root, "diff", "--shortstat", f"{ref_a}..{ref_b}")
    files_m = re.search(r"(\d+) files? changed", out)
    ins_m = re.search(r"(\d+) insertions?\(\+\)", out)
    del_m = re.search(r"(\d+) deletions?\(-\)", out)
    return (
        int(ins_m.group(1)) if ins_m else 0,
        int(del_m.group(1)) if del_m else 0,
        int(files_m.group(1)) if files_m else 0,
    )


def git_release_tags(repo_root: Path):
    """Return all tags matching v*, sorted by version."""
    out = git_run(repo_root, "tag", "-l", "v*", "--sort=v:refname")
    return [t.strip() for t in out.splitlines() if t.strip()]


def git_oldest_ancestor(repo_root: Path) -> str:
    """Return the SHA of the very first commit reachable from HEAD."""
    out = git_run(repo_root, "rev-list", "--max-parents=0", "HEAD").strip()
    # Multiple roots are possible (rare); pick the first.
    return out.splitlines()[0] if out else ""


# --- Fun stats -------------------------------------------------------------

def fun_stats(grand):
    """Return a list of (label, value) playful comparisons from totals dict."""
    chars = grand["chars"]
    words = grand["words"]
    lines = grand["lines"]
    out = []

    # Braille volumes: ~100K cells per standard volume.
    out.append((
        "Braille volumes (≈100K cells each)",
        f"{chars / BRAILLE_VOLUME_CELLS:.1f}",
    ))
    # Moby Dicks: 210K words.
    out.append((
        "Moby Dicks (~210K words)",
        f"{words / MOBY_DICK_WORDS:.1f}",
    ))
    # King James Bibles: 783K words.
    out.append((
        "King James Bibles (~783K words)",
        f"{words / KJV_BIBLE_WORDS:.2f}",
    ))
    # Read-aloud time at ~150 wpm.
    minutes = words / SPEAKING_WPM
    hours = minutes / 60
    out.append((
        "Read-aloud time at 150 wpm",
        f"{hours:.1f} hours ({minutes:,.0f} minutes)",
    ))
    # Printed pages.
    pages = lines / PRINTED_LINES_PER_PAGE
    out.append((
        "Printed pages (50 lines/page)",
        f"{pages:,.0f}",
    ))
    # Printed-stack height at 250 pages per inch.
    inches = pages / PAGES_PER_INCH
    out.append((
        "Stack-of-printed-pages height",
        f"{inches:.1f} inches ({inches * 2.54:.1f} cm)",
    ))
    return out


# --- Output formatting -----------------------------------------------------

def fmt_int(n) -> str:
    return f"{int(n):,}"


def print_table(rows, headers):
    """Print a simple plain-text table (screen-reader friendly bullets too)."""
    if not rows:
        print("(no rows)")
        return
    widths = [len(h) for h in headers]
    for row in rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(str(cell)))
    sep = " | "
    line = sep.join(h.ljust(widths[i]) for i, h in enumerate(headers))
    print(line)
    print("-" * len(line))
    for row in rows:
        print(sep.join(str(cell).ljust(widths[i]) for i, cell in enumerate(row)))


def print_section(title: str):
    print()
    print(f"=== {title} ===")


# --- Subcommand implementations -------------------------------------------

def cmd_all(args):
    """Print full repo statistics."""
    repo = REPO_ROOT
    print("Rigmeter — full repository statistics")
    print(f"Repo:   {repo}")
    print(f"As of:  {datetime.now().isoformat(timespec='seconds')}")

    stats = collect_stats(repo)
    totals, grand = aggregate(stats)

    print_section("Per-Project Breakdown")
    rows = []
    for proj in sorted(stats.keys()):
        ll = ww = ff = 0
        for _, m in stats[proj].items():
            ll += m["lines"]
            ww += m["words"]
            ff += m["files"]
        rows.append((proj, fmt_int(ll), fmt_int(ww), fmt_int(ff)))
    print_table(rows, ["Project", "Lines", "Words", "Files"])

    print_section("Per-Category Totals")
    rows = []
    for cat in ["code", "text_data", "docs", "build"]:
        m = totals.get(cat, {"lines": 0, "words": 0, "chars": 0, "files": 0})
        rows.append((cat, fmt_int(m["lines"]), fmt_int(m["words"]), fmt_int(m["files"])))
    print_table(rows, ["Category", "Lines", "Words", "Files"])

    print_section("Grand Total")
    print(f"  Files: {fmt_int(grand['files'])}")
    print(f"  Lines: {fmt_int(grand['lines'])}")
    print(f"  Words: {fmt_int(grand['words'])}")
    print(f"  Chars: {fmt_int(grand['chars'])}")

    print_section("Fun Comparisons")
    for label, val in fun_stats(grand):
        print(f"  {label}: {val}")


def cmd_window(args, label: str, days: int):
    """Generic git-activity report for a time window N days back from today."""
    repo = REPO_ROOT
    since_dt = (datetime.now(timezone.utc) - timedelta(days=days)).date().isoformat()
    print(f"Rigmeter — {label} activity (since {since_dt})")
    print()
    commits = git_commits_since(repo, since_dt)
    files = git_files_changed_since(repo, since_dt)
    print(f"  Commits:           {fmt_int(commits)}")
    print(f"  Unique files:      {fmt_int(files)}")

    # Insertions and deletions from the last commit before the window start to HEAD.
    base = git_run(repo, "rev-list", "-1", "--before", since_dt, "HEAD").strip()
    if base:
        ins, dels, fch = git_diff_shortstat(repo, base, "HEAD")
        net = ins - dels
        print(f"  Insertions:        {fmt_int(ins)}")
        print(f"  Deletions:         {fmt_int(dels)}")
        print(f"  Net line change:   {net:+,}")
        print(f"  Files in diff:     {fmt_int(fch)}")

    authors = git_authors_since(repo, since_dt)
    if authors:
        print()
        print("  Authors:")
        for name, count in authors:
            print(f"    {name}: {count} commit{'s' if count != 1 else ''}")


def cmd_today(args):
    return cmd_window(args, "today's", 1)


def cmd_week(args):
    return cmd_window(args, "this week's", 7)


def cmd_month(args):
    return cmd_window(args, "this month's", 30)


def cmd_year(args):
    return cmd_window(args, "this year's", 365)


def cmd_start(args):
    """Cumulative stats since the very first commit reachable from HEAD."""
    repo = REPO_ROOT
    first_date = git_first_commit_date(repo)
    first_sha = git_oldest_ancestor(repo)
    print("Rigmeter — cumulative since project start")
    print()
    if first_date:
        print(f"  First commit date: {first_date}")
    if first_sha:
        print(f"  First commit SHA:  {first_sha[:12]}")

    if first_sha:
        ins, dels, fch = git_diff_shortstat(repo, first_sha, "HEAD")
        commits = git_commits_since(repo, first_date) if first_date else 0
        print()
        print(f"  Total commits:     {fmt_int(commits)}")
        print(f"  Insertions:        {fmt_int(ins)}")
        print(f"  Deletions:         {fmt_int(dels)}")
        print(f"  Net line change:   {(ins - dels):+,}")
        print(f"  Files in diff:     {fmt_int(fch)}")
    else:
        print("  (could not resolve first commit)")


def cmd_releases(args):
    """List all release tags found on the repo."""
    repo = REPO_ROOT
    tags = git_release_tags(repo)
    if not tags:
        print("No release tags found.")
        return
    print(f"Rigmeter — {len(tags)} release tag(s):")
    for t in tags:
        print(f"  {t}")


def cmd_release(args):
    """Print stats for one release vs the prior tagged release."""
    repo = REPO_ROOT
    target = args.tag
    tags = git_release_tags(repo)
    if target not in tags:
        print(f"Tag {target!r} not found.")
        if tags:
            print("Available tags:")
            for t in tags:
                print(f"  {t}")
        sys.exit(1)
    idx = tags.index(target)
    if idx == 0:
        print(f"{target} is the first tagged release; no prior tag to diff against.")
        return
    prev = tags[idx - 1]
    ins, dels, fch = git_diff_shortstat(repo, prev, target)
    print(f"Rigmeter — {target} (vs {prev})")
    print()
    print(f"  Files changed:    {fmt_int(fch)}")
    print(f"  Insertions:       {fmt_int(ins)}")
    print(f"  Deletions:        {fmt_int(dels)}")
    print(f"  Net line change:  {(ins - dels):+,}")


def cmd_compare(args):
    """Side-by-side totals against another local repo path."""
    other = Path(args.other_repo).resolve()
    if not other.exists() or not other.is_dir():
        print(f"Path does not exist or is not a directory: {other}")
        sys.exit(1)

    print(f"Rigmeter — comparison")
    print(f"  This repo:  {REPO_ROOT}")
    print(f"  Other repo: {other}")

    print_section("Collecting stats…")
    here = collect_stats(REPO_ROOT)
    _, here_grand = aggregate(here)
    there = collect_stats(other)
    _, there_grand = aggregate(there)

    print_section("Side-by-side totals")
    rows = []
    for k in ("files", "lines", "words", "chars"):
        rows.append((k, fmt_int(here_grand[k]), fmt_int(there_grand[k]),
                     f"{here_grand[k] - there_grand[k]:+,}"))
    print_table(rows, ["Metric", "This repo", "Other repo", "Delta"])

    print_section("Fun comparisons — this repo")
    for label, val in fun_stats(here_grand):
        print(f"  {label}: {val}")
    print_section("Fun comparisons — other repo")
    for label, val in fun_stats(there_grand):
        print(f"  {label}: {val}")


def cmd_fun(args):
    """Print just the fun comparisons, prominently."""
    repo = REPO_ROOT
    stats = collect_stats(repo)
    _, grand = aggregate(stats)
    print("Rigmeter — fun stats")
    print()
    print(
        f"Across {fmt_int(grand['files'])} files, "
        f"{fmt_int(grand['lines'])} lines, "
        f"{fmt_int(grand['words'])} words, "
        f"{fmt_int(grand['chars'])} characters…"
    )
    print()
    for label, val in fun_stats(grand):
        print(f"  {label}: {val}")


# --- CLI entry -------------------------------------------------------------

def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="rigmeter",
        description=(
            "JJ Flexible Radio Access source statistics tool. "
            "Counts lines, words, files; reports git activity over time windows; "
            "diffs releases and other repos; provides playful comparisons "
            "(braille volumes, Moby Dicks, Bibles)."
        ),
    )
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("all", help="Full repository statistics right now")
    sub.add_parser("today", help="Git activity in the last 1 day")
    sub.add_parser("week", help="Git activity in the last 7 days")
    sub.add_parser("month", help="Git activity in the last 30 days")
    sub.add_parser("year", help="Git activity in the last 365 days")
    sub.add_parser("start", help="Cumulative stats since the first commit")
    sub.add_parser("releases", help="List all release tags")
    sub.add_parser("fun", help="Playful comparisons (Moby Dicks, Bibles, etc.)")

    p_release = sub.add_parser("release", help="Stats for a release vs prior tag")
    p_release.add_argument("tag", help="Release tag to report on, e.g. v4.1.16")

    p_compare = sub.add_parser("compare", help="Side-by-side totals vs another local repo")
    p_compare.add_argument("other_repo", help="Path to another local repo (e.g. Jim's JJ Radio)")

    args = parser.parse_args(argv)
    handlers = {
        "all":      cmd_all,
        "today":    cmd_today,
        "week":     cmd_week,
        "month":    cmd_month,
        "year":     cmd_year,
        "start":    cmd_start,
        "releases": cmd_releases,
        "release":  cmd_release,
        "compare":  cmd_compare,
        "fun":      cmd_fun,
    }
    handlers[args.cmd](args)


if __name__ == "__main__":
    main()
