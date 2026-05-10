"""Tests for app-manifest channel-pointer update logic."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from manifest_gen import (  # noqa: E402
    VALID_CHANNELS,
    _version_tuple,
    empty_app_manifest,
    update_app_manifest,
)


# ---- _version_tuple ordering ----


def test_version_tuple_orders_simple_versions():
    versions = ["4.2.0.42", "4.1.16.0", "4.2.0.5", "4.2.0.99"]
    sorted_versions = sorted(versions, key=_version_tuple)
    assert sorted_versions == ["4.1.16.0", "4.2.0.5", "4.2.0.42", "4.2.0.99"]


def test_version_tuple_release_beats_prerelease():
    assert _version_tuple("4.2.0") > _version_tuple("4.2.0-rc1")
    assert _version_tuple("4.2.0") > _version_tuple("4.2.0-beta")


def test_version_tuple_handles_short_versions():
    assert _version_tuple("4.2.0") < _version_tuple("4.2.0.1")


# ---- empty_app_manifest ----


def test_empty_app_manifest_has_all_channels():
    m = empty_app_manifest()
    assert set(m["channels"].keys()) == set(VALID_CHANNELS)
    for ch in VALID_CHANNELS:
        assert m["channels"][ch]["latest_version"] is None
        assert m["channels"][ch]["versions"] == []


# ---- update_app_manifest ----


def test_update_app_manifest_adds_new_version():
    m = empty_app_manifest()
    updated = update_app_manifest(
        m,
        channel="nightly",
        version="4.2.0.42",
        platform="win-x64",
        file_manifest_url="https://data.jjflexible.radio/manifests/4.2.0.42-win-x64.json",
    )
    nightly = updated["channels"]["nightly"]
    assert nightly["latest_version"] == "4.2.0.42"
    assert len(nightly["versions"]) == 1
    entry = nightly["versions"][0]
    assert entry["version"] == "4.2.0.42"
    assert entry["platform"] == "win-x64"
    assert entry["manifest_url"].endswith("4.2.0.42-win-x64.json")


def test_update_app_manifest_preserves_other_channels():
    m = empty_app_manifest()
    m["channels"]["stable"]["versions"] = [
        {"version": "4.1.15.0", "platform": "win-x64", "manifest_url": "..."}
    ]
    m["channels"]["stable"]["latest_version"] = "4.1.15.0"

    updated = update_app_manifest(
        m,
        channel="nightly",
        version="4.2.0.42",
        platform="win-x64",
        file_manifest_url="...",
    )
    assert updated["channels"]["stable"]["latest_version"] == "4.1.15.0"
    assert len(updated["channels"]["stable"]["versions"]) == 1


def test_update_app_manifest_does_not_mutate_input():
    m = empty_app_manifest()
    snapshot_id = id(m["channels"]["nightly"]["versions"])
    update_app_manifest(
        m,
        channel="nightly",
        version="4.2.0.42",
        platform="win-x64",
        file_manifest_url="...",
    )
    assert m["channels"]["nightly"]["versions"] == []
    assert id(m["channels"]["nightly"]["versions"]) == snapshot_id


def test_update_app_manifest_recomputes_latest_version():
    m = empty_app_manifest()
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.41", platform="win-x64",
        file_manifest_url="...",
    )
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.42", platform="win-x64",
        file_manifest_url="...",
    )
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.40", platform="win-x64",
        file_manifest_url="...",
    )
    assert m["channels"]["nightly"]["latest_version"] == "4.2.0.42"
    assert len(m["channels"]["nightly"]["versions"]) == 3


def test_update_app_manifest_replaces_same_version_platform_pair():
    m = empty_app_manifest()
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.42", platform="win-x64",
        file_manifest_url="https://old/url",
    )
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.42", platform="win-x64",
        file_manifest_url="https://new/url",
    )
    nightly_versions = m["channels"]["nightly"]["versions"]
    assert len(nightly_versions) == 1
    assert nightly_versions[0]["manifest_url"] == "https://new/url"


def test_update_app_manifest_keeps_separate_platform_entries():
    m = empty_app_manifest()
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.42", platform="win-x64",
        file_manifest_url="...",
    )
    m = update_app_manifest(
        m, channel="nightly", version="4.2.0.42", platform="win-x86",
        file_manifest_url="...",
    )
    assert len(m["channels"]["nightly"]["versions"]) == 2


def test_update_app_manifest_optional_fields():
    m = empty_app_manifest()
    m = update_app_manifest(
        m, channel="beta", version="4.2.0.42", platform="win-x64",
        file_manifest_url="...",
        release_notes_url="https://jjflexible.radio/release-notes#4.2.0.42",
        min_client_version="4.2.0.0",
    )
    entry = m["channels"]["beta"]["versions"][0]
    assert entry["release_notes_url"].endswith("#4.2.0.42")
    assert entry["min_client_version"] == "4.2.0.0"


def test_update_app_manifest_rejects_unknown_channel():
    with pytest.raises(ValueError):
        update_app_manifest(
            empty_app_manifest(),
            channel="rolling",
            version="4.2.0.42",
            platform="win-x64",
            file_manifest_url="...",
        )
