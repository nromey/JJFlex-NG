"""Tests for the CLI in dry-run and no-upload modes (no R2 calls).

These also exercise the full `generate` orchestration to catch wiring
mistakes between walk/compress/manifest/upload.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from click.testing import CliRunner

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from manifest_gen import cli  # noqa: E402


def _make_published_dir(root: Path) -> None:
    (root / "JJFlexRadio.exe").write_bytes(b"exe payload" * 200)
    (root / "FlexLib.dll").write_bytes(b"dll payload" * 200)
    sub = root / "runtimes" / "win-x64" / "native"
    sub.mkdir(parents=True)
    (sub / "portaudio.dll").write_bytes(b"native payload" * 200)
    (root / "JJFlexRadio.pdb").write_bytes(b"pdb should be excluded")


def test_dry_run_does_not_touch_r2(tmp_path: Path):
    _make_published_dir(tmp_path)
    runner = CliRunner()
    result = runner.invoke(
        cli,
        [
            "generate",
            "--published-dir", str(tmp_path),
            "--version", "4.2.0.99-test",
            "--platform", "win-x64",
            "--channel", "nightly",
            "--dry-run",
        ],
    )
    assert result.exit_code == 0, result.output
    assert "[DRY-RUN]" in result.output
    assert "PUT files/" in result.output  # would-upload lines present
    # Excluded file must NOT show up
    assert "JJFlexRadio.pdb" not in result.output


def test_no_upload_writes_local_manifest(tmp_path: Path):
    src = tmp_path / "src"
    src.mkdir()
    _make_published_dir(src)

    out = tmp_path / "out"

    runner = CliRunner()
    result = runner.invoke(
        cli,
        [
            "generate",
            "--published-dir", str(src),
            "--version", "4.2.0.99-test",
            "--platform", "win-x64",
            "--channel", "nightly",
            "--no-upload",
            "--output-dir", str(out),
        ],
    )
    assert result.exit_code == 0, result.output

    manifest_path = out / "4.2.0.99-test-win-x64.json"
    assert manifest_path.exists()
    manifest = json.loads(manifest_path.read_text())
    assert manifest["version"] == "4.2.0.99-test"
    assert manifest["platform"] == "win-x64"
    rel_paths = [f["rel_path"] for f in manifest["files"]]
    assert "JJFlexRadio.exe" in rel_paths
    assert "FlexLib.dll" in rel_paths
    assert "runtimes/win-x64/native/portaudio.dll" in rel_paths
    assert "JJFlexRadio.pdb" not in rel_paths


def test_no_upload_requires_output_dir(tmp_path: Path):
    _make_published_dir(tmp_path)
    runner = CliRunner()
    result = runner.invoke(
        cli,
        [
            "generate",
            "--published-dir", str(tmp_path),
            "--version", "4.2.0.99-test",
            "--platform", "win-x64",
            "--channel", "nightly",
            "--no-upload",
        ],
    )
    assert result.exit_code != 0
    assert "--output-dir" in result.output


def test_invalid_channel_rejected(tmp_path: Path):
    _make_published_dir(tmp_path)
    runner = CliRunner()
    result = runner.invoke(
        cli,
        [
            "generate",
            "--published-dir", str(tmp_path),
            "--version", "4.2.0.99-test",
            "--platform", "win-x64",
            "--channel", "rolling",
            "--dry-run",
        ],
    )
    assert result.exit_code != 0
