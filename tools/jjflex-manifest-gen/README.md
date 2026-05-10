# jjflex-manifest-gen — Sprint 29 Track N

Server-side tooling that takes a published JJFlexible directory and produces:

1. A per-version `file-manifest.json` listing every file (relative path, size, original sha256, compressed size, compressed sha256, public URL).
2. Each file LZMA-compressed (`.xz`) and uploaded to content-addressable storage at `data.jjflexible.radio/files/<sha-prefix>/<full-sha>.xz`.
3. The top-level `jjflex-app-manifest.json` updated with the new version's manifest URL on the chosen channel (stable / beta / nightly).

This is the **server-side counterpart to Track D's client-side delta-fetch logic.** Without it, Track D has no manifests to fetch.

Per `memory/project_data_provider_hosting.md`, `data.jjflexible.radio` is fronted by Cloudflare R2 with zero egress cost.

## Prerequisites

- Python 3.11+ (rarbox/roarbox already have it for the FastAPI receiver)
- R2 bucket + API token with read/write to that bucket
- The bucket must be public-read at the custom domain so clients can fetch by URL

## Setup

```powershell
cd tools\jjflex-manifest-gen
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

## R2 credentials

Set these in the environment (or load from a `.env` file you don't commit):

| Variable | Purpose |
|----------|---------|
| `R2_ACCOUNT_ID` | Cloudflare account ID — used to build the S3 endpoint URL |
| `R2_ACCESS_KEY_ID` | API token access-key half |
| `R2_SECRET_ACCESS_KEY` | API token secret half |
| `R2_BUCKET` | Bucket name (e.g. `jjflex-data`); can be overridden per-invocation with `--r2-bucket` |

The S3-compatible endpoint is built automatically as `https://<account-id>.r2.cloudflarestorage.com`.

On roarbox the CI/CD job exports these from a one-shot env file before invoking the script.

## Run the tests

```powershell
pytest
```

36 tests; runs in well under a second. No network calls, no R2 dependency.

## Usage

### Generate + upload + update app-manifest

```powershell
python manifest_gen.py generate `
    --published-dir C:\dev\JJFlex-NG\bin\x64\Release\net10.0-windows\win-x64\publish `
    --version 4.2.0.42 `
    --platform win-x64 `
    --channel nightly `
    --update-app-manifest
```

### Dry-run for testing without R2 credentials

```powershell
python manifest_gen.py generate `
    --published-dir .\test-fixtures\sample-publish `
    --version 4.2.0.99-test `
    --platform win-x64 `
    --channel nightly `
    --dry-run
```

### Local-only (write manifests to disk, no upload)

```powershell
python manifest_gen.py generate `
    --published-dir .\test-fixtures\sample-publish `
    --version 4.2.0.99-test `
    --platform win-x64 `
    --channel nightly `
    --no-upload `
    --output-dir .\test-output
```

### With prior-manifest delta (computes the `obsolete` list)

```powershell
python manifest_gen.py generate `
    --published-dir .\publish `
    --version 4.2.0.42 `
    --platform win-x64 `
    --channel nightly `
    --prior-manifest .\prior-manifests\4.2.0.41-win-x64.json `
    --update-app-manifest
```

## Compression format

Files are compressed with Python's `lzma` stdlib in `FORMAT_XZ` mode. Track D decompresses with SharpCompress's `XZStream`. The `.xz` framed format carries a header + integrity check, which `FORMAT_ALONE` (legacy `.lzma`) and `FORMAT_RAW` (no header) don't.

## Content-addressable layout

```
files/<2-char prefix>/<full-sha256>.xz       — compressed file blobs
manifests/<version>-<platform>.json          — per-version file-manifest
jjflex-app-manifest.json                     — top-level channel index
```

The blob URL key uses the **original** sha256 (not the compressed sha256). LZMA isn't deterministic across compression-parameter variations, so the compressed bytes can differ run-to-run on identical input. The original sha is stable, so the URL is stable, so dedup works across re-uploads.

## Cache headers

| Object | `Cache-Control` |
|--------|-----------------|
| `files/...xz` | `public, max-age=31536000, immutable` |
| `manifests/<version>-<platform>.json` | `public, max-age=300` |
| `jjflex-app-manifest.json` | `no-cache` |

## Exclusion list

These never ship by default (case-insensitive glob, matched against POSIX rel-path):

```
*.pdb
*.lastcodeanalysissucceeded
.env
.env.*
*.user
thumbs.db
.ds_store
desktop.ini
```

Override with one or more `--exclude PATTERN` flags (the supplied list replaces the defaults; pass each pattern explicitly if you want to extend rather than replace).

## Idempotence

Running the tool twice on the same published directory:

- Computes identical original-sha256 values (deterministic).
- Skips re-uploading every blob (R2 `head_object` dedup check).
- Re-writes the file-manifest (cheap; 5-minute TTL anyway).
- Replaces the matching `(version, platform)` entry in the app-manifest if it already exists; otherwise appends.

## App-manifest concurrency

For v1, the app-manifest read-modify-write is racy under concurrent CI/CD jobs. Releases are minutes apart, not seconds, so the window is tolerable. R2's `If-Match` ETag is plumbed through `R2Client.put` for a future hardening pass.

## Don't ship secrets

The exclusion list filters known offenders, but if you're publishing from a workspace that may contain other secrets (`appsettings.Development.json`, ad-hoc `.token` files), audit before running. The script never reads files outside `--published-dir`, but it will happily upload anything under that root that isn't excluded.
