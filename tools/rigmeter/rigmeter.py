#!/usr/bin/env python3
"""rigmeter — JJ Flexible Radio Access source statistics tool.

A curiosity-driven CLI for counting lines, words, and files across the
JJFlex codebase, with per-project breakdown, time-window git activity,
release diffs, cross-repo comparison, growth deltas across spans, JSON
snapshot persistence, and playful comparisons.

The name is a ham-pun: rig meter = code meter. v1.2 layers tokei on top
of the v1.1 line counter to split each language into pure code, comments,
and blank lines; adds author attribution via git blame; introduces
sprint, debt, and forgotten subcommands; surfaces a docs-to-code ratio
and an ASCII sparkline trend; supports json/markdown/csv output formats
alongside the screen-reader-friendly default; and ships an interactive
menu wizard for human exploration.

Usage:
    python tools/rigmeter/rigmeter.py --help            # full subcommand list
    python tools/rigmeter/rigmeter.py --interactive     # menu wizard
    python tools/rigmeter/rigmeter.py all
    python tools/rigmeter/rigmeter.py today
    python tools/rigmeter/rigmeter.py growth v4.1.15
    python tools/rigmeter/rigmeter.py snapshot
    python tools/rigmeter/rigmeter.py authors           # NEW v1.2: git-blame attribution
    python tools/rigmeter/rigmeter.py debt              # NEW v1.2: TODO/FIXME/HACK counter
    python tools/rigmeter/rigmeter.py forgotten         # NEW v1.2: dead-code candidates

Many subcommands accept --format text|json|markdown|csv (text is default,
non-tabular bullets, screen-reader-friendly), --verbose for debug logging,
--no-fetch to skip the auto-tokei-download path, and --explain to see
file accounting.

See the project_rigmeter_stats_tool.md memory note for the full design.
"""

import argparse
import io
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import zipfile
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Set, Tuple

RIGMETER_VERSION = "1.2.0"
SCHEMA_VERSION = 2  # v1.2 added tokei dimensions + author attribution

# Module-level logger; verbosity configured in main() based on --verbose.
log = logging.getLogger("rigmeter")

# --- Configuration ---------------------------------------------------------

# Repo root is two levels up from this file: tools/rigmeter/rigmeter.py → repo root.
REPO_ROOT = Path(__file__).resolve().parent.parent.parent

# File extension → language (sub-buckets within the 'code' category).
CODE_LANGUAGES = {
    ".vb":   "vb",
    ".cs":   "cs",
    ".xaml": "xaml",
    ".py":   "py",
}

# File extension → category. Anything not in any category gets skipped with
# reason "unknown_extension".
CATEGORIES = {
    "code":      set(CODE_LANGUAGES.keys()),
    "text_data": {".txt", ".json", ".xml", ".yml", ".yaml",
                  ".resx", ".config", ".settings"},
    "docs":      {".md", ".htm", ".html", ".rst"},
    "build":     {".bat", ".ps1", ".sln",
                  ".vbproj", ".csproj", ".pyproj",
                  ".nsi", ".hhc", ".hhp",
                  ".props", ".targets", ".manifest",
                  ".editorconfig", ".gitattributes", ".gitignore"},
}
CATEGORY_ORDER = ("code", "text_data", "docs", "build")

# Explicit binary deny-list. Checked BEFORE category lookup so e.g. an image
# accidentally added to a category set still gets excluded. Closes the silent
# miscount mode that v1 was vulnerable to.
BINARY_EXTENSIONS = {
    # Windows binaries
    ".dll", ".exe", ".pdb", ".lib", ".obj", ".so", ".dylib", ".sys",
    # Help / installers / archives
    ".chm", ".zip", ".tar", ".gz", ".7z", ".bz2", ".xz",
    ".nupkg", ".msi", ".cab",
    # Documents (binary formats)
    ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt",
    # Images
    ".png", ".jpg", ".jpeg", ".gif", ".bmp",
    ".tiff", ".tif", ".ico", ".webp", ".svg",
    # Audio / video
    ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac",
    ".mp4", ".avi", ".mov", ".mkv", ".webm", ".wmv",
    # Fonts
    ".ttf", ".otf", ".ttc", ".eot", ".woff", ".woff2",
    # .NET build outputs (sometimes leak into git)
    ".cache", ".resources", ".baml",
    # Crypto / signing
    ".snk", ".pfx", ".cer", ".crt", ".key",
    # Generic binaries / data blobs
    ".bin", ".dat", ".db", ".sqlite", ".sqlite3",
}
# SVG note: SVG is technically text/XML, but it functions as an image asset.
# Treating it as binary aligns with intent (no one wants SVG markup counted
# as docs).

# Repo-relative path prefixes that mark vendor / upstream code.
# Path-prefix matching, not basename — keeps it explicit.
VENDOR_DIR_PREFIXES = (
    "FlexLib_API/",
    "P-Opus-master/",
    "PortAudioSharp-src-0.19.3/",
)

# Repo-relative path prefixes that mark derived / regenerated artefacts.
# Most of these are .gitignored so git ls-files won't return them in live
# mode anyway, but keeping explicit prefixes here gives belt-and-suspenders
# coverage for ref-mode (where the path may have been historically committed)
# and the os.walk fallback path.
DERIVED_PATH_PREFIXES = (
    "docs/help/pages/",
    "docs/help/md/whats-new.md",
)

# A first-line marker any generated file can carry to opt out of stats.
# Cheap to check and forward-proof — future generated files self-identify
# without needing rigmeter code changes. Looks for the literal token in the
# first GENERATED_MARKER_HEAD_BYTES of the file.
GENERATED_MARKER_TOKEN = "rigmeter:generated"
GENERATED_MARKER_HEAD_BYTES = 500

# Directory names skipped at any depth. Mostly redundant once we use
# git ls-files (gitignored content is already absent), but kept as
# defence-in-depth for the os.walk fallback and ref-mode.
EXCLUDE_DIR_NAMES = {
    ".git", ".vs", ".vscode", ".idea",
    "bin", "obj", "Debug", "Release",
    "node_modules", "packages",
    "__pycache__", ".pytest_cache",
    "Old-Readme",
}

# Snapshot persistence: NAS primary, %LOCALAPPDATA%\rigmeter\snapshots\
# fallback. Snapshots live OUTSIDE the source tree (never in tools/rigmeter/).
NAS_SNAPSHOT_DIR = Path(r"\\nas.macaw-jazz.ts.net\jjflex\historical\stats")

def _local_snapshot_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base) / "rigmeter" / "snapshots"
    # Non-Windows or stripped env: home-dir fallback
    return Path.home() / ".rigmeter" / "snapshots"

LOCAL_SNAPSHOT_DIR = _local_snapshot_dir()

# --- Fun-stat constants ----------------------------------------------------

BRAILLE_VOLUME_CELLS   = 100_000   # ≈ standard braille volume capacity
MOBY_DICK_WORDS        = 210_000
KJV_BIBLE_WORDS        = 783_000
SPEAKING_WPM           = 150
PRINTED_LINES_PER_PAGE = 50
PAGES_PER_INCH         = 250

# --- Data model ------------------------------------------------------------

@dataclass(frozen=True)
class FileRecord:
    """Per-file classification, derived from path alone (no content I/O)."""
    relpath: str             # POSIX-style, repo-relative
    project: str             # top-level directory name, or "main_app" for root files
    category: Optional[str]  # "code"/"text_data"/"docs"/"build" or None when skipped
    language: Optional[str]  # within "code": "vb"/"cs"/"xaml"/"py"; None otherwise
    is_vendor: bool
    is_derived: bool
    skip_reason: Optional[str]
    # skip_reason values: None (included), "binary", "derived",
    # "unknown_extension", "excluded_dir", "unreadable"


@dataclass
class CategoryCounts:
    files: int = 0
    lines: int = 0       # Total physical lines (rigmeter v1.1 counter)
    words: int = 0
    chars: int = 0
    pure_code: int = 0   # tokei "code" — non-comment, non-blank lines (v1.2)
    comments: int = 0    # tokei "comments" (v1.2)
    blanks: int = 0      # tokei "blanks" (v1.2)
    by_language: Dict[str, "CategoryCounts"] = field(default_factory=dict)

    def add(self, lines: int, words: int, chars: int) -> None:
        self.files += 1
        self.lines += lines
        self.words += words
        self.chars += chars

    def add_tokei(self, pure_code: int, comments: int, blanks: int) -> None:
        """Layer tokei dimensions on top of the file already accounted-for
        via add(). Doesn't touch files/lines/words/chars; tokei numbers are
        a separate axis (pure_code + comments + blanks ≈ lines, but rigmeter
        and tokei count physical lines slightly differently — keeping both
        means consumers can pick whichever model they prefer)."""
        self.pure_code += pure_code
        self.comments += comments
        self.blanks += blanks

    def add_language(self, language: str, lines: int, words: int, chars: int) -> None:
        bucket = self.by_language.setdefault(language, CategoryCounts())
        bucket.add(lines, words, chars)

    def add_language_tokei(self, language: str, pure_code: int, comments: int, blanks: int) -> None:
        bucket = self.by_language.setdefault(language, CategoryCounts())
        bucket.add_tokei(pure_code, comments, blanks)

    def comment_density(self) -> float:
        """Ratio of comments to (pure_code + comments). 0.0 when neither
        present. Neutral metric, not a quality judgment — Noel likes
        commenting; this just observes how much there is."""
        denom = self.pure_code + self.comments
        return self.comments / denom if denom else 0.0

    def to_dict(self) -> Dict[str, Any]:
        d: Dict[str, Any] = {
            "files": self.files,
            "lines": self.lines,
            "words": self.words,
            "chars": self.chars,
        }
        # Only include tokei dimensions when present, to keep older snapshots
        # round-trippable without injecting zero fields they don't have.
        if self.pure_code or self.comments or self.blanks:
            d["pure_code"] = self.pure_code
            d["comments"] = self.comments
            d["blanks"]   = self.blanks
        if self.by_language:
            d["by_language"] = {
                lang: bucket.to_dict()
                for lang, bucket in sorted(self.by_language.items())
            }
        return d


@dataclass
class CategoryBundle:
    """Per-category counts for one rollup (authored, vendor, or combined)."""
    code:      CategoryCounts = field(default_factory=CategoryCounts)
    text_data: CategoryCounts = field(default_factory=CategoryCounts)
    docs:      CategoryCounts = field(default_factory=CategoryCounts)
    build:     CategoryCounts = field(default_factory=CategoryCounts)

    def for_category(self, cat: str) -> CategoryCounts:
        return getattr(self, cat)

    def grand(self) -> Tuple[int, int, int, int]:
        f = sum(self.for_category(c).files for c in CATEGORY_ORDER)
        l = sum(self.for_category(c).lines for c in CATEGORY_ORDER)
        w = sum(self.for_category(c).words for c in CATEGORY_ORDER)
        ch = sum(self.for_category(c).chars for c in CATEGORY_ORDER)
        return f, l, w, ch

    def to_dict(self) -> Dict[str, Any]:
        f, l, w, ch = self.grand()
        return {
            "files": f, "lines": l, "words": w, "chars": ch,
            "by_category": {cat: self.for_category(cat).to_dict() for cat in CATEGORY_ORDER},
        }

    def absorb(self, other: "CategoryBundle") -> None:
        for cat in CATEGORY_ORDER:
            dst = self.for_category(cat)
            src = other.for_category(cat)
            dst.files += src.files
            dst.lines += src.lines
            dst.words += src.words
            dst.chars += src.chars
            dst.pure_code += src.pure_code
            dst.comments += src.comments
            dst.blanks += src.blanks
            for lang, lc in src.by_language.items():
                dl = dst.by_language.setdefault(lang, CategoryCounts())
                dl.files += lc.files
                dl.lines += lc.lines
                dl.words += lc.words
                dl.chars += lc.chars
                dl.pure_code += lc.pure_code
                dl.comments += lc.comments
                dl.blanks += lc.blanks


@dataclass
class ProjectBundle:
    name: str
    is_vendor: bool
    counts: CategoryBundle = field(default_factory=CategoryBundle)

    def to_dict(self) -> Dict[str, Any]:
        d: Dict[str, Any] = {"is_vendor": self.is_vendor}
        d.update(self.counts.to_dict())
        return d


@dataclass
class FileAccounting:
    """Tracks which files were included vs excluded, with example paths per
    skip reason. Examples truncated for JSON output, fuller for --explain."""
    EXAMPLES_CAP_JSON = 5
    EXAMPLES_CAP_EXPLAIN = 20

    included: int = 0
    binary:           Tuple[int, List[str]] = field(default_factory=lambda: [0, []])
    derived:          Tuple[int, List[str]] = field(default_factory=lambda: [0, []])
    unknown_extension: Tuple[int, List[str]] = field(default_factory=lambda: [0, []])
    excluded_dir:     Tuple[int, List[str]] = field(default_factory=lambda: [0, []])
    unreadable:       Tuple[int, List[str]] = field(default_factory=lambda: [0, []])

    def record_skip(self, reason: str, relpath: str) -> None:
        slot = self._slot(reason)
        slot[0] += 1
        if len(slot[1]) < self.EXAMPLES_CAP_EXPLAIN:
            slot[1].append(relpath)

    def _slot(self, reason: str):
        if reason == "binary":            return self.binary
        if reason == "derived":           return self.derived
        if reason == "unknown_extension": return self.unknown_extension
        if reason == "excluded_dir":      return self.excluded_dir
        if reason == "unreadable":        return self.unreadable
        raise ValueError(f"Unknown skip reason: {reason!r}")

    def to_dict(self, examples_cap: int = EXAMPLES_CAP_JSON) -> Dict[str, Any]:
        def slot_dict(s):
            return {"count": s[0], "examples": s[1][:examples_cap]}
        return {
            "included": self.included,
            "excluded": {
                "binary":            slot_dict(self.binary),
                "derived":           slot_dict(self.derived),
                "unknown_extension": slot_dict(self.unknown_extension),
                "excluded_dir":      slot_dict(self.excluded_dir),
                "unreadable":        slot_dict(self.unreadable),
            },
        }


@dataclass
class Snapshot:
    """Full repo statistics at a single point — live tree or git ref."""
    repo_root_name: str
    head_sha: str
    head_ref: str       # "HEAD" or tag/branch name
    head_describe: str
    generated_at: str   # ISO 8601 with TZ

    authored: CategoryBundle = field(default_factory=CategoryBundle)
    vendor:   CategoryBundle = field(default_factory=CategoryBundle)
    by_project: Dict[str, ProjectBundle] = field(default_factory=dict)
    file_accounting: FileAccounting = field(default_factory=FileAccounting)

    def add_record(self, rec: FileRecord, lines: int, words: int, chars: int) -> None:
        bundle = self.vendor if rec.is_vendor else self.authored
        cat = bundle.for_category(rec.category)
        cat.add(lines, words, chars)
        if rec.language and rec.category == "code":
            cat.add_language(rec.language, lines, words, chars)
        proj = self.by_project.setdefault(
            rec.project, ProjectBundle(rec.project, rec.is_vendor)
        )
        pcat = proj.counts.for_category(rec.category)
        pcat.add(lines, words, chars)
        if rec.language and rec.category == "code":
            pcat.add_language(rec.language, lines, words, chars)
        self.file_accounting.included += 1

    def add_skip(self, rec: FileRecord) -> None:
        self.file_accounting.record_skip(rec.skip_reason, rec.relpath)

    def combined(self) -> CategoryBundle:
        c = CategoryBundle()
        c.absorb(self.authored)
        c.absorb(self.vendor)
        return c

    def to_dict(self) -> Dict[str, Any]:
        _, l_auth, w_auth, ch_auth = self.authored.grand()
        return {
            "schema_version":   SCHEMA_VERSION,
            "generated_at":     self.generated_at,
            "rigmeter_version": RIGMETER_VERSION,
            "head_sha":         self.head_sha,
            "head_ref":         self.head_ref,
            "head_describe":    self.head_describe,
            "repo_root_name":   self.repo_root_name,
            "totals": {
                "authored": self.authored.to_dict(),
                "vendor":   self.vendor.to_dict(),
                "combined": self.combined().to_dict(),
            },
            "by_project":      {n: p.to_dict() for n, p in sorted(self.by_project.items())},
            "file_accounting": self.file_accounting.to_dict(FileAccounting.EXAMPLES_CAP_JSON),
            "fun_stats":       fun_stats_dict(l_auth, w_auth, ch_auth),
        }

    @classmethod
    def from_dict(cls, d: Dict[str, Any]) -> "Snapshot":
        """Reconstruct a Snapshot from a JSON dict (file_accounting examples
        and per-file detail are NOT reconstructed — only the bundles needed
        for growth math)."""
        snap = cls(
            repo_root_name=d.get("repo_root_name", ""),
            head_sha=d.get("head_sha", ""),
            head_ref=d.get("head_ref", ""),
            head_describe=d.get("head_describe", ""),
            generated_at=d.get("generated_at", ""),
        )

        def _load_bundle(b: Dict[str, Any]) -> CategoryBundle:
            cb = CategoryBundle()
            for cat in CATEGORY_ORDER:
                cd = b.get("by_category", {}).get(cat, {})
                cc = cb.for_category(cat)
                cc.files = cd.get("files", 0)
                cc.lines = cd.get("lines", 0)
                cc.words = cd.get("words", 0)
                cc.chars = cd.get("chars", 0)
                cc.pure_code = cd.get("pure_code", 0)
                cc.comments  = cd.get("comments", 0)
                cc.blanks    = cd.get("blanks", 0)
                for lang, ld in cd.get("by_language", {}).items():
                    cc.by_language[lang] = CategoryCounts(
                        files=ld.get("files", 0),
                        lines=ld.get("lines", 0),
                        words=ld.get("words", 0),
                        chars=ld.get("chars", 0),
                        pure_code=ld.get("pure_code", 0),
                        comments=ld.get("comments", 0),
                        blanks=ld.get("blanks", 0),
                    )
            return cb

        totals = d.get("totals", {})
        snap.authored = _load_bundle(totals.get("authored", {}))
        snap.vendor   = _load_bundle(totals.get("vendor",   {}))

        for proj_name, pd in d.get("by_project", {}).items():
            pb = ProjectBundle(proj_name, pd.get("is_vendor", False))
            pb.counts = _load_bundle(pd)
            snap.by_project[proj_name] = pb

        return snap


# --- Path classification ---------------------------------------------------

def to_posix(path: str) -> str:
    return path.replace("\\", "/")


def assign_project(relpath: str) -> str:
    """Top-level directory name as project bucket; 'main_app' for root files."""
    if "/" in relpath:
        return relpath.split("/", 1)[0]
    return "main_app"


def classify_path(relpath: str) -> FileRecord:
    """Classify a file by its repo-relative POSIX path. No file I/O.

    Order of checks: excluded-dir → binary extension → derived path →
    unknown extension → category match. is_vendor and is_derived flags
    are populated even on skip (used for --explain attribution).
    """
    relpath = to_posix(relpath)
    parts = relpath.split("/")
    project = assign_project(relpath)
    is_vendor = any(relpath.startswith(p) for p in VENDOR_DIR_PREFIXES)
    is_derived = any(relpath.startswith(p) for p in DERIVED_PATH_PREFIXES)

    # Excluded-dir check: any segment except the basename matches.
    for part in parts[:-1]:
        if part in EXCLUDE_DIR_NAMES:
            return FileRecord(relpath, project, None, None,
                              is_vendor, is_derived, "excluded_dir")

    # Extension extraction. We use the last dot-separator only — multi-extension
    # names (e.g. .tar.gz) just see the trailing piece. Good enough for our deny-list.
    name = parts[-1]
    if "." in name:
        suffix = "." + name.rsplit(".", 1)[1].lower()
    else:
        # Special-case dotfiles whose entire name is the "extension"
        # (.gitignore, .editorconfig, .gitattributes — these ARE in CATEGORIES.build).
        suffix = name.lower() if name.startswith(".") else ""

    # Binary deny-list FIRST, before category lookup.
    if suffix in BINARY_EXTENSIONS:
        return FileRecord(relpath, project, None, None,
                          is_vendor, is_derived, "binary")

    # Derived-path exclusion.
    if is_derived:
        return FileRecord(relpath, project, None, None,
                          is_vendor, True, "derived")

    # Category match.
    category = None
    for cat, exts in CATEGORIES.items():
        if suffix in exts:
            category = cat
            break
    if category is None:
        return FileRecord(relpath, project, None, None,
                          is_vendor, is_derived, "unknown_extension")

    language = CODE_LANGUAGES.get(suffix) if category == "code" else None
    return FileRecord(relpath, project, category, language,
                      is_vendor, is_derived, None)


# --- Content reading -------------------------------------------------------

def count_text(text: str) -> Tuple[int, int, int]:
    """Return (lines, words, chars) for a string."""
    if not text:
        return 0, 0, 0
    lines = text.count("\n") + (0 if text.endswith("\n") else 1)
    words = len(text.split())
    chars = len(text)
    return lines, words, chars


def is_marked_generated(text: str) -> bool:
    """True if the file's first GENERATED_MARKER_HEAD_BYTES contain the
    rigmeter:generated marker token. Comment syntax-agnostic — works for
    HTML/XML (<!-- rigmeter:generated -->), Python (# rigmeter:generated),
    VB (' rigmeter:generated), etc."""
    return GENERATED_MARKER_TOKEN in text[:GENERATED_MARKER_HEAD_BYTES]


def read_file_text(filepath: Path) -> Optional[str]:
    """Read a file as UTF-8 text with error-replacement; None on any error."""
    try:
        with open(filepath, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except Exception:
        return None


# --- File enumeration ------------------------------------------------------

def enumerate_tracked_files(repo_root: Path) -> List[str]:
    """Repo-relative POSIX paths of git-tracked files.

    Falls back to os.walk if the path is not a git repo (e.g. cross-repo
    compare against a non-git copy). The fallback applies EXCLUDE_DIR_NAMES
    inline since git's gitignore filtering isn't available in that mode.
    """
    try:
        result = subprocess.run(
            ["git", "-C", str(repo_root), "ls-files"],
            capture_output=True, text=True, timeout=120,
        )
        if result.returncode == 0:
            return [to_posix(line.strip())
                    for line in result.stdout.splitlines() if line.strip()]
    except Exception:
        pass
    # Filesystem fallback (non-git path)
    paths: List[str] = []
    rr = repo_root.resolve()
    for dirpath, dirs, files in os.walk(rr):
        dirs[:] = [d for d in dirs if d not in EXCLUDE_DIR_NAMES]
        for f in files:
            fp = Path(dirpath) / f
            try:
                rel = fp.resolve().relative_to(rr)
            except ValueError:
                continue
            paths.append(to_posix(str(rel)))
    return paths


# --- Snapshot building (live tree) -----------------------------------------

def build_snapshot_live(repo_root: Path, no_fetch: bool = False, with_tokei: bool = True) -> Snapshot:
    """Build a Snapshot from the live working tree (git-tracked files).

    When with_tokei is True (default) and tokei is available (or fetchable),
    layer per-file pure_code/comments/blanks dimensions on top of the line
    counter. Pass no_fetch=True to skip the auto-download step (e.g., for
    offline/CI runs); detection-only still happens.
    """
    head_sha = git_run(repo_root, "rev-parse", "HEAD").strip()
    head_describe = git_run(repo_root, "describe", "--tags", "--always", "--dirty").strip()
    if not head_describe:
        head_describe = head_sha[:12] if head_sha else "(no git)"

    snap = Snapshot(
        repo_root_name=repo_root.name,
        head_sha=head_sha,
        head_ref="HEAD",
        head_describe=head_describe,
        generated_at=_iso_now(),
    )

    for relpath in enumerate_tracked_files(repo_root):
        rec = classify_path(relpath)
        if rec.skip_reason:
            snap.add_skip(rec)
            continue
        text = read_file_text(repo_root / relpath)
        if text is None:
            snap.add_skip(FileRecord(relpath, rec.project, None, None,
                                     rec.is_vendor, rec.is_derived, "unreadable"))
            continue
        if is_marked_generated(text):
            snap.add_skip(FileRecord(relpath, rec.project, None, None,
                                     rec.is_vendor, True, "derived"))
            continue
        lines, words, chars = count_text(text)
        snap.add_record(rec, lines, words, chars)

    # v1.2 — layer tokei pure_code/comments/blanks onto the same per-file records.
    # Failure here is non-fatal; the snapshot still has the v1.1 line counts.
    if with_tokei:
        tokei_path = ensure_tokei(no_fetch=no_fetch)
        if tokei_path is not None:
            data = run_tokei(tokei_path, repo_root)
            if data is not None:
                enrich_snapshot_with_tokei(snap, data, repo_root)
            else:
                log.debug("tokei: invocation produced no data; snapshot unenriched")
        else:
            log.debug("tokei: not available; snapshot unenriched (lines-only)")

    return snap


def _iso_now() -> str:
    """Local-time ISO 8601 with timezone offset."""
    return datetime.now().astimezone().isoformat(timespec="seconds")


# --- Snapshot building (git ref) -------------------------------------------

class GitCatFileBatch:
    """Pipe-based wrapper around `git cat-file --batch` for fast bulk reads.

    The batch protocol is binary, NOT line-based:
      Request:  <sha-or-ref>\\n  (written to stdin)
      Response: <sha> <type> <size>\\n<size bytes of content>\\n  (read from stdout)

    Reading line-by-line will desync on files without trailing newlines —
    this class reads exactly N bytes per response. Self-tests on construction
    against a known small blob to validate the protocol up-front.
    """

    def __init__(self, repo_root: Path):
        self._proc = subprocess.Popen(
            ["git", "-C", str(repo_root), "cat-file", "--batch"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            bufsize=0,
        )
        # Self-test against a known small file. If it fails, we want to know
        # immediately rather than after silently corrupting a snapshot.
        for probe in ("HEAD:.gitignore", "HEAD:LICENSE.txt", "HEAD:README.md"):
            content = self.get(probe)
            if content is not None and len(content) > 0:
                return
        # No suitable probe file found — let the first real call fail loudly.

    def get(self, ref_or_path: str) -> Optional[bytes]:
        """Read an object's content. ref_or_path is a SHA or `<ref>:<path>`.
        Returns None for missing/unresolvable objects."""
        try:
            self._proc.stdin.write((ref_or_path + "\n").encode("utf-8"))
            self._proc.stdin.flush()
            header = self._read_line()
            if not header or header.endswith(b" missing"):
                return None
            parts = header.split(b" ")
            if len(parts) < 3:
                return None
            try:
                size = int(parts[-1])
            except ValueError:
                return None
            content = self._read_exact(size)
            self._proc.stdout.read(1)  # trailing newline after content
            return content
        except Exception:
            return None

    def _read_line(self) -> bytes:
        chunks = []
        while True:
            b = self._proc.stdout.read(1)
            if not b or b == b"\n":
                break
            chunks.append(b)
        return b"".join(chunks)

    def _read_exact(self, n: int) -> bytes:
        chunks = []
        remaining = n
        while remaining > 0:
            chunk = self._proc.stdout.read(remaining)
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        return b"".join(chunks)

    def close(self):
        try:
            if self._proc.stdin:
                self._proc.stdin.close()
            self._proc.wait(timeout=5)
        except Exception:
            try:
                self._proc.kill()
            except Exception:
                pass

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def build_snapshot_at_ref(repo_root: Path, ref: str) -> Snapshot:
    """Build a Snapshot from a git ref's tree.

    Uses git ls-tree -r to enumerate paths and git cat-file --batch (via
    GitCatFileBatch) for content. Same classification + counting pipeline
    as the live-tree path; output Snapshot is structurally identical, so
    growth math reuses the same helpers.
    """
    sha = git_resolve_ref(repo_root, ref)
    if not sha:
        raise ValueError(f"Could not resolve ref: {ref!r}")
    describe = git_describe(repo_root, sha)

    snap = Snapshot(
        repo_root_name=repo_root.name,
        head_sha=sha,
        head_ref=ref,
        head_describe=describe or sha[:12],
        generated_at=_iso_now(),
    )

    ls = git_run(repo_root, "ls-tree", "-r", "--name-only", sha)
    paths = [to_posix(p.strip()) for p in ls.splitlines() if p.strip()]

    with GitCatFileBatch(repo_root) as cat:
        for relpath in paths:
            rec = classify_path(relpath)
            if rec.skip_reason:
                snap.add_skip(rec)
                continue
            content_bytes = cat.get(f"{sha}:{relpath}")
            if content_bytes is None:
                snap.add_skip(FileRecord(relpath, rec.project, None, None,
                                         rec.is_vendor, rec.is_derived, "unreadable"))
                continue
            try:
                text = content_bytes.decode("utf-8", errors="replace")
            except Exception:
                snap.add_skip(FileRecord(relpath, rec.project, None, None,
                                         rec.is_vendor, rec.is_derived, "unreadable"))
                continue
            if is_marked_generated(text):
                snap.add_skip(FileRecord(relpath, rec.project, None, None,
                                         rec.is_vendor, True, "derived"))
                continue
            lines, words, chars = count_text(text)
            snap.add_record(rec, lines, words, chars)

    return snap


# --- Tokei integration (v1.2) ---------------------------------------------
#
# Tokei (https://github.com/XAMPPRocky/tokei) is a single ~5 MB Rust binary
# that splits each language into pure-code, comment, and blank lines. We
# shell out to it for accurate comment-vs-code counting, then layer the
# numbers onto our existing per-file classifier so authored-vs-vendor and
# per-project semantics still apply.
#
# Detection order: $RIGMETER_TOKEI_PATH → cache (%LOCALAPPDATA%\rigmeter\tools\)
# → PATH. If absent and not --no-fetch, the tool is downloaded one-time from
# GitHub releases into the cache directory. Cache lives outside the source
# tree, mirroring the JSON-snapshot pattern (repo carries code, NAS/local-cache
# carries data).
#
# Tonal note: tokei numbers are reported observation-first ("17% of your
# code is comments"), never as a quality stick. Noel likes commenting; the
# data is for curiosity, not for prescriptions.

# Map from tokei language names to our internal language keys (CODE_LANGUAGES).
# Tokei's full language list is much larger, but we only care about the four
# we already track. Anything else from tokei is silently ignored.
TOKEI_LANGUAGE_MAP = {
    "VisualBasic":   "vb",
    "C#":            "cs",
    "Xaml":          "xaml",
    "XAML":          "xaml",  # tolerate either capitalization
    "Python":        "py",
}


def _tokei_cache_dir() -> Path:
    """Where downloaded tokei binaries live (and the install marker for TUI deps).
    Outside the source tree — repo carries code, local cache carries tools."""
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base) / "rigmeter" / "tools"
    return Path.home() / ".rigmeter" / "tools"


def _tokei_cache_binary() -> Path:
    """Cached tokei binary path."""
    name = "tokei.exe" if sys.platform == "win32" else "tokei"
    return _tokei_cache_dir() / name


def detect_tokei() -> Optional[Path]:
    """Find a usable tokei executable. Returns Path or None.

    Precedence: env var (RIGMETER_TOKEI_PATH) → cache dir → PATH.
    Power users with a system-installed tokei skip the cache entirely
    via the env var.
    """
    env_path = os.environ.get("RIGMETER_TOKEI_PATH")
    if env_path:
        p = Path(env_path)
        if p.exists():
            log.debug("tokei: using env-var path %s", p)
            return p
        log.debug("tokei: RIGMETER_TOKEI_PATH set to %s but file missing", env_path)

    cache = _tokei_cache_binary()
    if cache.exists():
        log.debug("tokei: using cached binary %s", cache)
        return cache

    which = shutil.which("tokei")
    if which:
        log.debug("tokei: using PATH binary %s", which)
        return Path(which)

    log.debug("tokei: not found via env, cache, or PATH")
    return None


def _tokei_release_asset_url() -> Optional[str]:
    """Query GitHub releases API for the latest tokei Windows asset URL.
    Returns None on any network or parse failure."""
    api_url = "https://api.github.com/repos/XAMPPRocky/tokei/releases/latest"
    try:
        import urllib.request
        req = urllib.request.Request(
            api_url,
            headers={"User-Agent": f"rigmeter/{RIGMETER_VERSION}"},
        )
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read())
    except Exception as e:
        log.debug("tokei: failed to fetch release info: %s", e)
        return None

    # Recent releases ship a zip; older releases sometimes shipped a bare exe.
    # Try the zip first (most common).
    candidates_zip = [
        "tokei-x86_64-pc-windows-msvc.zip",
        "tokei-x86_64-pc-windows-gnu.zip",
    ]
    candidates_exe = [
        "tokei-x86_64-pc-windows-msvc.exe",
    ]
    for asset in data.get("assets", []):
        name = asset.get("name", "")
        if name in candidates_zip:
            log.debug("tokei: matched zip asset %s", name)
            return asset.get("browser_download_url")
    for asset in data.get("assets", []):
        name = asset.get("name", "")
        if name in candidates_exe:
            log.debug("tokei: matched exe asset %s", name)
            return asset.get("browser_download_url")
    log.debug("tokei: no matching Windows asset in release")
    return None


def fetch_tokei(prompt_user: bool = True) -> Optional[Path]:
    """Download tokei to the cache directory and return the path.

    Behaviour:
    - If running interactively (stdin is a TTY) AND prompt_user is True,
      asks for confirmation first.
    - If non-interactive (e.g., autonomous batch, CI), downloads silently
      since rigmeter being invoked already implies user consent.
    - Returns None on prompt-decline or download failure.

    The download URL is GitHub's release CDN (HTTPS). No checksum is
    independently published per release; relying on TLS for integrity.
    """
    if prompt_user and sys.stdin.isatty():
        try:
            print()
            resp = input(
                "rigmeter needs tokei (~5 MB) to count code-vs-comments accurately.\n"
                "Download from github.com/XAMPPRocky/tokei/releases? [Y/n] "
            )
        except EOFError:
            resp = "y"
        if resp.strip().lower() in ("n", "no"):
            log.debug("tokei: user declined download")
            return None

    asset_url = _tokei_release_asset_url()
    if not asset_url:
        log.warning("tokei: could not resolve a download URL")
        return None

    log.info("tokei: downloading from %s", asset_url)
    try:
        import urllib.request
        req = urllib.request.Request(
            asset_url,
            headers={"User-Agent": f"rigmeter/{RIGMETER_VERSION}"},
        )
        with urllib.request.urlopen(req, timeout=120) as resp:
            payload = resp.read()
    except Exception as e:
        log.warning("tokei: download failed: %s", e)
        return None

    cache_dir = _tokei_cache_dir()
    try:
        cache_dir.mkdir(parents=True, exist_ok=True)
    except Exception as e:
        log.warning("tokei: could not create cache dir %s: %s", cache_dir, e)
        return None

    cache_path = _tokei_cache_binary()

    if asset_url.endswith(".zip"):
        try:
            with zipfile.ZipFile(io.BytesIO(payload)) as zf:
                exe_name = next(
                    (n for n in zf.namelist() if n.endswith("tokei.exe") or n.endswith("tokei")),
                    None,
                )
                if not exe_name:
                    log.warning("tokei: zip contained no tokei executable")
                    return None
                cache_path.write_bytes(zf.read(exe_name))
        except Exception as e:
            log.warning("tokei: zip extraction failed: %s", e)
            return None
    else:
        try:
            cache_path.write_bytes(payload)
        except Exception as e:
            log.warning("tokei: write failed: %s", e)
            return None

    # On Unix, mark executable. (No-op on Windows.)
    try:
        cache_path.chmod(cache_path.stat().st_mode | 0o111)
    except Exception:
        pass

    log.info("tokei: cached at %s (%d bytes)", cache_path, cache_path.stat().st_size)
    return cache_path


def ensure_tokei(no_fetch: bool = False) -> Optional[Path]:
    """Detect tokei or fetch it on demand. Returns Path or None.

    Honors --no-fetch (returns None if not already on disk). Otherwise
    triggers the download path the first time tokei is missing.
    """
    p = detect_tokei()
    if p is not None:
        return p
    if no_fetch:
        log.debug("tokei: missing and --no-fetch set; skipping download")
        return None
    return fetch_tokei(prompt_user=True)


def run_tokei(tokei_path: Path, repo_root: Path) -> Optional[Dict[str, Any]]:
    """Run tokei on the repo and return parsed JSON. None on failure.

    Excludes the same vendor / build / cache directories rigmeter excludes,
    so the per-language totals are roughly comparable. The per-file `--files`
    output is what we actually care about; the totals are advisory."""
    excludes = []
    for d in EXCLUDE_DIR_NAMES:
        excludes += ["--exclude", d]
    cmd = [str(tokei_path), "--output", "json", "--files"] + excludes + [str(repo_root)]
    log.debug("tokei: running %s", " ".join(cmd))
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
    except Exception as e:
        log.warning("tokei: invocation failed: %s", e)
        return None
    if result.returncode != 0:
        log.warning("tokei: exit code %d, stderr=%r",
                    result.returncode, result.stderr.strip()[:200])
        return None
    try:
        return json.loads(result.stdout)
    except Exception as e:
        log.warning("tokei: JSON parse failed: %s", e)
        return None


def collect_tokei_per_file(tokei_data: Dict[str, Any], repo_root: Path) -> Dict[str, Tuple[int, int, int, str]]:
    """Walk tokei output and return {relpath: (pure_code, comments, blanks, lang)}.

    Path normalization: tokei reports absolute paths; we convert to repo-relative
    POSIX so they match our classify_path() keys.
    """
    out: Dict[str, Tuple[int, int, int, str]] = {}
    repo_root_resolved = repo_root.resolve()
    for lang_name, lang_data in tokei_data.items():
        if lang_name == "Total":
            continue
        rig_lang = TOKEI_LANGUAGE_MAP.get(lang_name)
        # Languages outside our map (Markdown, JSON, etc.) are skipped — we
        # only enrich code categories with tokei data.
        if rig_lang is None:
            continue
        reports = lang_data.get("reports", []) or []
        for report in reports:
            file_path = report.get("name", "")
            if not file_path:
                continue
            try:
                p = Path(file_path)
                if p.is_absolute():
                    rel = p.resolve().relative_to(repo_root_resolved)
                else:
                    rel = p
                rel_str = to_posix(str(rel))
            except Exception:
                continue
            stats = report.get("stats", {})
            out[rel_str] = (
                int(stats.get("code", 0)),
                int(stats.get("comments", 0)),
                int(stats.get("blanks", 0)),
                rig_lang,
            )
    return out


def enrich_snapshot_with_tokei(snap: "Snapshot", tokei_data: Dict[str, Any], repo_root: Path) -> int:
    """Apply tokei per-file dimensions onto the snapshot's existing records.

    Returns the count of files actually enriched (rigmeter classified them
    AND tokei reported on them). The mismatch between the two enumerations
    is logged at DEBUG.
    """
    per_file = collect_tokei_per_file(tokei_data, repo_root)
    enriched = 0

    # Build a mapping of relpath → (project, is_vendor, language) for all our
    # included code-category records. We rebuild this from classify_path()
    # rather than storing it on the Snapshot to keep the data-model size small.
    for relpath in enumerate_tracked_files(repo_root):
        rec = classify_path(relpath)
        if rec.skip_reason or rec.category != "code":
            continue
        tokei = per_file.get(relpath)
        if tokei is None:
            log.debug("tokei: no per-file data for %s", relpath)
            continue
        pure, comm, blnk, _lang = tokei
        bundle = snap.vendor if rec.is_vendor else snap.authored
        bundle.for_category("code").add_tokei(pure, comm, blnk)
        if rec.language:
            bundle.for_category("code").add_language_tokei(rec.language, pure, comm, blnk)
        proj = snap.by_project.get(rec.project)
        if proj:
            proj.counts.for_category("code").add_tokei(pure, comm, blnk)
            if rec.language:
                proj.counts.for_category("code").add_language_tokei(rec.language, pure, comm, blnk)
        enriched += 1

    log.debug("tokei: enriched %d files (of %d tokei reports)", enriched, len(per_file))
    return enriched


# --- Growth math -----------------------------------------------------------

@dataclass
class GrowthDelta:
    label: str
    before: int
    after: int
    delta: int
    pct: float

    @classmethod
    def from_pair(cls, label: str, before: int, after: int) -> "GrowthDelta":
        delta = after - before
        if before == 0:
            pct = float("inf") if after > 0 else 0.0
        else:
            pct = (after - before) / before * 100.0
        return cls(label=label, before=before, after=after, delta=delta, pct=pct)


def compute_growth_per_category(
    bundle_a: CategoryBundle, bundle_b: CategoryBundle, metric: str = "lines"
) -> List[GrowthDelta]:
    deltas = []
    for cat in CATEGORY_ORDER:
        before = getattr(bundle_a.for_category(cat), metric)
        after = getattr(bundle_b.for_category(cat), metric)
        deltas.append(GrowthDelta.from_pair(cat, before, after))
    return deltas


def compute_growth_per_language(
    bundle_a: CategoryBundle, bundle_b: CategoryBundle, metric: str = "lines"
) -> List[GrowthDelta]:
    code_a = bundle_a.for_category("code")
    code_b = bundle_b.for_category("code")
    languages = sorted(set(list(code_a.by_language.keys()) + list(code_b.by_language.keys())))
    return [
        GrowthDelta.from_pair(
            lang,
            getattr(code_a.by_language.get(lang, CategoryCounts()), metric),
            getattr(code_b.by_language.get(lang, CategoryCounts()), metric),
        )
        for lang in languages
    ]


# --- Snapshot persistence (JSON) -------------------------------------------

def snapshot_filename(snap: Snapshot, repo_root: Path) -> str:
    """Filename convention: <commit-date>-<short-sha>.json.

    Commit date (not generation date) so backfill produces meaningful
    chronological filenames. Short SHA disambiguates same-day commits.
    """
    short_sha = (snap.head_sha or "unknown")[:8]
    commit_date = git_commit_date(repo_root, snap.head_sha) if snap.head_sha else ""
    if not commit_date:
        # Fallback: use generation date
        commit_date = (snap.generated_at or _iso_now())[:10]
    # Strip "-dirty" from the picture — the SHA is the authoritative identity.
    return f"{commit_date}-{short_sha}.json"


def write_snapshot_json(snap: Snapshot, repo_root: Path) -> Tuple[Path, bool]:
    """Write snapshot JSON to NAS primary, %LOCALAPPDATA% fallback.

    Returns (path_written, used_nas_primary). When NAS is unreachable, the
    file lands in the local fallback directory and the caller can later run
    `snapshot --sync` to copy local-only files up to NAS.
    """
    fname = snapshot_filename(snap, repo_root)
    payload = json.dumps(snap.to_dict(), indent=2, sort_keys=True)

    # Try NAS first
    try:
        NAS_SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
        nas_path = NAS_SNAPSHOT_DIR / fname
        nas_path.write_text(payload, encoding="utf-8")
        return nas_path, True
    except Exception:
        pass

    # Fall back to local
    LOCAL_SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
    local_path = LOCAL_SNAPSHOT_DIR / fname
    local_path.write_text(payload, encoding="utf-8")
    return local_path, False


def list_snapshots() -> Dict[str, Path]:
    """Return {filename: full_path} for all snapshots across NAS + local.
    NAS wins on filename collisions (NAS is source of truth)."""
    snaps: Dict[str, Path] = {}
    # Local first (so NAS overwrites in the dict)
    if LOCAL_SNAPSHOT_DIR.exists():
        for p in LOCAL_SNAPSHOT_DIR.glob("*.json"):
            snaps[p.name] = p
    try:
        if NAS_SNAPSHOT_DIR.exists():
            for p in NAS_SNAPSHOT_DIR.glob("*.json"):
                snaps[p.name] = p
    except Exception:
        pass
    return snaps


def find_snapshot_by_date(date_prefix: str) -> Optional[Path]:
    """Find a snapshot file whose filename starts with the given date prefix.
    If multiple match, return the lexicographically last (latest SHA-suffix)."""
    all_snaps = list_snapshots()
    matches = sorted(name for name in all_snaps if name.startswith(date_prefix))
    if not matches:
        return None
    return all_snaps[matches[-1]]


def load_snapshot_json(path: Path) -> Snapshot:
    payload = path.read_text(encoding="utf-8")
    d = json.loads(payload)
    return Snapshot.from_dict(d)


def sync_local_snapshots_to_nas() -> Tuple[int, int]:
    """Copy local-only snapshot files up to NAS. Returns (copied, skipped)."""
    if not LOCAL_SNAPSHOT_DIR.exists():
        return 0, 0
    try:
        NAS_SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
    except Exception:
        return 0, 0  # NAS still unreachable
    nas_existing = {p.name for p in NAS_SNAPSHOT_DIR.glob("*.json")}
    copied = 0
    skipped = 0
    for p in LOCAL_SNAPSHOT_DIR.glob("*.json"):
        if p.name in nas_existing:
            skipped += 1
            continue
        try:
            shutil.copy2(p, NAS_SNAPSHOT_DIR / p.name)
            copied += 1
        except Exception:
            pass
    return copied, skipped


def hot_files_in_span(repo_root: Path, sha_a: str, sha_b: str, top_n: int = 10) -> List[Tuple[str, int]]:
    """Return (path, total_churn) for the top-N most-changed files between two SHAs.
    Total churn = insertions + deletions across all commits in the range."""
    out = git_run(repo_root, "log", "--numstat", "--format=", f"{sha_a}..{sha_b}")
    churn: Dict[str, int] = {}
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        parts = line.split("\t")
        if len(parts) != 3:
            continue
        ins_s, del_s, path = parts
        try:
            ins = int(ins_s) if ins_s != "-" else 0
            dels = int(del_s) if del_s != "-" else 0
        except ValueError:
            continue
        churn[path] = churn.get(path, 0) + ins + dels
    return sorted(churn.items(), key=lambda kv: kv[1], reverse=True)[:top_n]


# --- Git helpers -----------------------------------------------------------

def git_run(repo_root: Path, *args: str) -> str:
    """Run `git -C <repo> args`, return stdout text. Returns '' on failure."""
    try:
        result = subprocess.run(
            ["git", "-C", str(repo_root)] + list(args),
            capture_output=True, text=True, timeout=120,
        )
        return result.stdout
    except Exception:
        return ""


def git_first_commit_date(repo_root: Path) -> str:
    out = git_run(repo_root, "log", "--reverse", "--format=%ai", "--date-order")
    for line in out.splitlines():
        if line.strip():
            return line.split()[0]
    return ""


def git_commits_since(repo_root: Path, since_iso: str) -> int:
    out = git_run(repo_root, "log", "--since", since_iso, "--oneline")
    return sum(1 for line in out.splitlines() if line.strip())


def git_files_changed_since(repo_root: Path, since_iso: str) -> int:
    out = git_run(repo_root, "log", "--since", since_iso, "--name-only", "--pretty=format:")
    files = {line for line in out.splitlines() if line.strip()}
    return len(files)


def git_authors_since(repo_root: Path, since_iso: str):
    out = git_run(repo_root, "shortlog", "-sn", "--since", since_iso, "HEAD")
    rows = []
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        parts = line.split(None, 1)
        if len(parts) == 2 and parts[0].isdigit():
            rows.append((parts[1], int(parts[0])))
    return rows


def git_diff_shortstat(repo_root: Path, ref_a: str, ref_b: str):
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
    out = git_run(repo_root, "tag", "-l", "v*", "--sort=v:refname")
    return [t.strip() for t in out.splitlines() if t.strip()]


def git_oldest_ancestor(repo_root: Path) -> str:
    out = git_run(repo_root, "rev-list", "--max-parents=0", "HEAD").strip()
    return out.splitlines()[0] if out else ""


def git_resolve_ref(repo_root: Path, ref: str) -> str:
    """Return a ref's full SHA, or empty string if unresolvable."""
    return git_run(repo_root, "rev-parse", ref).strip()


def git_describe(repo_root: Path, ref: str) -> str:
    return git_run(repo_root, "describe", "--tags", "--always", ref).strip()


def git_commit_date(repo_root: Path, ref: str) -> str:
    """Return the ISO date of a ref's commit, or '' if unknown."""
    out = git_run(repo_root, "log", "-1", "--format=%ai", ref).strip()
    return out.split()[0] if out else ""


def git_commit_datetime(repo_root: Path, ref: str) -> Optional[datetime]:
    """Return a tz-aware datetime for a ref's commit, or None."""
    out = git_run(repo_root, "log", "-1", "--format=%aI", ref).strip()
    if not out:
        return None
    try:
        return datetime.fromisoformat(out)
    except Exception:
        return None


def resolve_ref_or_date(repo_root: Path, spec: str) -> str:
    """Resolve a spec that might be a ref OR a date string. Returns SHA."""
    sha = git_resolve_ref(repo_root, spec)
    if sha:
        return sha
    out = git_run(repo_root, "rev-list", "-1", f"--before={spec}", "HEAD").strip()
    return out


# --- Fun stats -------------------------------------------------------------

def fun_stats_dict(lines: int, words: int, chars: int) -> Dict[str, Any]:
    """Structured dict of fun-stat-derived numbers (for JSON output)."""
    pages = lines / PRINTED_LINES_PER_PAGE if lines else 0.0
    inches = pages / PAGES_PER_INCH if pages else 0.0
    minutes = words / SPEAKING_WPM if words else 0.0
    return {
        "braille_volumes":  round(chars / BRAILLE_VOLUME_CELLS, 2) if chars else 0.0,
        "moby_dicks":       round(words / MOBY_DICK_WORDS, 2)      if words else 0.0,
        "kjv_bibles":       round(words / KJV_BIBLE_WORDS, 3)      if words else 0.0,
        "read_aloud_hours": round(minutes / 60, 2)                 if minutes else 0.0,
        "printed_pages":    int(pages),
        "stack_inches":     round(inches, 2),
    }


def fun_stats_lines(lines: int, words: int, chars: int) -> List[Tuple[str, str]]:
    """Human-readable playful-comparison rows."""
    out: List[Tuple[str, str]] = []
    out.append((
        "Braille volumes (≈100K cells each)",
        f"{chars / BRAILLE_VOLUME_CELLS:.1f}" if chars else "0.0",
    ))
    out.append((
        "Moby Dicks (~210K words)",
        f"{words / MOBY_DICK_WORDS:.1f}" if words else "0.0",
    ))
    out.append((
        "King James Bibles (~783K words)",
        f"{words / KJV_BIBLE_WORDS:.2f}" if words else "0.00",
    ))
    minutes = words / SPEAKING_WPM if words else 0
    hours = minutes / 60 if minutes else 0
    out.append((
        "Read-aloud time at 150 wpm",
        f"{hours:.1f} hours ({minutes:,.0f} minutes)",
    ))
    pages = lines / PRINTED_LINES_PER_PAGE if lines else 0
    out.append((
        "Printed pages (50 lines/page)",
        f"{pages:,.0f}",
    ))
    inches = pages / PAGES_PER_INCH if pages else 0
    out.append((
        "Stack-of-printed-pages height",
        f"{inches:.1f} inches ({inches * 2.54:.1f} cm)",
    ))
    return out


# --- Output formatting -----------------------------------------------------

def fmt_int(n) -> str:
    return f"{int(n):,}"


def fmt_pct(value: float, decimals: int = 1) -> str:
    if value == float("inf"):
        return "∞%"
    if value == float("-inf"):
        return "-∞%"
    sign = "+" if value > 0 else ""
    return f"{sign}{value:.{decimals}f}%"


def print_records(rows, headers):
    """Print rows as bullet-listed records, one per line. Screen-reader friendly.

    v1.0 used a pipe-separated 'table' which NVDA reads as 'authored vertical
    bar 710 vertical bar 134 thousand 335 vertical bar...' — hostile to screen
    readers and a violation of the project rule (MEMORY.md User Preferences:
    "NO tables or diagrams"). v1.1.1 collapses every print site to natural-prose
    bullets so each record reads as one parseable line.

    Format per record:
        - <label>: <value1> <header1>, <value2> <header2>, ...
    where <label> is the first column and headers (lowercased) follow each value.
    Empty values are skipped to avoid orphan headers. Separator-style rows
    (label-only with empty rest) print as a plain indented line.
    """
    if not rows:
        print("  (no records)")
        return
    for row in rows:
        if not row:
            continue
        # Separator-row pattern: first column has text, all others are empty.
        if len(row) > 1 and all(not str(c).strip() for c in row[1:]):
            print(f"  {row[0]}")
            continue
        label = str(row[0])
        parts = []
        for i in range(1, len(row)):
            value = str(row[i]).strip()
            if not value:
                continue
            header_label = headers[i].lower() if i < len(headers) else ""
            if header_label:
                parts.append(f"{value} {header_label}")
            else:
                parts.append(value)
        if parts:
            print(f"  - {label}: {', '.join(parts)}")
        else:
            print(f"  - {label}")


def print_section(title: str):
    """Section header. Plain title with leading blank line and trailing colon —
    no '===' wrapper that NVDA would read as 'equals equals equals'."""
    print()
    print(f"{title}:")


def _bundle_row(name: str, bundle: CategoryBundle) -> Tuple[str, str, str, str, str]:
    f, l, w, ch = bundle.grand()
    return (name, fmt_int(f), fmt_int(l), fmt_int(w), fmt_int(ch))


def print_snapshot_full(snap: Snapshot, title: str = "Rigmeter — full repository statistics"):
    """Print the full v1.1 snapshot output: authored vs vendor split, per-project,
    per-category with per-language breakdown, grand totals, fun comparisons."""
    print(title)
    print(f"Repo:      {snap.repo_root_name}")
    print(f"As of:     {snap.generated_at}")
    print(f"Ref:       {snap.head_ref}  ({snap.head_describe})")
    print(f"SHA:       {snap.head_sha[:12]}")

    # --- Headline (authored vs vendor vs combined)
    print_section("Headline rollups")
    rows = [
        _bundle_row("authored",  snap.authored),
        _bundle_row("vendor",    snap.vendor),
        _bundle_row("combined",  snap.combined()),
    ]
    print_records(rows, ["Rollup", "Files", "Lines", "Words", "Chars"])

    # --- Per-project (authored first, vendor below separator)
    print_section("Per-project breakdown (lines)")
    authored_projects = sorted([p for p in snap.by_project.values() if not p.is_vendor],
                               key=lambda p: p.name)
    vendor_projects = sorted([p for p in snap.by_project.values() if p.is_vendor],
                             key=lambda p: p.name)
    rows: List[Tuple[str, ...]] = []
    for p in authored_projects:
        f, l, w, _ = p.counts.grand()
        rows.append((p.name, fmt_int(l), fmt_int(w), fmt_int(f)))
    if authored_projects and vendor_projects:
        rows.append(("--- vendor below ---", "", "", ""))
    for p in vendor_projects:
        f, l, w, _ = p.counts.grand()
        rows.append((f"{p.name} (vendor)", fmt_int(l), fmt_int(w), fmt_int(f)))
    print_records(rows, ["Project", "Lines", "Words", "Files"])

    # --- Per-category (authored)
    print_section("Per-category totals — authored only")
    rows = []
    for cat in CATEGORY_ORDER:
        c = snap.authored.for_category(cat)
        rows.append((cat, fmt_int(c.lines), fmt_int(c.words), fmt_int(c.files)))
    print_records(rows, ["Category", "Lines", "Words", "Files"])

    # --- Per-language within code (authored vs all-files)
    print_section("Code language breakdown")
    auth_code = snap.authored.for_category("code")
    all_code = snap.combined().for_category("code")
    auth_total_lines = auth_code.lines or 1
    all_total_lines = all_code.lines or 1
    rows = []
    languages = sorted(set(list(auth_code.by_language.keys()) + list(all_code.by_language.keys())))
    for lang in languages:
        a = auth_code.by_language.get(lang, CategoryCounts())
        c = all_code.by_language.get(lang, CategoryCounts())
        a_pct = (a.lines / auth_total_lines * 100) if auth_total_lines else 0
        c_pct = (c.lines / all_total_lines * 100) if all_total_lines else 0
        rows.append((
            lang,
            fmt_int(a.lines),
            f"{a_pct:.1f}%",
            fmt_int(c.lines),
            f"{c_pct:.1f}%",
        ))
    print_records(rows, ["Lang", "authored lines", "of authored", "all lines", "of all"])

    # --- Code structure (tokei) — only when tokei data was layered on
    auth_code_for_tokei = snap.authored.for_category("code")
    if auth_code_for_tokei.pure_code or auth_code_for_tokei.comments or auth_code_for_tokei.blanks:
        print_section("Code structure — authored (tokei)")
        rows = []
        rows.append((
            "total",
            fmt_int(auth_code_for_tokei.pure_code),
            fmt_int(auth_code_for_tokei.comments),
            fmt_int(auth_code_for_tokei.blanks),
            f"{auth_code_for_tokei.comment_density() * 100:.1f}%",
        ))
        for lang in sorted(auth_code_for_tokei.by_language.keys()):
            lc = auth_code_for_tokei.by_language[lang]
            if not (lc.pure_code or lc.comments or lc.blanks):
                continue
            rows.append((
                lang,
                fmt_int(lc.pure_code),
                fmt_int(lc.comments),
                fmt_int(lc.blanks),
                f"{lc.comment_density() * 100:.1f}%",
            ))
        print_records(rows, ["Language", "code lines", "comment lines",
                             "blank lines", "comment density"])

    # --- Per-category (vendor) only if non-zero
    v_grand = snap.vendor.grand()
    if v_grand[1] > 0:
        print_section("Per-category totals — vendor")
        rows = []
        for cat in CATEGORY_ORDER:
            c = snap.vendor.for_category(cat)
            rows.append((cat, fmt_int(c.lines), fmt_int(c.words), fmt_int(c.files)))
        print_records(rows, ["Category", "Lines", "Words", "Files"])

    # --- Grand total (authored — the headline that means "what we wrote")
    print_section("Grand total — authored")
    f, l, w, ch = snap.authored.grand()
    print(f"  Files: {fmt_int(f)}")
    print(f"  Lines: {fmt_int(l)}")
    print(f"  Words: {fmt_int(w)}")
    print(f"  Chars: {fmt_int(ch)}")

    # --- Fun comparisons (authored only — "what Noel and Jim wrote")
    print_section("Fun comparisons (authored only)")
    for label, val in fun_stats_lines(l, w, ch):
        print(f"  {label}: {val}")

    # --- File accounting summary
    print_section("File accounting")
    fa = snap.file_accounting
    print(f"  Included:                {fmt_int(fa.included)}")
    print(f"  Excluded — binary:       {fmt_int(fa.binary[0])}")
    print(f"  Excluded — derived:      {fmt_int(fa.derived[0])}")
    print(f"  Excluded — unknown ext:  {fmt_int(fa.unknown_extension[0])}")
    print(f"  Excluded — excluded dir: {fmt_int(fa.excluded_dir[0])}")
    print(f"  Excluded — unreadable:   {fmt_int(fa.unreadable[0])}")


def emit_snapshot_tabular(snap: "Snapshot", fmt: str) -> None:
    """Emit per-project + per-category tables in markdown or CSV.

    These formats are opt-in (--format=markdown / --format=csv) for sighted
    humans pasting into GitHub issues, docs, or spreadsheets. The default
    text output stays bullet-formatted for screen readers.
    """
    if fmt == "csv":
        sep = ","
        def row(*cells): return sep.join(str(c) for c in cells)
    elif fmt == "markdown":
        sep = " | "
        def row(*cells): return "| " + sep.join(str(c) for c in cells) + " |"
    else:
        return

    auth_grand = snap.authored.grand()
    vendor_grand = snap.vendor.grand()
    print(row("rollup", "files", "lines", "words", "chars"))
    if fmt == "markdown":
        print(row("---", "---", "---", "---", "---"))
    print(row("authored", *auth_grand))
    print(row("vendor",   *vendor_grand))
    print()
    print(row("project", "is_vendor", "files", "lines", "words", "chars"))
    if fmt == "markdown":
        print(row("---", "---", "---", "---", "---", "---"))
    for p in sorted(snap.by_project.values(), key=lambda p: p.name):
        f, l, w, ch = p.counts.grand()
        print(row(p.name, p.is_vendor, f, l, w, ch))
    print()
    print(row("category", "files", "lines", "pure_code", "comments", "blanks"))
    if fmt == "markdown":
        print(row("---", "---", "---", "---", "---", "---"))
    for cat in CATEGORY_ORDER:
        c = snap.authored.for_category(cat)
        print(row(cat, c.files, c.lines, c.pure_code, c.comments, c.blanks))


def print_explain(snap: Snapshot):
    """Show example included paths per category and excluded paths grouped by reason."""
    print_section("--explain: included files (sample per category)")
    # We don't store per-category included paths in the snapshot to keep memory
    # bounded. Re-walk classification just for the included sample (cheap).
    by_cat: Dict[str, List[str]] = {c: [] for c in CATEGORY_ORDER}
    cap = 20
    for relpath in enumerate_tracked_files(REPO_ROOT):
        rec = classify_path(relpath)
        if rec.skip_reason or rec.category is None:
            continue
        if len(by_cat[rec.category]) < cap:
            by_cat[rec.category].append(relpath)
    for cat in CATEGORY_ORDER:
        sample = by_cat[cat]
        c = snap.combined().for_category(cat)
        print(f"\n  [{cat}]  ({fmt_int(c.files)} total, showing first {len(sample)}):")
        for p in sample:
            print(f"    {p}")

    print_section("--explain: excluded files grouped by reason")
    fa = snap.file_accounting
    for label, slot in (
        ("binary",            fa.binary),
        ("derived",           fa.derived),
        ("unknown_extension", fa.unknown_extension),
        ("excluded_dir",      fa.excluded_dir),
        ("unreadable",        fa.unreadable),
    ):
        count, examples = slot
        print(f"\n  [{label}]  ({fmt_int(count)} total, showing first {len(examples)}):")
        for p in examples:
            print(f"    {p}")


def print_growth(snap_a: Snapshot, snap_b: Snapshot, days_elapsed: Optional[float] = None):
    """Print growth comparison between two snapshots. Code-base growth % and
    doc-base growth % are highlighted as headline lines (per Noel's ask)."""
    print(f"Rigmeter — growth comparison")
    print(f"  Before: {snap_a.head_ref}  ({snap_a.head_describe})  {snap_a.head_sha[:12]}")
    print(f"  After:  {snap_b.head_ref}  ({snap_b.head_describe})  {snap_b.head_sha[:12]}")
    if days_elapsed is not None and days_elapsed > 0:
        if days_elapsed >= 1:
            print(f"  Span:   {days_elapsed:.1f} days")
        else:
            hours = days_elapsed * 24
            if hours >= 1:
                print(f"  Span:   {hours:.1f} hours")
            else:
                print(f"  Span:   {days_elapsed * 86400:.0f} seconds")

    # --- Headline: code-base and doc-base growth %, authored only
    print_section("HEADLINE — authored growth (the numbers Noel asked for)")
    auth_code_a = snap_a.authored.for_category("code").lines
    auth_code_b = snap_b.authored.for_category("code").lines
    auth_docs_a = snap_a.authored.for_category("docs").lines
    auth_docs_b = snap_b.authored.for_category("docs").lines
    code_delta = GrowthDelta.from_pair("code", auth_code_a, auth_code_b)
    docs_delta = GrowthDelta.from_pair("docs", auth_docs_a, auth_docs_b)

    def _print_headline(label: str, d: GrowthDelta):
        rate_str = ""
        # Per-day rate normalization is only meaningful when the span is
        # at least ~1 day. Below that, the rate explodes and conveys nothing.
        if (days_elapsed and days_elapsed >= 1.0
                and d.pct not in (float("inf"), float("-inf"))):
            rate = d.pct / days_elapsed
            rate_str = f"  =  {rate:+.3f}%/day"
        print(f"  {label:25s} {fmt_int(d.before):>10} → {fmt_int(d.after):>10}  "
              f"({d.delta:+,} lines, {fmt_pct(d.pct)}{rate_str})")

    _print_headline("Code base lines:", code_delta)
    _print_headline("Doc base lines:",  docs_delta)

    # --- Docs-to-code ratio shift (always available; no tokei needed)
    ratio_a = docs_to_code_ratio(snap_a)
    ratio_b = docs_to_code_ratio(snap_b)
    if ratio_a > 0 or ratio_b > 0:
        delta_ratio = ratio_b - ratio_a
        print(f"  Docs-to-code ratio:       {ratio_a:5.2f}    →   {ratio_b:5.2f}      ({delta_ratio:+.2f})")

    # --- Pure-code and comment growth (only when both sides have tokei data)
    auth_code_a_obj = snap_a.authored.for_category("code")
    auth_code_b_obj = snap_b.authored.for_category("code")
    have_tokei = (auth_code_a_obj.pure_code or auth_code_a_obj.comments) and \
                 (auth_code_b_obj.pure_code or auth_code_b_obj.comments)
    if have_tokei:
        pure_delta = GrowthDelta.from_pair(
            "pure code", auth_code_a_obj.pure_code, auth_code_b_obj.pure_code,
        )
        comm_delta = GrowthDelta.from_pair(
            "comments", auth_code_a_obj.comments, auth_code_b_obj.comments,
        )
        _print_headline("Pure-code lines:", pure_delta)
        _print_headline("Comment lines:",   comm_delta)
        # Comment-density shift (observation, not prescription)
        before_density = auth_code_a_obj.comment_density() * 100
        after_density  = auth_code_b_obj.comment_density() * 100
        density_delta = after_density - before_density
        print(f"  Comment density:          {before_density:5.1f}%  → "
              f"{after_density:5.1f}%   ({density_delta:+.1f} pp)")
    else:
        # Authored growth was computed against rigmeter's line count; tokei
        # numbers are absent on at least one side. Note that explicitly so
        # users don't assume the comment-vs-code split is missing in error.
        if auth_code_b_obj.pure_code or auth_code_b_obj.comments:
            print()
            print("  (Pure-code/comment deltas unavailable: BEFORE snapshot has no tokei "
                  "data. Re-run snapshot at that ref with tokei available, or backfill.)")

    # --- Per-category authored growth (full table)
    print_section("Per-category authored growth (lines)")
    rows = []
    for d in compute_growth_per_category(snap_a.authored, snap_b.authored):
        rows.append((d.label, fmt_int(d.before), fmt_int(d.after),
                     f"{d.delta:+,}", fmt_pct(d.pct)))
    print_records(rows, ["Category", "before", "after", "delta", "growth"])

    # --- Per-language growth within code (authored only)
    print_section("Per-language growth within code — authored")
    rows = []
    for d in compute_growth_per_language(snap_a.authored, snap_b.authored):
        rows.append((d.label, fmt_int(d.before), fmt_int(d.after),
                     f"{d.delta:+,}", fmt_pct(d.pct)))
    print_records(rows, ["Language", "before", "after", "delta", "growth"])

    # --- Vendor growth (only if non-zero on either side)
    v_a = snap_a.vendor.grand()
    v_b = snap_b.vendor.grand()
    if v_a[1] > 0 or v_b[1] > 0:
        print_section("Per-category vendor growth (lines)")
        rows = []
        for d in compute_growth_per_category(snap_a.vendor, snap_b.vendor):
            rows.append((d.label, fmt_int(d.before), fmt_int(d.after),
                         f"{d.delta:+,}", fmt_pct(d.pct)))
        print_records(rows, ["Category", "before", "after", "delta", "growth"])

    # --- Hot files in span (Phase 10 bonus, reuses git log --numstat)
    if snap_a.head_sha and snap_b.head_sha and snap_a.head_sha != snap_b.head_sha:
        hot = hot_files_in_span(REPO_ROOT, snap_a.head_sha, snap_b.head_sha, top_n=10)
        if hot:
            print_section("Hot files in span (top 10 by churn)")
            rows = [(path, fmt_int(churn)) for path, churn in hot]
            print_records(rows, ["File", "Lines changed"])


# --- Subcommand implementations -------------------------------------------

def cmd_all(args):
    no_fetch = getattr(args, "no_fetch", False)
    snap = build_snapshot_live(REPO_ROOT, no_fetch=no_fetch)
    out_format = getattr(args, "output_format", "text")
    if out_format == "json":
        print(json.dumps(snap.to_dict(), indent=2, sort_keys=True))
        return
    if out_format in ("markdown", "csv"):
        emit_snapshot_tabular(snap, out_format)
        return
    print_snapshot_full(snap)

    # v1.2 — docs:code ratio plus sparkline trend (when ≥5 snapshots exist)
    ratio = docs_to_code_ratio(snap)
    if ratio > 0:
        print()
        print(f"  Docs-to-code ratio:  {ratio:.2f}  (authored docs / authored code, by lines)")
    cmd_trend_block(snap)

    if getattr(args, "explain", False):
        print_explain(snap)


def cmd_window(args, label: str, since_spec: str, since_display: str = None):
    """Generic git-activity report for a time window. Same as v1 — uses git's
    natural-language --since interpretation to avoid timezone bugs."""
    repo = REPO_ROOT
    display = since_display if since_display is not None else since_spec
    print(f"Rigmeter — {label} activity (since {display})")
    print()
    commits = git_commits_since(repo, since_spec)
    files = git_files_changed_since(repo, since_spec)
    print(f"  Commits:           {fmt_int(commits)}")
    print(f"  Unique files:      {fmt_int(files)}")

    base = git_run(repo, "rev-list", "-1", f"--before={since_spec}", "HEAD").strip()
    if base:
        ins, dels, fch = git_diff_shortstat(repo, base, "HEAD")
        net = ins - dels
        print(f"  Insertions:        {fmt_int(ins)}")
        print(f"  Deletions:         {fmt_int(dels)}")
        print(f"  Net line change:   {net:+,}")
        print(f"  Files in diff:     {fmt_int(fch)}")
        print()
        print(f"  (For size growth across this span, run: rigmeter growth {base[:12]})")

    authors = git_authors_since(repo, since_spec)
    if authors:
        print()
        print("  Authors:")
        for name, count in authors:
            print(f"    {name}: {count} commit{'s' if count != 1 else ''}")


def cmd_today(args):
    today_local = date.today().isoformat()
    return cmd_window(args, "today's", "midnight", f"midnight local time ({today_local})")


def cmd_week(args):
    return cmd_window(args, "this week's", "7 days ago")


def cmd_month(args):
    return cmd_window(args, "this month's", "30 days ago")


def cmd_year(args):
    return cmd_window(args, "this year's", "1 year ago")


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
    """List all release tags found on the repo, with days-since-prior column."""
    repo = REPO_ROOT
    tags = git_release_tags(repo)
    if not tags:
        print("No release tags found.")
        return
    print(f"Rigmeter — {len(tags)} release tag(s):")
    print()
    rows = []
    prev_dt = None
    for t in tags:
        d = git_commit_datetime(repo, t)
        if d is None:
            rows.append((t, "", ""))
            continue
        days = ""
        if prev_dt is not None:
            delta = d - prev_dt
            days = str(delta.days)
        rows.append((t, d.date().isoformat(), days))
        prev_dt = d
    print_records(rows, ["Tag", "released", "days since prior"])


def cmd_release(args):
    """Stats for one release vs the prior tagged release.

    With --growth, additionally builds full snapshots at both refs and shows
    per-category growth %s (code base + doc base headlines).
    """
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

    if getattr(args, "growth", False):
        print()
        print(f"--growth: building snapshots at {prev} and {target} …", file=sys.stderr)
        snap_a = build_snapshot_at_ref(repo, prev)
        snap_b = build_snapshot_at_ref(repo, target)
        dt_a = git_commit_datetime(repo, snap_a.head_sha)
        dt_b = git_commit_datetime(repo, snap_b.head_sha)
        days_elapsed = (dt_b - dt_a).total_seconds() / 86400.0 if (dt_a and dt_b) else None
        print_growth(snap_a, snap_b, days_elapsed)
        if getattr(args, "explain", False):
            print_explain(snap_b)


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
    no_fetch = getattr(args, "no_fetch", False)
    here = build_snapshot_live(REPO_ROOT, no_fetch=no_fetch)
    there = build_snapshot_live(other, no_fetch=no_fetch)

    # Headline side-by-side (authored only — the meaningful comparison)
    print_section("Side-by-side authored totals")
    rows = []
    for label, here_b, there_b in [
        ("files", here.authored.grand()[0], there.authored.grand()[0]),
        ("lines", here.authored.grand()[1], there.authored.grand()[1]),
        ("words", here.authored.grand()[2], there.authored.grand()[2]),
        ("chars", here.authored.grand()[3], there.authored.grand()[3]),
    ]:
        rows.append((label, fmt_int(here_b), fmt_int(there_b),
                     f"{here_b - there_b:+,}"))
    print_records(rows, ["Metric", "this authored", "other authored", "delta"])

    # Vendor row only if either side has any vendor — otherwise it looks broken.
    here_v = here.vendor.grand()
    there_v = there.vendor.grand()
    if here_v[1] > 0 or there_v[1] > 0:
        print_section("Side-by-side vendor totals")
        rows = []
        for label, here_b, there_b in [
            ("files", here_v[0], there_v[0]),
            ("lines", here_v[1], there_v[1]),
            ("words", here_v[2], there_v[2]),
            ("chars", here_v[3], there_v[3]),
        ]:
            rows.append((label, fmt_int(here_b), fmt_int(there_b),
                         f"{here_b - there_b:+,}"))
        print_records(rows, ["Metric", "this vendor", "other vendor", "delta"])

    print_section("Fun comparisons — this repo (authored)")
    _, l, w, ch = here.authored.grand()
    for label, val in fun_stats_lines(l, w, ch):
        print(f"  {label}: {val}")
    print_section("Fun comparisons — other repo (authored)")
    _, l, w, ch = there.authored.grand()
    for label, val in fun_stats_lines(l, w, ch):
        print(f"  {label}: {val}")


def cmd_fun(args):
    """Just the fun comparisons (authored only)."""
    snap = build_snapshot_live(REPO_ROOT, no_fetch=getattr(args, "no_fetch", False),
                               with_tokei=False)  # tokei not needed for fun
    f, l, w, ch = snap.authored.grand()
    print("Rigmeter — fun stats (authored only)")
    print()
    print(
        f"Across {fmt_int(f)} authored files, "
        f"{fmt_int(l)} lines, "
        f"{fmt_int(w)} words, "
        f"{fmt_int(ch)} characters…"
    )
    print()
    for label, val in fun_stats_lines(l, w, ch):
        print(f"  {label}: {val}")


def cmd_growth(args):
    """growth <ref-a> [<ref-b>] OR growth --since <date> OR growth --use-snapshots.

    Forms:
      rigmeter growth v4.1.15                       # v4.1.15..HEAD shorthand
      rigmeter growth v4.1.12 v4.1.16               # explicit pair
      rigmeter growth --since 2026-01-01            # date sugar (nearest prior commit)
      rigmeter growth --use-snapshots 2026-03 2026-04  # fast path: read from JSON
    """
    repo = REPO_ROOT

    # --use-snapshots branch: read JSON snapshots without re-walking git
    if args.use_snapshots:
        if not (args.ref_a and args.ref_b):
            print("growth --use-snapshots: need two date prefixes "
                  "(e.g. growth --use-snapshots 2026-03-08 2026-04-25)")
            sys.exit(1)
        path_a = find_snapshot_by_date(args.ref_a)
        path_b = find_snapshot_by_date(args.ref_b)
        if not path_a:
            print(f"No snapshot found matching date prefix {args.ref_a!r}.")
            print("Run `rigmeter snapshot` first, or `rigmeter backfill --refs 'v*'`.")
            sys.exit(1)
        if not path_b:
            print(f"No snapshot found matching date prefix {args.ref_b!r}.")
            sys.exit(1)
        snap_a = load_snapshot_json(path_a)
        snap_b = load_snapshot_json(path_b)
        print(f"Loaded snapshots: {path_a.name} → {path_b.name}", file=sys.stderr)
        dt_a = git_commit_datetime(repo, snap_a.head_sha)
        dt_b = git_commit_datetime(repo, snap_b.head_sha)
        days_elapsed = (dt_b - dt_a).total_seconds() / 86400.0 if (dt_a and dt_b) else None
        print_growth(snap_a, snap_b, days_elapsed)
        return

    # Live-walk branch: build snapshots from git on demand
    if args.since:
        sha_a = git_run(repo, "rev-list", "-1", f"--before={args.since}", "HEAD").strip()
        if not sha_a:
            print(f"Could not resolve --since {args.since!r} to a commit.")
            sys.exit(1)
        ref_a = sha_a
        ref_b = "HEAD"
    elif args.ref_a and args.ref_b:
        ref_a, ref_b = args.ref_a, args.ref_b
    elif args.ref_a:
        ref_a, ref_b = args.ref_a, "HEAD"
    else:
        print("growth: need a ref. See: rigmeter growth --help")
        sys.exit(1)

    no_fetch = getattr(args, "no_fetch", False)
    print(f"Building snapshot at {ref_a} (this can take a few seconds) ...", file=sys.stderr)
    snap_a = build_snapshot_at_ref(repo, ref_a)
    if ref_b == "HEAD":
        print(f"Building snapshot at HEAD (live tree) ...", file=sys.stderr)
        snap_b = build_snapshot_live(repo, no_fetch=no_fetch)
    else:
        print(f"Building snapshot at {ref_b} ...", file=sys.stderr)
        snap_b = build_snapshot_at_ref(repo, ref_b)

    dt_a = git_commit_datetime(repo, snap_a.head_sha)
    dt_b = git_commit_datetime(repo, snap_b.head_sha)
    days_elapsed = (dt_b - dt_a).total_seconds() / 86400.0 if (dt_a and dt_b) else None

    print_growth(snap_a, snap_b, days_elapsed)

    if getattr(args, "explain", False):
        print()
        print("Note: --explain on growth shows the AFTER snapshot's accounting.")
        print_explain(snap_b)


def cmd_snapshot(args):
    """Write a JSON snapshot to NAS (with %LOCALAPPDATA% fallback).

    With --sync, instead of writing a new snapshot, walk local-only files
    and copy any missing ones up to NAS. Useful after NAS-down windows.
    """
    if args.sync:
        copied, skipped = sync_local_snapshots_to_nas()
        if copied == 0 and skipped == 0:
            print("No local snapshots to sync (or NAS unreachable).")
        else:
            print(f"Synced {copied} snapshot(s) to NAS. {skipped} already present.")
        return

    repo = REPO_ROOT
    no_fetch = getattr(args, "no_fetch", False)
    print(f"Building live-tree snapshot ...", file=sys.stderr)
    snap = build_snapshot_live(repo, no_fetch=no_fetch)
    path, used_nas = write_snapshot_json(snap, repo)
    where = "NAS" if used_nas else "local fallback (%LOCALAPPDATA%)"
    print(f"Snapshot written to {where}:")
    print(f"  {path}")
    if not used_nas:
        print()
        print("NAS was unreachable. Run `rigmeter snapshot --sync` later to copy")
        print("local-only snapshots up to NAS once Tailscale is back.")


def cmd_backfill(args):
    """Walk matching git refs and write a JSON snapshot for each.

    Default (and only meaningful invocation tonight): backfill --refs 'v*'
    walks all release tags. Long-running: each ref builds a full snapshot
    (~1-2s per tag with git cat-file --batch).
    """
    repo = REPO_ROOT
    pattern = args.refs or "v*"
    out = git_run(repo, "tag", "-l", pattern, "--sort=v:refname")
    refs = [t.strip() for t in out.splitlines() if t.strip()]
    if not refs:
        print(f"No refs matched pattern {pattern!r}.")
        return

    print(f"Backfilling {len(refs)} snapshot(s) for pattern {pattern!r} ...", file=sys.stderr)
    written = 0
    used_nas = 0
    for ref in refs:
        try:
            print(f"  [{written + 1}/{len(refs)}] {ref} ...", file=sys.stderr)
            snap = build_snapshot_at_ref(repo, ref)
            path, on_nas = write_snapshot_json(snap, repo)
            if on_nas:
                used_nas += 1
            written += 1
        except Exception as e:
            print(f"  FAILED: {ref}: {e}", file=sys.stderr)
    print(f"\nBackfill complete: {written}/{len(refs)} snapshot(s) written.")
    print(f"  NAS:   {used_nas}")
    print(f"  Local: {written - used_nas}")
    if written - used_nas > 0:
        print(f"  (Run `rigmeter snapshot --sync` to copy local-only snapshots to NAS.)")


def cmd_installers(args):
    """Parse the NAS historical/<version>/installers/ tree for installer file
    sizes and report a version-by-version size trend. Builds-bloat watchdog.
    Note: only release-built versions land in installers/ — debug-only versions
    are skipped (most version dirs are debug-only)."""
    nas_root = Path(r"\\nas.macaw-jazz.ts.net\jjflex\historical")
    if not nas_root.exists():
        print(f"NAS path not reachable: {nas_root}")
        print("(Tailscale up?)")
        return
    print(f"Rigmeter — installer size trend")
    print(f"  Source: {nas_root}")
    print()

    def _version_key(name: str) -> Tuple[int, ...]:
        """Sort key for version dirnames like '4.1.16.1', '4.1.16.10', '4.1.16.101'."""
        parts = []
        for p in name.split("."):
            try:
                parts.append(int(p))
            except ValueError:
                parts.append(0)
        return tuple(parts)

    rows = []
    for version_dir in sorted(nas_root.iterdir(), key=lambda p: _version_key(p.name)):
        if not version_dir.is_dir():
            continue
        installers_dir = version_dir / "installers"
        if not installers_dir.exists():
            continue
        # Pick first matching installer per arch (filenames have timestamp suffix)
        x64_size = 0
        x86_size = 0
        for f in installers_dir.glob("Setup JJFlex_*_x64.exe"):
            x64_size = f.stat().st_size
            break
        for f in installers_dir.glob("Setup JJFlex_*_x86.exe"):
            x86_size = f.stat().st_size
            break
        if x64_size or x86_size:
            x64_mb = x64_size / (1024 * 1024)
            x86_mb = x86_size / (1024 * 1024)
            rows.append((
                version_dir.name,
                f"{x64_mb:.2f} MB" if x64_mb else "—",
                f"{x86_mb:.2f} MB" if x86_mb else "—",
            ))
    if not rows:
        print("(no installer files found in historical tree — only debug builds present)")
        print(f"Looked under: {nas_root}/<version>/installers/")
        return
    print_records(rows, ["Version", "x64 size", "x86 size"])
    print()
    if len(rows) >= 2:
        # Trend: first vs last
        def _parse_mb(s: str) -> float:
            return float(s.replace(" MB", "")) if s != "—" else 0.0
        first = rows[0]
        last = rows[-1]
        first_x64 = _parse_mb(first[1])
        last_x64 = _parse_mb(last[1])
        if first_x64 > 0 and last_x64 > 0:
            delta = last_x64 - first_x64
            pct = (delta / first_x64) * 100
            print(f"  x64 trend: {first[0]} → {last[0]}: "
                  f"{first[1]} → {last[1]}  ({delta:+.2f} MB, {fmt_pct(pct)})")


# --- v1.2 subcommand: authors (git blame attribution) ---------------------
#
# Per the project memory: "Jim never saw what Noel built; this number means
# something." Output is respectful and statistical, not value-laden.

# Author-name aliases: collapse multiple emails for the same human into a
# canonical name. Discovered emails populated lazily from `git log`; users
# can extend via the RIGMETER_AUTHOR_ALIAS env var (format: "old=New Name;old2=New").
AUTHOR_ALIASES_DEFAULT = {
    # JJ Flex bot identity used by tooling commits — collapse to Noel.
    "jj flexbot": "Noel Romey (K5NER)",
    "noel": "Noel Romey (K5NER)",
    "noel romey": "Noel Romey (K5NER)",
    "nromey": "Noel Romey (K5NER)",
    "k5ner": "Noel Romey (K5NER)",
    "kevin shaffer": "Jim Shaffer",
    "jim shaffer": "Jim Shaffer",
    "kevinsshaffer": "Jim Shaffer",
}


def normalize_author(raw_name: str) -> str:
    """Collapse alias variants to a canonical author name."""
    if not raw_name:
        return "(unknown)"
    key = raw_name.strip().lower()
    return AUTHOR_ALIASES_DEFAULT.get(key, raw_name.strip())


def git_blame_authors(repo_root: Path, relpath: str) -> Dict[str, int]:
    """Return {author: line_count} for one file via git blame --line-porcelain.
    Empty dict on any failure (e.g. binary file, deleted file)."""
    out = git_run(repo_root, "blame", "--line-porcelain", "--", relpath)
    if not out:
        return {}
    counts: Dict[str, int] = {}
    for line in out.splitlines():
        if line.startswith("author "):
            name = normalize_author(line[len("author "):])
            counts[name] = counts.get(name, 0) + 1
    return counts


def cmd_authors(args):
    """Author attribution — % of current code lines per author, per project + repo-wide.

    Emotional context: Jim never saw what Noel built. This number is not for
    crowing; it's for Noel to look at quietly when the math finally lands.
    """
    repo = REPO_ROOT
    print("Rigmeter — author attribution (via git blame)")
    print()
    print("  Walking authored code files (this is slow — git blame is per-file)…", file=sys.stderr)

    by_project: Dict[str, Dict[str, int]] = {}
    overall: Dict[str, int] = {}
    walked = 0
    for relpath in enumerate_tracked_files(repo):
        rec = classify_path(relpath)
        if rec.skip_reason or rec.is_vendor or rec.category != "code":
            continue
        counts = git_blame_authors(repo, relpath)
        if not counts:
            continue
        walked += 1
        proj_bucket = by_project.setdefault(rec.project, {})
        for author, n in counts.items():
            overall[author] = overall.get(author, 0) + n
            proj_bucket[author] = proj_bucket.get(author, 0) + n
        if walked % 50 == 0:
            log.debug("authors: %d files walked", walked)

    if not overall:
        print("  (no authored code files found, or git blame unavailable)")
        return

    total = sum(overall.values())
    print(f"  Files walked:   {fmt_int(walked)}")
    print(f"  Lines analyzed: {fmt_int(total)}")

    print_section("Repo-wide attribution")
    rows = []
    for author, n in sorted(overall.items(), key=lambda kv: -kv[1]):
        pct = (n / total * 100) if total else 0
        rows.append((author, fmt_int(n), f"{pct:.1f}%"))
    print_records(rows, ["Author", "Lines", "Share"])

    print_section("Per-project attribution (authored code only)")
    for proj_name in sorted(by_project.keys()):
        proj_counts = by_project[proj_name]
        proj_total = sum(proj_counts.values())
        if proj_total == 0:
            continue
        rows = []
        for author, n in sorted(proj_counts.items(), key=lambda kv: -kv[1]):
            pct = (n / proj_total * 100) if proj_total else 0
            rows.append((author, fmt_int(n), f"{pct:.1f}%"))
        print(f"\n  [{proj_name}]  ({fmt_int(proj_total)} lines)")
        print_records(rows, ["Author", "Lines", "Share"])


# --- v1.2 subcommand: sprint ---------------------------------------------

def git_branches_matching(repo_root: Path, pattern: str) -> List[str]:
    """Return short branch names matching pattern (across local + remote/origin)."""
    out = git_run(repo_root, "for-each-ref", "--format=%(refname:short)",
                  f"refs/heads/{pattern}", f"refs/remotes/origin/{pattern}")
    names = [b.strip() for b in out.splitlines() if b.strip()]
    # De-dup local vs origin variants
    return sorted(set(names))


def git_merge_base(repo_root: Path, ref_a: str, ref_b: str) -> str:
    out = git_run(repo_root, "merge-base", ref_a, ref_b).strip()
    return out


def cmd_sprint(args):
    """Stats for sprint <N>, auto-detected by branch-name pattern.

    Looks for sprint<N>/track-* branches (per CLAUDE.md sprint convention).
    Falls back to commit-message scanning if no live branches survive.
    """
    repo = REPO_ROOT
    n = args.number
    pattern = f"sprint{n}/*"
    print(f"Rigmeter — sprint {n} stats")
    print()

    branches = git_branches_matching(repo, pattern)
    if branches:
        print(f"  Found {len(branches)} matching branch(es):")
        for b in branches:
            print(f"    {b}")
        print()

        # Compute aggregate growth: from the merge-base of all branches with main,
        # to HEAD on the latest sprint branch.
        bases = []
        for b in branches:
            mb = git_merge_base(repo, b, "main")
            if mb:
                bases.append(mb)
        # Earliest merge-base = sprint start point
        if bases:
            base_sha = sorted(bases, key=lambda s: git_commit_datetime(repo, s) or datetime.min)[0]
        else:
            base_sha = ""

        # Latest tip = newest commit across the matching branches
        latest_branch = sorted(
            branches,
            key=lambda b: git_commit_datetime(repo, b) or datetime.min,
        )[-1]
        tip_sha = git_resolve_ref(repo, latest_branch)

        if base_sha and tip_sha:
            ins, dels, fch = git_diff_shortstat(repo, base_sha, tip_sha)
            print(f"  Span:           {base_sha[:12]} → {tip_sha[:12]}")
            print(f"  Files changed:  {fmt_int(fch)}")
            print(f"  Insertions:     {fmt_int(ins)}")
            print(f"  Deletions:      {fmt_int(dels)}")
            print(f"  Net line change:{(ins - dels):+,}")
        else:
            print("  (could not resolve sprint span via branch+merge-base)")

        print_section(f"Per-track activity")
        rows = []
        for b in branches:
            tip = git_resolve_ref(repo, b)
            mb = git_merge_base(repo, b, "main")
            if not (tip and mb):
                continue
            ins, dels, fch = git_diff_shortstat(repo, mb, tip)
            commits = git_run(repo, "rev-list", "--count", f"{mb}..{tip}").strip() or "0"
            rows.append((
                b,
                commits,
                fmt_int(fch),
                f"+{fmt_int(ins)}",
                f"-{fmt_int(dels)}",
            ))
        print_records(rows, ["Branch", "commits", "files", "insertions", "deletions"])
    else:
        # Fallback: search commit messages
        print(f"  No live branches matching {pattern!r}.")
        print("  Scanning commit messages for 'Sprint {n}' references…".format(n=n))
        out = git_run(repo, "log", "--format=%H %s", "--all")
        matches = []
        sprint_re = re.compile(rf"\bSprint\s+{n}\b", re.IGNORECASE)
        for line in out.splitlines():
            if not line.strip():
                continue
            sha, _, subject = line.partition(" ")
            if sprint_re.search(subject):
                matches.append((sha, subject))
        print(f"  Found {len(matches)} commit(s) referencing Sprint {n}.")
        if matches:
            for sha, subject in matches[:20]:
                print(f"    {sha[:12]}  {subject[:80]}")
            if len(matches) > 20:
                print(f"    … ({len(matches) - 20} more)")

    # If a per-sprint test matrix exists, point to it
    matrix_paths = [
        REPO_ROOT / "docs" / "planning" / "agile" / f"sprint{n}-test-matrix.md",
        REPO_ROOT / "docs" / "planning" / "agile" / f"4.1.{n}-test-matrix.md",
    ]
    for mp in matrix_paths:
        if mp.exists():
            print()
            print(f"  Test matrix: {mp.relative_to(REPO_ROOT)}")
            break


# --- v1.2 subcommand: debt (TODO/FIXME/HACK counter) ----------------------

DEBT_MARKERS = ("TODO", "FIXME", "HACK", "XXX")


def cmd_debt(args):
    """Count technical-debt markers across authored code+docs.

    Tonal note: rising counts aren't necessarily bad (means you're surfacing
    more); falling counts aren't necessarily good (might mean deletion without
    fix). Reports counts and deltas without value-judgment language.
    """
    repo = REPO_ROOT
    print("Rigmeter — technical-debt markers")
    print()

    counts: Dict[str, int] = {m: 0 for m in DEBT_MARKERS}
    by_marker_examples: Dict[str, List[Tuple[str, int, str]]] = {m: [] for m in DEBT_MARKERS}
    by_project: Dict[str, Dict[str, int]] = {}

    # Scan authored code, docs, and text_data files (skip vendor + binaries).
    files_scanned = 0
    for relpath in enumerate_tracked_files(repo):
        rec = classify_path(relpath)
        if rec.skip_reason or rec.is_vendor:
            continue
        if rec.category not in ("code", "docs", "text_data"):
            continue
        text = read_file_text(repo / relpath)
        if text is None:
            continue
        files_scanned += 1
        for lineno, line in enumerate(text.splitlines(), start=1):
            for m in DEBT_MARKERS:
                if m in line:
                    counts[m] += 1
                    proj_bucket = by_project.setdefault(rec.project, {m: 0 for m in DEBT_MARKERS})
                    proj_bucket[m] = proj_bucket.get(m, 0) + 1
                    if len(by_marker_examples[m]) < args.examples:
                        # Trim line to a sane width
                        snippet = line.strip()[:120]
                        by_marker_examples[m].append((relpath, lineno, snippet))

    print(f"  Files scanned:  {fmt_int(files_scanned)}")
    print()
    rows = [(m, fmt_int(counts[m])) for m in DEBT_MARKERS]
    rows.append(("total", fmt_int(sum(counts.values()))))
    print_records(rows, ["Marker", "Count"])

    if any(by_project.values()):
        print_section("Per-project breakdown")
        proj_rows = []
        for proj in sorted(by_project.keys()):
            proj_counts = by_project[proj]
            proj_rows.append((
                proj,
                fmt_int(proj_counts.get("TODO", 0)),
                fmt_int(proj_counts.get("FIXME", 0)),
                fmt_int(proj_counts.get("HACK", 0)),
                fmt_int(proj_counts.get("XXX", 0)),
            ))
        print_records(proj_rows, ["Project", "TODO", "FIXME", "HACK", "XXX"])

    if args.examples > 0:
        print_section(f"Examples (up to {args.examples} per marker)")
        for m in DEBT_MARKERS:
            ex = by_marker_examples[m]
            if not ex:
                continue
            print(f"\n  [{m}]")
            for relpath, lineno, snippet in ex:
                print(f"    {relpath}:{lineno}  {snippet}")


# --- v1.2 subcommand: forgotten (dead-code candidates) --------------------

def cmd_forgotten(args):
    """List files not modified in N days. Surfaces dead-code candidates.

    Uses git log to find each file's most-recent touch date. Files older
    than the threshold are reported, sorted oldest-first.
    """
    repo = REPO_ROOT
    threshold_days = args.days
    excludes = list(args.exclude or [])

    print(f"Rigmeter — files not touched in {threshold_days}+ days")
    if excludes:
        print(f"  Excluding prefixes: {', '.join(excludes)}")
    print()

    cutoff = datetime.now().astimezone() - timedelta(days=threshold_days)
    forgotten: List[Tuple[str, str, int]] = []  # (path, last-touch-date, days-ago)

    for relpath in enumerate_tracked_files(repo):
        if any(relpath.startswith(p) for p in excludes):
            continue
        rec = classify_path(relpath)
        if rec.skip_reason:
            continue
        # Get last commit touch
        out = git_run(repo, "log", "-1", "--format=%aI", "--", relpath).strip()
        if not out:
            continue
        try:
            last_dt = datetime.fromisoformat(out)
        except Exception:
            continue
        if last_dt < cutoff:
            days_ago = (datetime.now().astimezone() - last_dt).days
            forgotten.append((relpath, last_dt.date().isoformat(), days_ago))

    if not forgotten:
        print(f"  Every tracked file has been touched within the last {threshold_days} days.")
        return

    forgotten.sort(key=lambda t: t[2], reverse=True)  # oldest first
    print(f"  {fmt_int(len(forgotten))} file(s) not touched in {threshold_days}+ days:")
    print()
    rows = [(path, last_date, f"{days} days") for path, last_date, days in forgotten]
    print_records(rows, ["Path", "last touch", "ago"])

    # Aggregate per-project
    by_proj: Dict[str, int] = {}
    for path, _, _ in forgotten:
        proj = assign_project(path)
        by_proj[proj] = by_proj.get(proj, 0) + 1
    if by_proj:
        print_section("By project")
        rows = [(p, fmt_int(c)) for p, c in sorted(by_proj.items(), key=lambda kv: -kv[1])]
        print_records(rows, ["Project", "forgotten files"])


# --- v1.2 ASCII sparkline (for `all` trend display) -----------------------

SPARKLINE_BARS = "▁▂▃▄▅▆▇█"


def render_sparkline(values: List[float]) -> str:
    """Render a Unicode sparkline from a list of numbers. Empty if <2 values."""
    if not values or len(values) < 2:
        return ""
    lo = min(values)
    hi = max(values)
    if hi == lo:
        # All equal — flat line
        return SPARKLINE_BARS[len(SPARKLINE_BARS) // 2] * len(values)
    span = hi - lo
    out = []
    for v in values:
        idx = int((v - lo) / span * (len(SPARKLINE_BARS) - 1))
        idx = max(0, min(len(SPARKLINE_BARS) - 1, idx))
        out.append(SPARKLINE_BARS[idx])
    return "".join(out)


def cmd_trend_block(snap: Snapshot) -> None:
    """Print a tiny code-base-trend section based on accumulated NAS snapshots.

    Renders for `all` when ≥5 snapshots exist. Quiet otherwise (no false
    starts on a fresh repo). Sparklines pair with a numeric bullet list so
    screen-reader users get the trend in linear form.
    """
    snaps = list_snapshots()
    if len(snaps) < 5:
        return
    # Sort by filename (commit-date prefix sorts naturally)
    paths = sorted(snaps.values(), key=lambda p: p.name)
    rows: List[Tuple[str, int, int, int, int]] = []  # (date, code, docs, lines, comments)
    for p in paths:
        try:
            s = load_snapshot_json(p)
            code_lines = s.authored.for_category("code").lines
            docs_lines = s.authored.for_category("docs").lines
            comm_lines = s.authored.for_category("code").comments
            total_lines = sum(s.authored.for_category(c).lines for c in CATEGORY_ORDER)
            # Date prefix from filename (YYYY-MM-DD-<sha>.json)
            date_str = p.name[:10]
            rows.append((date_str, code_lines, docs_lines, total_lines, comm_lines))
        except Exception as e:
            log.debug("trend: failed to load %s: %s", p, e)
            continue

    if len(rows) < 2:
        return

    print_section(f"Trend (across {len(rows)} historical snapshots)")
    code_spark = render_sparkline([r[1] for r in rows])
    docs_spark = render_sparkline([r[2] for r in rows])
    total_spark = render_sparkline([r[3] for r in rows])
    print(f"  Code base lines:  {code_spark}   ({fmt_int(rows[0][1])} → {fmt_int(rows[-1][1])})")
    print(f"  Doc base lines:   {docs_spark}   ({fmt_int(rows[0][2])} → {fmt_int(rows[-1][2])})")
    print(f"  Total lines:      {total_spark}   ({fmt_int(rows[0][3])} → {fmt_int(rows[-1][3])})")
    if any(r[4] for r in rows):
        comm_spark = render_sparkline([r[4] for r in rows])
        print(f"  Comment lines:    {comm_spark}   ({fmt_int(rows[0][4])} → {fmt_int(rows[-1][4])})")
    # Linear list for screen readers — the sparkline above is visual sugar.
    print()
    print("  (Each bar in the sparkline is one snapshot; numeric values per snapshot:)")
    bullet_rows = [(r[0], fmt_int(r[1]), fmt_int(r[2]), fmt_int(r[4])) for r in rows]
    print_records(bullet_rows, ["Snapshot date", "code lines", "doc lines", "comment lines"])


# --- v1.2 docs-to-code ratio ---------------------------------------------

def docs_to_code_ratio(snap: Snapshot) -> float:
    """Authored docs lines / authored code lines. 0.0 when no code."""
    code = snap.authored.for_category("code").lines
    docs = snap.authored.for_category("docs").lines
    return docs / code if code else 0.0


# --- v1.2 interactive menu wizard -----------------------------------------

INTERACTIVE_MENU_ITEMS = [
    ("all",        "Full repo statistics", None),
    ("today",      "Git activity since midnight", None),
    ("week",       "Git activity in the last 7 days", None),
    ("month",      "Git activity in the last 30 days", None),
    ("year",       "Git activity in the last 365 days", None),
    ("start",      "Cumulative since first commit", None),
    ("releases",   "List release tags", None),
    ("release",    "Stats for one release",        ["tag (e.g. v4.1.16)"]),
    ("growth",     "Growth between two refs",      ["ref-a", "ref-b (or blank for HEAD)"]),
    ("snapshot",   "Write JSON snapshot to NAS",   None),
    ("backfill",   "Walk release tags and write snapshot for each", None),
    ("installers", "Installer size trend",         None),
    ("fun",        "Playful comparisons",          None),
    ("authors",    "Author attribution via git blame", None),
    ("sprint",     "Sprint stats",                 ["sprint number (e.g. 28)"]),
    ("debt",       "TODO / FIXME / HACK counter",  None),
    ("forgotten",  "Dead-code candidates",         ["days threshold (default 90)"]),
    ("compare",    "Compare to another repo",      ["other repo path"]),
]


def _interactive_print_menu():
    print()
    print("Rigmeter — interactive menu")
    print()
    for i, (cmd, descr, _) in enumerate(INTERACTIVE_MENU_ITEMS, start=1):
        print(f"  {i:2d}. {cmd:11s}  {descr}")
    print(f"   0. exit")
    print()
    print("  Ctrl+C to exit · `rigmeter --help` from the shell shows the full command list.")
    print()


def run_interactive_menu(args) -> int:
    """Stdlib-only menu wizard. Calls the same handlers as direct CLI use.

    Single screen with numbered choices. Each choice that needs arguments
    prompts for them inline. Loop continues until the user picks 0, types
    'exit'/'quit', or presses Ctrl+C / Ctrl+D.
    """
    print("Rigmeter interactive mode (basic). Type a number, 'exit', or press Ctrl+C to quit.")
    handlers = {
        "all":        cmd_all,        "today":      cmd_today,
        "week":       cmd_week,       "month":      cmd_month,
        "year":       cmd_year,       "start":      cmd_start,
        "releases":   cmd_releases,   "release":    cmd_release,
        "compare":    cmd_compare,    "fun":        cmd_fun,
        "growth":     cmd_growth,     "snapshot":   cmd_snapshot,
        "backfill":   cmd_backfill,   "installers": cmd_installers,
        "authors":    cmd_authors,    "sprint":     cmd_sprint,
        "debt":       cmd_debt,       "forgotten":  cmd_forgotten,
    }
    while True:
        _interactive_print_menu()
        try:
            choice = input("Choice: ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return 0
        if choice in ("0", "exit", "quit", "q"):
            return 0
        if not choice.isdigit():
            print(f"  (didn't understand {choice!r}; try a number 0-{len(INTERACTIVE_MENU_ITEMS)})")
            continue
        idx = int(choice)
        if idx == 0:
            return 0
        if idx < 1 or idx > len(INTERACTIVE_MENU_ITEMS):
            print(f"  (out of range: type 0-{len(INTERACTIVE_MENU_ITEMS)})")
            continue
        cmd_name, _descr, prompts = INTERACTIVE_MENU_ITEMS[idx - 1]
        # Build a synthetic args namespace reusing the original common flags.
        sub_args = argparse.Namespace(**vars(args))
        sub_args.cmd = cmd_name
        # Per-subcommand argument capture
        if cmd_name == "release":
            sub_args.tag = input("  Tag (e.g. v4.1.16): ").strip() or "HEAD"
            sub_args.growth = False
            sub_args.explain = False
        elif cmd_name == "growth":
            sub_args.ref_a = input("  Earlier ref (tag/SHA/date): ").strip() or None
            ref_b_in = input("  Later ref (blank for HEAD): ").strip()
            sub_args.ref_b = ref_b_in or None
            sub_args.since = None
            sub_args.use_snapshots = False
            sub_args.explain = False
        elif cmd_name == "sprint":
            n_in = input("  Sprint number: ").strip()
            try:
                sub_args.number = int(n_in)
            except ValueError:
                print("  (need an integer)")
                continue
        elif cmd_name == "forgotten":
            d_in = input("  Days threshold (default 90): ").strip() or "90"
            try:
                sub_args.days = int(d_in)
            except ValueError:
                sub_args.days = 90
            sub_args.exclude = []
        elif cmd_name == "compare":
            sub_args.other_repo = input("  Other repo path: ").strip()
            if not sub_args.other_repo:
                print("  (need a path)")
                continue
            sub_args.explain = False
        elif cmd_name == "debt":
            sub_args.examples = 5
        elif cmd_name == "authors":
            sub_args.top = 10
        elif cmd_name == "backfill":
            sub_args.refs = "v*"
        elif cmd_name == "snapshot":
            sub_args.sync = False
        elif cmd_name == "all":
            sub_args.explain = False
        try:
            handlers[cmd_name](sub_args)
        except KeyboardInterrupt:
            print("\n  (interrupted; back to menu)")
            continue
        except Exception as e:
            print(f"  (subcommand failed: {e})")
            log.exception("interactive subcommand %s failed", cmd_name)
        print()
        try:
            input("  -- press Enter to return to the menu, Ctrl+C to exit -- ")
        except (EOFError, KeyboardInterrupt):
            print()
            return 0


# --- CLI entry -------------------------------------------------------------

def main(argv=None):
    # Force stdout AND stderr to UTF-8 so em-dashes, ≈, ∞, ellipsis, and other
    # Unicode glyphs don't crash on Windows consoles defaulting to cp1252.
    # stderr matters because growth/snapshot/backfill emit progress messages there.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    # Common flags accepted by every subcommand. argparse doesn't have a
    # native "global option" notion with subparsers, so we attach a parent
    # parser whose flags are inherited by every subparser via parents=[].
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--verbose", "-v", action="store_true",
                        help="Verbose debug logging to stderr (subcommand decisions, "
                             "tokei invocations, snapshot writes, etc.)")
    common.add_argument("--no-fetch", dest="no_fetch", action="store_true",
                        help="Skip network downloads (tokei auto-fetch). When tokei is "
                             "missing and --no-fetch is set, code-vs-comment splits are "
                             "marked '(approximate)' or omitted.")
    common.add_argument("--format", dest="output_format", default="text",
                        choices=("text", "json", "markdown", "csv"),
                        help="Output format. Default 'text' is the screen-reader-friendly "
                             "bullet output. 'markdown' and 'csv' use real tables (opt-in). "
                             "'json' emits the underlying data structure.")

    parser = argparse.ArgumentParser(
        prog="rigmeter",
        description=(
            "JJ Flexible Radio Access source statistics tool. v1.2 layers tokei "
            "code-vs-comment splits, author attribution, sprint/debt/forgotten "
            "subcommands, and an interactive menu wizard onto the v1.1 line "
            "counter. Run with no arguments to print this help."
        ),
        parents=[common],
    )
    parser.add_argument("--interactive", action="store_true",
                        help="Launch the interactive menu wizard (no-args still prints "
                             "this help instead, so scripts and pipelines stay safe).")
    sub = parser.add_subparsers(dest="cmd")

    p_all = sub.add_parser("all", parents=[common],
                           help="Full repository statistics right now")
    p_all.add_argument("--explain", action="store_true",
                       help="Show file accounting (which files were included/excluded and why)")

    sub.add_parser("today",    parents=[common], help="Git activity in the last 1 day")
    sub.add_parser("week",     parents=[common], help="Git activity in the last 7 days")
    sub.add_parser("month",    parents=[common], help="Git activity in the last 30 days")
    sub.add_parser("year",     parents=[common], help="Git activity in the last 365 days")
    sub.add_parser("start",    parents=[common], help="Cumulative stats since the first commit")
    sub.add_parser("releases", parents=[common], help="List all release tags")
    sub.add_parser("fun",      parents=[common], help="Playful comparisons (authored only)")

    p_release = sub.add_parser("release", parents=[common],
                               help="Stats for a release vs prior tag")
    p_release.add_argument("tag", help="Release tag to report on, e.g. v4.1.16")
    p_release.add_argument("--growth", action="store_true",
                           help="Also build full snapshots and show per-category growth %s")
    p_release.add_argument("--explain", action="store_true",
                           help="Show file accounting (only with --growth)")

    p_compare = sub.add_parser("compare", parents=[common],
                               help="Side-by-side totals vs another local repo")
    p_compare.add_argument("other_repo", help="Path to another local repo (e.g. Jim's JJ Radio)")
    p_compare.add_argument("--explain", action="store_true",
                           help="Show file accounting for THIS repo")

    p_growth = sub.add_parser(
        "growth", parents=[common],
        help="Growth between two refs (code base + doc base, separately, with per-day rate)",
    )
    p_growth.add_argument("ref_a", nargs="?",
                          help="Earlier ref (tag/SHA/date) or date prefix with --use-snapshots")
    p_growth.add_argument("ref_b", nargs="?",
                          help="Later ref. Defaults to HEAD if omitted.")
    p_growth.add_argument("--since",
                          help="Date string (e.g. 2026-01-01). Resolves to nearest prior commit.")
    p_growth.add_argument("--use-snapshots", dest="use_snapshots", action="store_true",
                          help="Read JSON snapshots (NAS or local) instead of walking git. "
                               "ref-a and ref-b become DATE PREFIXES (e.g. '2026-03-08').")
    p_growth.add_argument("--explain", action="store_true",
                          help="Show file accounting for the AFTER snapshot")

    p_snapshot = sub.add_parser(
        "snapshot", parents=[common],
        help="Write a JSON snapshot to NAS (with %%LOCALAPPDATA%% fallback)",
    )
    p_snapshot.add_argument("--sync", action="store_true",
                            help="Copy local-only snapshots up to NAS instead of writing a new one")

    p_backfill = sub.add_parser(
        "backfill", parents=[common],
        help="Walk matching git refs, write a JSON snapshot for each (overnight job)",
    )
    p_backfill.add_argument("--refs", default="v*",
                            help="Tag glob to backfill. Default: 'v*' (all release tags).")

    sub.add_parser("installers", parents=[common],
                   help="Installer size trend across versions (parses NAS historical/ tree)")

    # --- v1.2 new subcommands ---
    p_authors = sub.add_parser(
        "authors", parents=[common],
        help="Author attribution via git blame: who wrote each line",
    )
    p_authors.add_argument("--top", type=int, default=10,
                           help="Show top-N files per author (default 10). 0 = no per-file detail.")

    p_sprint = sub.add_parser(
        "sprint", parents=[common],
        help="Stats for a sprint, auto-detected by branch-name pattern (sprint<N>/...)",
    )
    p_sprint.add_argument("number", type=int,
                          help="Sprint number, e.g. 28 for sprint28/track-a branches")

    p_debt = sub.add_parser(
        "debt", parents=[common],
        help="Count TODO / FIXME / HACK / XXX markers across the repo",
    )
    p_debt.add_argument("--examples", type=int, default=5,
                        help="Examples per marker type (default 5; 0 = none)")

    p_forgotten = sub.add_parser(
        "forgotten", parents=[common],
        help="Files not modified in N days (dead-code candidates)",
    )
    p_forgotten.add_argument("--days", type=int, default=90,
                             help="Threshold in days (default 90). Files with no commit "
                                  "touch in this window are reported.")
    p_forgotten.add_argument("--exclude", action="append", default=[],
                             help="Path prefix to exclude (repeatable). Useful to skip "
                                  "vendor/ if you only care about authored code.")

    # No-args invocation: print help and exit cleanly. This keeps scripts,
    # cron jobs, subprocess.run callers, and CI pipelines safe — interactive
    # mode is opt-in via --interactive, never a default that could block.
    if argv is None and len(sys.argv) <= 1:
        parser.print_help()
        return 0
    if argv is not None and len(argv) == 0:
        parser.print_help()
        return 0

    args = parser.parse_args(argv)

    # Configure logging based on --verbose. Use a parent-level handler so
    # subcommand-supplied verbose flags work consistently. Always write to
    # stderr so stdout stays clean for piping.
    log_level = logging.DEBUG if getattr(args, "verbose", False) else logging.WARNING
    logging.basicConfig(
        level=log_level,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        stream=sys.stderr,
    )
    log.debug("rigmeter v%s starting; args=%s", RIGMETER_VERSION, vars(args))

    # --interactive (no subcommand): launch the menu wizard. Argparse with
    # an optional 'cmd' subparser may parse this with cmd=None.
    if getattr(args, "interactive", False) and not getattr(args, "cmd", None):
        return run_interactive_menu(args)

    if not getattr(args, "cmd", None):
        parser.print_help()
        return 0

    handlers = {
        "all":        cmd_all,
        "today":      cmd_today,
        "week":       cmd_week,
        "month":      cmd_month,
        "year":       cmd_year,
        "start":      cmd_start,
        "releases":   cmd_releases,
        "release":    cmd_release,
        "compare":    cmd_compare,
        "fun":        cmd_fun,
        "growth":     cmd_growth,
        "snapshot":   cmd_snapshot,
        "backfill":   cmd_backfill,
        "installers": cmd_installers,
        # v1.2:
        "authors":    cmd_authors,
        "sprint":     cmd_sprint,
        "debt":       cmd_debt,
        "forgotten":  cmd_forgotten,
    }
    try:
        handlers[args.cmd](args)
    except KeyboardInterrupt:
        # Catch Ctrl+C cleanly so users (especially screen-reader users)
        # don't get a wall of red traceback.
        print("\nInterrupted.", file=sys.stderr)
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main() or 0)
