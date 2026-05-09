---
title: Discovery Cascade v3 — Research Phase
status: research-in-flight
spawned: 2026-05-06
authorized-by: Noel
---

# Discovery Cascade v3 — Comprehensive Research

## Why this exists

The round-2 design memo at `docs/planning/design/discovery-fallback-chain.md` was ACK'd 2026-05-04 with six rungs. Only Rungs 1a + 1b were built and shipped in R6 (per the orchestrator-direction-file-bounded scope on 2026-05-05). Today, 2026-05-06, the chicken-and-egg in the cache-greenfield-on-fresh-install case became visible during Don's R6 trace analysis. The team's response: don't just build the four missing rungs from the round-2 memo — research more methods, more thoroughly, to land a v3 design that's actually comprehensive.

Per Noel's direction: *"Don't spare anything, research new options of discovery. ... I'll deal with lots of friction now if we can load up a newly installed JJ Flexible Radio on an old firmware that's connected to the network. ... I don't care if this research takes literally hours."*

## Streams

| # | Stream | Status | Output file | Notes |
|---|---|---|---|---|
| 1 | Windows OS IP-discovery primitives | **complete** | `stream-1-windows-os-primitives.md` | 23 mechanisms; sleeper hit = `GetExtendedTcpTable` (catches running-SmartSDR + TIME_WAIT). NLM gates cache validity. |
| 2 | Empirical Flex-radio LAN-probe survey | **needs Noel** | `stream-2-empirical-flex-probes.md` (Noel writes) | Runbook at `stream-2-runbook-for-noel.md`. Active probing of Noel's own radios with Wireshark/nmap/etc. |
| 3 | Third-party app config scrape | **complete** | `stream-3-thirdparty-config-scrape.md` | SmartSDR XML files are highest-yield. Most apps store loopback (SmartCAT pattern) — must filter. Data Provider manifest hook. |
| 4 | Other SDR / accessibility apps patterns | **complete** | `stream-4-other-sdr-accessibility-patterns.md` | JJF is genuinely inventing — no surveyed app has a comparable cascade. 14 named patterns, 10 anti-patterns, 39 citations. |
| 5 | Edge cases & failure modes | **complete** | `stream-5-edge-cases.md` | 46 cases (20 prompted + 26 found). Tailscale, multi-NIC, virtual-adapter ARP, APIPA all real. IPv6 confirmed out of scope. |
| 6 | Manual fallback UX | **complete** | `stream-6-manual-fallback-ux.md` | Single dialog beats wizard. "Show last-known IPs" is load-bearing; IP entry is fallback. Greenfield UX in Flex ecosystem. 10 open Qs for round-2. |
| 7 | Novel discovery methods | **complete** | `stream-7-novel-methods.md` | 9 novel methods + ruled-out list with rationale. Surprises: hosts file, reverse-DNS sweep, NetworkAddressChanged background watchdog. 10 empirical tests for Stream 2. |

## Synthesis status

Round-3 design memo synthesized 2026-05-06 from 6 of 7 streams (Stream 2 deferred to round-4 update once hardware-time happens). Canonical doc:

`docs/planning/design/discovery-fallback-chain-v3.md` — supersedes round-2.

Headline: cascade expands from 6 rungs to ~10 active rungs plus a `NetworkAddressChanged` background watchdog. Sequential rung-walking replaced by two-batch concurrent execution. SmartLink-as-fallback-row baked into UX. ~2,300-2,900 LOC for 4.2.0 release-blocking scope.

15 open questions distilled for Noel's round-3 review. Most load-bearing: confirm FlexRadio's MAC OUI bytes (Rung 1.5a's ARP filter depends on it) — recommend Stream 2 hardware check before implementation begins.

After Noel ACKs round-3:

1. Round-4 update folds in Stream 2's empirical findings.
2. Spawn `track/discovery-chain-full-buildout` to implement the locked design.
3. v3 is a 4.2.0 release-blocking spec.

## Cross-references

- Round-2 design memo: `docs/planning/design/discovery-fallback-chain.md`
- Investigation memory: `memory/project_flexlib_4218_discovery_investigation.md`
- Friction-tax principle: `memory/project_friction_tax_principle.md`
- Today's chicken-and-egg finding: `memory/project_autoconnect_no_ip_dead_end.md`
- Cache-writer backport (4.1-line fix shipped to Don 2026-05-06): commit `e2d4ebea` on `feature/cache-writer-backport`
- Trace-level fix on track/flexlib-42: commit `5bfc6501`
