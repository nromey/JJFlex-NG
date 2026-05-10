"""Tests for LZMA compression and per-version file-manifest generation."""

from __future__ import annotations

import hashlib
import json
import lzma
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from manifest_gen import (  # noqa: E402
    LZMA_FORMAT,
    SCHEMA_VERSION,
    build_file_manifest,
    compress_bytes,
    compress_file,
    content_addressable_key,
    file_manifest_key,
    walk_published_dir,
)


def test_compress_bytes_roundtrips_through_xz():
    payload = b"jjflex repeats jjflex repeats jjflex repeats" * 100
    compressed = compress_bytes(payload)
    assert compressed != payload
    assert lzma.decompress(compressed, format=LZMA_FORMAT) == payload


def test_compress_bytes_uses_xz_format_magic():
    # XZ stream header: 0xFD '7' 'z' 'X' 'Z' 0x00
    compressed = compress_bytes(b"hello")
    assert compressed[:6] == b"\xfd7zXZ\x00"


def test_compress_file_populates_all_fields(tmp_path: Path):
    (tmp_path / "data.bin").write_bytes(b"some random payload" * 50)
    walked = walk_published_dir(tmp_path)
    assert len(walked) == 1

    cf = compress_file(walked[0])
    assert cf.rel_path == "data.bin"
    assert cf.size_bytes == walked[0].size_bytes
    assert cf.sha256 == walked[0].sha256
    assert cf.compressed_size_bytes == len(cf.compressed_bytes)
    assert cf.compressed_sha256 == hashlib.sha256(cf.compressed_bytes).hexdigest()
    # Original sha and compressed sha differ.
    assert cf.sha256 != cf.compressed_sha256


def test_content_addressable_key_uses_two_char_prefix():
    sha = "1a2b3c" + "0" * 58
    key = content_addressable_key(sha)
    assert key == f"files/1a/{sha}.xz"


def test_file_manifest_key_format():
    assert file_manifest_key("4.2.0.42", "win-x64") == "manifests/4.2.0.42-win-x64.json"


def test_build_file_manifest_schema(tmp_path: Path):
    (tmp_path / "JJFlexRadio.exe").write_bytes(b"exe")
    (tmp_path / "FlexLib.dll").write_bytes(b"dll")
    walked = walk_published_dir(tmp_path)
    compressed = [compress_file(w) for w in walked]

    manifest = build_file_manifest(
        version="4.2.0.42",
        platform="win-x64",
        compressed=compressed,
        public_base="https://data.jjflexible.radio",
    )

    assert manifest["schema_version"] == SCHEMA_VERSION
    assert manifest["version"] == "4.2.0.42"
    assert manifest["platform"] == "win-x64"
    assert manifest["obsolete"] == []
    assert len(manifest["files"]) == 2

    file_entry = manifest["files"][0]
    expected_keys = {
        "rel_path",
        "size_bytes",
        "sha256",
        "compressed_size_bytes",
        "compressed_sha256",
        "url",
    }
    assert set(file_entry.keys()) == expected_keys
    assert file_entry["url"].startswith("https://data.jjflexible.radio/files/")
    assert file_entry["url"].endswith(".xz")


def test_build_file_manifest_obsolete_diff(tmp_path: Path):
    (tmp_path / "kept.bin").write_bytes(b"k")
    (tmp_path / "added.bin").write_bytes(b"a")
    walked = walk_published_dir(tmp_path)
    compressed = [compress_file(w) for w in walked]

    prior = {
        "schema_version": 1,
        "version": "4.2.0.41",
        "platform": "win-x64",
        "files": [
            {"rel_path": "kept.bin", "size_bytes": 1, "sha256": "old", "compressed_size_bytes": 1, "compressed_sha256": "old", "url": "..."},
            {"rel_path": "removed.bin", "size_bytes": 1, "sha256": "old", "compressed_size_bytes": 1, "compressed_sha256": "old", "url": "..."},
        ],
        "obsolete": [],
    }

    manifest = build_file_manifest(
        version="4.2.0.42",
        platform="win-x64",
        compressed=compressed,
        public_base="https://data.jjflexible.radio",
        prior_manifest=prior,
    )
    assert manifest["obsolete"] == ["removed.bin"]


def test_manifest_is_json_serializable(tmp_path: Path):
    (tmp_path / "x.bin").write_bytes(b"x")
    walked = walk_published_dir(tmp_path)
    compressed = [compress_file(w) for w in walked]
    manifest = build_file_manifest(
        version="4.2.0.42",
        platform="win-x64",
        compressed=compressed,
        public_base="https://data.jjflexible.radio",
    )
    # Must round-trip through JSON without TypeError.
    s = json.dumps(manifest)
    assert json.loads(s) == manifest
