"""Tests for file walking, sha256, and exclusion handling."""

from __future__ import annotations

import hashlib
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from manifest_gen import (  # noqa: E402
    DEFAULT_EXCLUDES,
    is_excluded,
    sha256_file,
    walk_published_dir,
)


def test_sha256_file_matches_known_value(tmp_path: Path):
    payload = b"hello jjflex"
    f = tmp_path / "x.bin"
    f.write_bytes(payload)
    digest, size = sha256_file(f)
    assert digest == hashlib.sha256(payload).hexdigest()
    assert size == len(payload)


def test_sha256_file_handles_large_streamed_content(tmp_path: Path):
    payload = b"a" * (3 * 1024 * 1024 + 17)  # > chunk size, non-aligned
    f = tmp_path / "big.bin"
    f.write_bytes(payload)
    digest, size = sha256_file(f, chunk_size=1024 * 1024)
    assert digest == hashlib.sha256(payload).hexdigest()
    assert size == len(payload)


def test_sha256_file_empty(tmp_path: Path):
    f = tmp_path / "empty.bin"
    f.write_bytes(b"")
    digest, size = sha256_file(f)
    assert digest == hashlib.sha256(b"").hexdigest()
    assert size == 0


def test_is_excluded_pdb():
    assert is_excluded("JJFlexRadio.pdb", DEFAULT_EXCLUDES)
    assert is_excluded("subdir/JJFlexRadio.pdb", DEFAULT_EXCLUDES)


def test_is_excluded_env():
    assert is_excluded(".env", DEFAULT_EXCLUDES)
    assert is_excluded(".env.local", DEFAULT_EXCLUDES)


def test_is_excluded_case_insensitive():
    assert is_excluded("FOO.PDB", DEFAULT_EXCLUDES)
    assert is_excluded("Thumbs.db", DEFAULT_EXCLUDES)


def test_is_excluded_normal_files_kept():
    assert not is_excluded("JJFlexRadio.exe", DEFAULT_EXCLUDES)
    assert not is_excluded("runtimes/win-x64/native/portaudio.dll", DEFAULT_EXCLUDES)
    assert not is_excluded("config.xml", DEFAULT_EXCLUDES)


def test_walk_published_dir_basic(tmp_path: Path):
    (tmp_path / "JJFlexRadio.exe").write_bytes(b"exe content")
    (tmp_path / "FlexLib.dll").write_bytes(b"dll content")
    (tmp_path / "JJFlexRadio.pdb").write_bytes(b"pdb content")  # excluded
    sub = tmp_path / "runtimes" / "win-x64" / "native"
    sub.mkdir(parents=True)
    (sub / "portaudio.dll").write_bytes(b"native content")

    walked = walk_published_dir(tmp_path)
    rel_paths = [w.rel_path for w in walked]

    assert "JJFlexRadio.exe" in rel_paths
    assert "FlexLib.dll" in rel_paths
    assert "runtimes/win-x64/native/portaudio.dll" in rel_paths
    assert "JJFlexRadio.pdb" not in rel_paths
    assert rel_paths == sorted(rel_paths)


def test_walk_published_dir_uses_posix_separators_on_all_platforms(tmp_path: Path):
    sub = tmp_path / "a" / "b"
    sub.mkdir(parents=True)
    (sub / "c.bin").write_bytes(b"x")
    walked = walk_published_dir(tmp_path)
    assert walked[0].rel_path == "a/b/c.bin"


def test_walk_published_dir_empty(tmp_path: Path):
    assert walk_published_dir(tmp_path) == []


def test_walk_published_dir_missing_dir(tmp_path: Path):
    with pytest.raises(FileNotFoundError):
        walk_published_dir(tmp_path / "does-not-exist")


def test_walk_published_dir_custom_excludes(tmp_path: Path):
    (tmp_path / "keep.txt").write_bytes(b"k")
    (tmp_path / "skip.special").write_bytes(b"s")
    walked = walk_published_dir(tmp_path, excludes=("*.special",))
    rel_paths = [w.rel_path for w in walked]
    assert "keep.txt" in rel_paths
    assert "skip.special" not in rel_paths
