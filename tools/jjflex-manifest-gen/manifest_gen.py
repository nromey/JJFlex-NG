"""Sprint 29 Track N — server-side manifest generation tooling.

Walks a published JJF directory, computes per-file sha256 + LZMA-compressed
artifact, uploads the compressed blobs to Cloudflare R2 under content-
addressable paths, writes a per-version file-manifest, and updates the
top-level jjflex-app-manifest.json channel pointer.

Track D (client-side updater) consumes the manifests this tool produces.

Run `python manifest_gen.py generate --help` for flags.
"""

from __future__ import annotations

import fnmatch
import hashlib
import json
import lzma
import os
import sys
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Iterable, Optional

import click

# Compression format constant. Track D will decompress with SharpCompress's
# XZStream — `.xz` is the framed format with header + checksum that maps
# cleanly to that. FORMAT_ALONE (legacy .lzma) is older and lacks the
# integrity check; FORMAT_RAW has no header and is harder to interop.
LZMA_FORMAT = lzma.FORMAT_XZ
LZMA_EXTENSION = ".xz"

# Manifest schema version. Track D parses `schema_version`; bump on
# breaking changes (renaming/removing fields). Adding optional fields
# does NOT bump.
SCHEMA_VERSION = 1

# Files we never ship. Lowercase patterns matched case-insensitively
# against POSIX-style relative paths.
DEFAULT_EXCLUDES = (
    "*.pdb",
    "*.lastcodeanalysissucceeded",
    ".env",
    ".env.*",
    "*.user",
    "thumbs.db",
    ".ds_store",
    "desktop.ini",
)

# Cache headers
CACHE_IMMUTABLE = "public, max-age=31536000, immutable"
CACHE_FILE_MANIFEST = "public, max-age=300"
CACHE_APP_MANIFEST = "no-cache"

# R2 key prefixes
KEY_PREFIX_FILES = "files"
KEY_PREFIX_MANIFESTS = "manifests"
KEY_APP_MANIFEST = "jjflex-app-manifest.json"

CHUNK_SIZE = 1024 * 1024  # 1 MiB


# ---------------------------------------------------------------------------
# File walking + hashing
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class WalkedFile:
    rel_path: str           # POSIX-style, relative to published dir
    size_bytes: int
    sha256: str             # hex digest of the original (uncompressed) bytes
    full_path: Path


def is_excluded(rel_path: str, patterns: Iterable[str]) -> bool:
    name = rel_path.lower()
    base = os.path.basename(name)
    return any(fnmatch.fnmatch(name, p) or fnmatch.fnmatch(base, p) for p in patterns)


def sha256_file(path: Path, chunk_size: int = CHUNK_SIZE) -> tuple[str, int]:
    """Stream a file through sha256 to avoid loading multi-MB binaries into RAM."""
    hasher = hashlib.sha256()
    size = 0
    with path.open("rb") as f:
        while True:
            chunk = f.read(chunk_size)
            if not chunk:
                break
            hasher.update(chunk)
            size += len(chunk)
    return hasher.hexdigest(), size


def walk_published_dir(
    published_dir: Path,
    excludes: Iterable[str] = DEFAULT_EXCLUDES,
) -> list[WalkedFile]:
    """Recursively walk a published directory, returning sorted file metadata."""
    if not published_dir.is_dir():
        raise FileNotFoundError(f"published dir does not exist: {published_dir}")

    results: list[WalkedFile] = []
    for path in sorted(published_dir.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(published_dir).as_posix()
        if is_excluded(rel, excludes):
            continue
        digest, size = sha256_file(path)
        results.append(WalkedFile(rel_path=rel, size_bytes=size, sha256=digest, full_path=path))
    return results


# ---------------------------------------------------------------------------
# Compression
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class CompressedFile:
    rel_path: str
    size_bytes: int
    sha256: str             # original sha — content-addressable URL key
    compressed_bytes: bytes
    compressed_sha256: str
    compressed_size_bytes: int


def compress_bytes(data: bytes, preset: int = 6) -> bytes:
    """LZMA-compress to .xz format. Default preset 6 matches xz's default."""
    return lzma.compress(data, format=LZMA_FORMAT, preset=preset)


def compress_file(walked: WalkedFile, preset: int = 6) -> CompressedFile:
    """Read + compress a walked file in one shot.

    Files are loaded fully into memory for compression — published JJF
    binaries top out around ~150 MB total and individual files at <50 MB,
    so this is fine on a build box. If we ever need to compress
    multi-GB files, switch to lzma.LZMAFile streaming + a tempfile.
    """
    raw = walked.full_path.read_bytes()
    compressed = compress_bytes(raw, preset=preset)
    return CompressedFile(
        rel_path=walked.rel_path,
        size_bytes=walked.size_bytes,
        sha256=walked.sha256,
        compressed_bytes=compressed,
        compressed_sha256=hashlib.sha256(compressed).hexdigest(),
        compressed_size_bytes=len(compressed),
    )


# ---------------------------------------------------------------------------
# Manifest generation
# ---------------------------------------------------------------------------


def content_addressable_key(sha256: str) -> str:
    """files/<2-char prefix>/<full sha>.xz — keyed on ORIGINAL sha.

    Compressed bytes vary per run (LZMA params, parallelism), but the
    underlying file content is stable. Original-sha keying preserves
    URL stability across re-uploads of identical inputs.
    """
    return f"{KEY_PREFIX_FILES}/{sha256[:2]}/{sha256}{LZMA_EXTENSION}"


def file_url(public_base: str, sha256: str) -> str:
    return f"{public_base.rstrip('/')}/{content_addressable_key(sha256)}"


def build_file_manifest(
    version: str,
    platform: str,
    compressed: list[CompressedFile],
    public_base: str,
    prior_manifest: Optional[dict] = None,
) -> dict:
    """Produce the per-version manifest dict matching Track D's schema."""
    files = [
        {
            "rel_path": cf.rel_path,
            "size_bytes": cf.size_bytes,
            "sha256": cf.sha256,
            "compressed_size_bytes": cf.compressed_size_bytes,
            "compressed_sha256": cf.compressed_sha256,
            "url": file_url(public_base, cf.sha256),
        }
        for cf in compressed
    ]

    obsolete: list[str] = []
    if prior_manifest:
        prior_paths = {entry["rel_path"] for entry in prior_manifest.get("files", [])}
        new_paths = {cf.rel_path for cf in compressed}
        obsolete = sorted(prior_paths - new_paths)

    return {
        "schema_version": SCHEMA_VERSION,
        "version": version,
        "platform": platform,
        "files": files,
        "obsolete": obsolete,
    }


def file_manifest_key(version: str, platform: str) -> str:
    return f"{KEY_PREFIX_MANIFESTS}/{version}-{platform}.json"


# ---------------------------------------------------------------------------
# App manifest
# ---------------------------------------------------------------------------

VALID_CHANNELS = ("stable", "beta", "nightly")


def empty_app_manifest() -> dict:
    return {
        "schema_version": SCHEMA_VERSION,
        "channels": {ch: {"latest_version": None, "versions": []} for ch in VALID_CHANNELS},
    }


def _version_tuple(v: str) -> tuple:
    """Loose semver-like sort key. Handles 4.2.0.42, 4.2.0-rc1, etc.

    Numeric segments sort numerically; non-numeric trail (rc1, beta) sort
    lexically AFTER the bare version (so 4.2.0 > 4.2.0-rc1 holds via
    appending an empty-suffix sentinel).
    """
    head, _, tail = v.partition("-")
    nums = []
    for seg in head.split("."):
        try:
            nums.append((0, int(seg)))
            continue
        except ValueError:
            nums.append((1, seg))
    # No suffix sorts AFTER any suffix (release > prerelease).
    # Use (1,) for release so it beats any (0, "...") prerelease key.
    suffix_key = (0, tail) if tail else (1,)
    return (tuple(nums), suffix_key)


def update_app_manifest(
    current: dict,
    channel: str,
    version: str,
    platform: str,
    file_manifest_url: str,
    *,
    release_notes_url: Optional[str] = None,
    min_client_version: Optional[str] = None,
) -> dict:
    """Insert/update a version entry in `channel` and recompute latest_version.

    Returns a new dict; doesn't mutate `current`. Preserves all existing
    channel entries.
    """
    if channel not in VALID_CHANNELS:
        raise ValueError(f"unknown channel {channel!r}; valid: {VALID_CHANNELS}")

    updated = json.loads(json.dumps(current))  # deep copy
    updated.setdefault("schema_version", SCHEMA_VERSION)
    updated.setdefault("channels", {})

    ch = updated["channels"].setdefault(
        channel, {"latest_version": None, "versions": []}
    )

    entry = {
        "version": version,
        "platform": platform,
        "manifest_url": file_manifest_url,
    }
    if release_notes_url:
        entry["release_notes_url"] = release_notes_url
    if min_client_version:
        entry["min_client_version"] = min_client_version

    # Replace any existing entry with same (version, platform); else append.
    versions = ch["versions"]
    replaced = False
    for i, existing in enumerate(versions):
        if existing.get("version") == version and existing.get("platform") == platform:
            versions[i] = entry
            replaced = True
            break
    if not replaced:
        versions.append(entry)

    # Recompute latest_version across ALL platforms in this channel.
    if versions:
        sorted_versions = sorted(
            versions, key=lambda e: _version_tuple(e["version"]), reverse=True
        )
        ch["latest_version"] = sorted_versions[0]["version"]
    else:
        ch["latest_version"] = None

    return updated


# ---------------------------------------------------------------------------
# Orchestration
# ---------------------------------------------------------------------------


@dataclass
class GenerateResult:
    """Summary returned by `generate_release` for downstream reporting."""

    version: str
    platform: str
    channel: str
    file_count: int
    uploaded_blob_count: int
    skipped_blob_count: int
    obsolete_count: int
    file_manifest_key: str
    file_manifest_url: str
    app_manifest_updated: bool
    dry_run: bool
    no_upload: bool


def _log(message: str, *, dry_run: bool = False) -> None:
    prefix = "[DRY-RUN] " if dry_run else ""
    click.echo(f"{prefix}{message}")


def generate_release(
    *,
    published_dir: Path,
    version: str,
    platform: str,
    channel: str,
    public_base: str,
    output_dir: Optional[Path],
    dry_run: bool,
    no_upload: bool,
    update_app_manifest_flag: bool,
    prior_manifest: Optional[dict],
    excludes: Iterable[str],
    r2_bucket: Optional[str],
    release_notes_url: Optional[str],
    min_client_version: Optional[str],
    lzma_preset: int,
) -> GenerateResult:
    """Top-level workflow. Pure-ish: I/O confined to walker + R2 client."""

    if channel not in VALID_CHANNELS:
        raise click.BadParameter(
            f"channel must be one of {VALID_CHANNELS}, got {channel!r}"
        )

    _log(f"Walking {published_dir} ...")
    walked = walk_published_dir(published_dir, excludes=excludes)
    _log(f"  found {len(walked)} files (excludes: {list(excludes)})")

    _log("Compressing files (LZMA / .xz) ...")
    compressed = [compress_file(w, preset=lzma_preset) for w in walked]
    raw_total = sum(c.size_bytes for c in compressed)
    comp_total = sum(c.compressed_size_bytes for c in compressed)
    if raw_total:
        ratio = 100.0 * comp_total / raw_total
        _log(f"  raw {raw_total:,} bytes -> compressed {comp_total:,} bytes ({ratio:.1f}%)")

    _log("Building file-manifest ...")
    manifest = build_file_manifest(
        version=version,
        platform=platform,
        compressed=compressed,
        public_base=public_base,
        prior_manifest=prior_manifest,
    )
    manifest_key = file_manifest_key(version, platform)
    manifest_url = f"{public_base.rstrip('/')}/{manifest_key}"
    _log(f"  manifest URL: {manifest_url}")
    if manifest["obsolete"]:
        _log(f"  obsolete files (vs prior): {len(manifest['obsolete'])}")

    # Local writes (always when --output-dir set; mandatory when --no-upload)
    if output_dir is not None:
        output_dir.mkdir(parents=True, exist_ok=True)
        local_manifest_path = output_dir / f"{version}-{platform}.json"
        local_manifest_path.write_text(json.dumps(manifest, indent=2))
        _log(f"  wrote local copy: {local_manifest_path}")

    uploaded = 0
    skipped = 0
    app_manifest_updated = False

    if no_upload:
        _log("--no-upload: skipping all R2 calls.")
    else:
        from r2_client import R2Client, load_r2_config  # local import keeps tests light

        if dry_run:
            _log("Would upload compressed blobs:", dry_run=True)
            for cf in compressed:
                _log(f"  PUT {content_addressable_key(cf.sha256)}  ({cf.compressed_size_bytes} bytes)", dry_run=True)
            _log(f"Would upload file-manifest: {manifest_key}", dry_run=True)
            if update_app_manifest_flag:
                _log(f"Would update app-manifest channel '{channel}' with {version}/{platform}", dry_run=True)
            uploaded = len(compressed)
        else:
            config = load_r2_config(bucket_override=r2_bucket)
            client = R2Client(config)

            for cf in compressed:
                key = content_addressable_key(cf.sha256)
                if client.head(key) is not None:
                    skipped += 1
                    continue
                client.put(
                    key=key,
                    body=cf.compressed_bytes,
                    cache_control=CACHE_IMMUTABLE,
                    metadata={
                        "original-sha256": cf.sha256,
                        "compressed-sha256": cf.compressed_sha256,
                        "rel-path": cf.rel_path,
                    },
                )
                uploaded += 1

            _log(f"Blob upload: {uploaded} new, {skipped} dedup-skipped")

            client.put(
                key=manifest_key,
                body=json.dumps(manifest, indent=2).encode("utf-8"),
                content_type="application/json",
                cache_control=CACHE_FILE_MANIFEST,
            )
            _log(f"Uploaded file-manifest: {manifest_key}")

            if update_app_manifest_flag:
                current_bytes = client.get_bytes(KEY_APP_MANIFEST)
                if current_bytes is None:
                    _log("App-manifest does not exist yet — initializing.")
                    current_app = empty_app_manifest()
                else:
                    current_app = json.loads(current_bytes.decode("utf-8"))

                new_app = update_app_manifest(
                    current_app,
                    channel=channel,
                    version=version,
                    platform=platform,
                    file_manifest_url=manifest_url,
                    release_notes_url=release_notes_url,
                    min_client_version=min_client_version,
                )
                client.put(
                    key=KEY_APP_MANIFEST,
                    body=json.dumps(new_app, indent=2).encode("utf-8"),
                    content_type="application/json",
                    cache_control=CACHE_APP_MANIFEST,
                )
                app_manifest_updated = True
                _log(
                    f"Updated app-manifest channel '{channel}': "
                    f"latest_version={new_app['channels'][channel]['latest_version']}"
                )

    return GenerateResult(
        version=version,
        platform=platform,
        channel=channel,
        file_count=len(compressed),
        uploaded_blob_count=uploaded,
        skipped_blob_count=skipped,
        obsolete_count=len(manifest["obsolete"]),
        file_manifest_key=manifest_key,
        file_manifest_url=manifest_url,
        app_manifest_updated=app_manifest_updated,
        dry_run=dry_run,
        no_upload=no_upload,
    )


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


@click.group()
def cli() -> None:
    """JJFlex manifest generator (Sprint 29 Track N)."""


@cli.command("generate")
@click.option(
    "--published-dir",
    required=True,
    type=click.Path(exists=True, file_okay=False, dir_okay=True, path_type=Path),
    help="Path to the published JJF release directory.",
)
@click.option("--version", required=True, help="Full 4-part version, e.g. 4.2.0.42")
@click.option("--platform", required=True, help="Target platform, e.g. win-x64 / win-x86")
@click.option(
    "--channel",
    required=True,
    type=click.Choice(VALID_CHANNELS, case_sensitive=False),
    help="Update channel.",
)
@click.option("--r2-bucket", default=None, help="R2 bucket name (overrides $R2_BUCKET).")
@click.option(
    "--public-base",
    default="https://data.jjflexible.radio",
    help="Public base URL files will be served from.",
)
@click.option(
    "--prior-manifest",
    type=click.Path(exists=True, dir_okay=False, path_type=Path),
    default=None,
    help="Path to the previous version's file-manifest JSON; used to compute the obsolete list.",
)
@click.option(
    "--output-dir",
    type=click.Path(file_okay=False, dir_okay=True, path_type=Path),
    default=None,
    help="Local directory to write manifest copies into (in addition to upload).",
)
@click.option(
    "--release-notes-url",
    default=None,
    help="Optional URL to release notes; goes into the app-manifest entry.",
)
@click.option(
    "--min-client-version",
    default=None,
    help="Optional minimum JJF version required to install this build. Drives chained-updater flow.",
)
@click.option(
    "--lzma-preset",
    type=click.IntRange(0, 9),
    default=6,
    show_default=True,
    help="LZMA preset (0=fastest, 9=smallest). 6 matches xz default.",
)
@click.option(
    "--exclude",
    "excludes",
    multiple=True,
    default=DEFAULT_EXCLUDES,
    show_default=True,
    help="Glob to exclude (repeatable). Replaces defaults if specified.",
)
@click.option("--dry-run", is_flag=True, help="Print actions without executing.")
@click.option(
    "--no-upload",
    is_flag=True,
    help="Skip all R2 calls; write manifest locally only. Requires --output-dir.",
)
@click.option(
    "--update-app-manifest",
    "update_app_manifest_flag",
    is_flag=True,
    help="Also update the top-level jjflex-app-manifest.json channel pointer.",
)
def generate_cmd(
    published_dir: Path,
    version: str,
    platform: str,
    channel: str,
    r2_bucket: Optional[str],
    public_base: str,
    prior_manifest: Optional[Path],
    output_dir: Optional[Path],
    release_notes_url: Optional[str],
    min_client_version: Optional[str],
    lzma_preset: int,
    excludes: tuple,
    dry_run: bool,
    no_upload: bool,
    update_app_manifest_flag: bool,
) -> None:
    """Generate manifests + upload compressed blobs for one published build."""

    if no_upload and output_dir is None:
        raise click.BadParameter("--no-upload requires --output-dir.")

    prior = None
    if prior_manifest is not None:
        prior = json.loads(prior_manifest.read_text(encoding="utf-8"))

    result = generate_release(
        published_dir=published_dir,
        version=version,
        platform=platform,
        channel=channel.lower(),
        public_base=public_base,
        output_dir=output_dir,
        dry_run=dry_run,
        no_upload=no_upload,
        update_app_manifest_flag=update_app_manifest_flag,
        prior_manifest=prior,
        excludes=excludes,
        r2_bucket=r2_bucket,
        release_notes_url=release_notes_url,
        min_client_version=min_client_version,
        lzma_preset=lzma_preset,
    )

    click.echo("")
    click.echo("=== Summary ===")
    click.echo(json.dumps(asdict(result), indent=2))


if __name__ == "__main__":
    cli()
