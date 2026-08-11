---
date: 2026-05-06
collected from: Don's Dropbox folder (`Dropbox/JJFlexRadio/don/`)
context: FlexLib 4.2.18 silent-discovery investigation + cache-writer-backport end-to-end validation
---

# 2026-05-06 trace corpus — Don's R6-via-cache-writer validation

The day's full diagnostic-and-validation paper trail. Don sent four traces and one config artifact across two sessions, and the cache-writer backport plus R6 build validated end-to-end. This folder preserves everything in original form for future bisection / reference.

## Morning session (~5:41 AM Don's time)

Failed-discovery scenario that triggered the whole day's design pivot.

| File | Build | Outcome |
|---|---|---|
| `working-installed-build-AM.txt` | Don's installed working build (`Program Files`, 4.1.16.44) | Connected normally via UDP, 1.1s. Confirms 4.1-line UDP works fine on his box. |
| `r6-AM-no_cache-failed.txt` | R6 (`jjf-test`, 4.1.16.0, FlexLib 4.2.18) | Rung 1a `no_cache` in 2ms, fell to UDP, silent-fail. The chicken-and-egg trace. |
| `wa2iwc_Donald_Breda_autoConnectV2.xml` | (config artifact) | Don's autoconnect file — empirical proof that no IP is stored anywhere on disk pre-R6. |
| `JJFlex_v4.2.18-test_NOTES_R6_yesterday.txt` | (NOTES file shipped with R6) | Yesterday's NOTES — contains the "your IP is already there from your old working build" claim, since superseded. |

## Evening session (~6:56 PM Don's time)

Validation of the cache-writer-backport plan.

| File | Build | Outcome |
|---|---|---|
| `JJFlex_4.1.16.241_x64_debug.zip` | First cache writer build (`jjf-41`) | Stored version as decimal-of-ulong (parse-fail bug, polish-fixed in 4.1.16.242). |
| `NOTES-4.1.16.241-debug.txt` | (NOTES file shipped with .241) | Two-step instructions for Don. |
| `cache-writer-4.1.16.241-PM.txt` | 4.1.16.241 cache writer | Connected via UDP, 4.5s. Cache file written. |
| `r6-PM-rung-1a-success-296ms.txt` | R6 reading the seeded cache | **Rung 1a success in 162ms; total connect 296ms.** End-to-end validation. |

## Reference

| File | Notes |
|---|---|
| `trace_april30.zip` | Pre-existing trace from April 30 — kept since Don originally sent it during R3/R4 investigation. |

## What this validates

- 4.1-line UDP discovery: works on Don's box (`working-installed-build-AM.txt`).
- 4.2.18-line UDP discovery: silent on Don's box (`r6-AM-no_cache-failed.txt`, line `Discovery.SyncDrain: complete -- received 0 total`).
- Cache-writer backport: writes cache file on successful Connect (verified by Rung 1a winning in next session).
- R6 cascade: works correctly when cache is seeded — `Rung 1a -> success in 160ms`.
- The chicken-and-egg dissolves for upgrade-path users; fresh-install users still need the v3 cascade to land before 4.2.0.

## Cross-references

- v3 design memo: `docs/planning/design/discovery-fallback-chain-v3.md`
- Investigation memory: `memory/project_flexlib_4218_discovery_investigation.md`
- Cache writer commit: `e2d4ebea` (initial), `e7e2e3b2` (version-string polish fix)
- Trace-level fix on track/flexlib-42: `5bfc6501`
