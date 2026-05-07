---
type: design pull-doc — round 3 ACK
priority: high — gates 4.2.0 release scope and `track/discovery-chain-full-buildout` spawn
draft author: Claude (synthesis from 6 background research streams)
date: 2026-05-06
canonical doc: docs/planning/design/discovery-fallback-chain-v3.md (813 lines, supersedes round-2)
related memories: project_friction_tax_principle.md, project_no_silent_phone_home.md, project_flexibility_principle.md, project_anti_patterns_from_blindcat.md
---

# Discovery cascade — round 3 design ACK pull

## Why this exists

Round 2 was ACK'd 2026-05-04 with six rungs. R6 shipped only 2 of 6 (1a + 1b); Phases 2-6 were scoped out by the orchestrator-direction file at build time. The 2026-05-06 chicken-and-egg discovery on Don's box (cache empty on fresh install ⇒ Rung 1a fails ⇒ falls through to broken UDP) made the missing rungs urgent. You authorized a "spare nothing" round-3 research effort. Six background agents completed; one stream (empirical, your hardware) is deferred to a round-4 update once you run the runbook.

The synthesis lives at `docs/planning/design/discovery-fallback-chain-v3.md` — full design with rung-by-rung specs, architecture, edge cases, UX, ruled-out methods, implementation phasing.

This pull-doc is the **review surface only**. Read here to ACK or revise; the canonical doc is the spec that gets implemented.

## What's substantively new vs round 2

- Cascade expanded from 6 rungs to ~10 active rungs.
- New rungs: 1c (N-deep history), 1.5a (ARP read), 1.5b (TCP table read), 1.6 (hosts file), 1.7 (third-party config scrape), 1.8 (hostname-pattern probe), 1.9 (reverse-DNS PTR sweep), 4b (SmartLink-as-metadata-provider), 5-recovery (APIPA scan, user-invoked).
- Sequential rung-walking → two-batch concurrent execution (Plex/Sonos/Home Assistant pattern).
- `NetworkAddressChanged` background watchdog promoted to top-level layer (not a rung — a persistent listener that re-runs the cascade when laptop joins a new network). Stream 7 estimates eliminates 30-50% of "JJF doesn't see my radio" support cases.
- SmartLink rows surface in chooser AS the cascade runs (Stream 4's biggest UX-win finding).
- Stream 6 Layout A locked as canonical manual-fallback UX. Pre-dialog "We couldn't find your radio" prompt; wizard explicitly rejected.
- NLM-based network identity gating prevents wasted probes on different networks.
- Per-interface enumeration with virtual-adapter skip-list and Tailscale-aware socket binding handle multi-NIC and VPN edges.

## Implementation phasing summary

- **4.2.0 release-blocking:** ~2,300–2,900 LOC across 14 items. Includes all must-build rungs, the watchdog, NLM gating, per-interface enumeration, manual fallback UX, diagnostic capture wiring.
- **4.2.x post-release:** 12 items. Rung 4b live-test upgrade, manifest-driven scrape paths, etc.
- **Sprint 30+ / speculative:** 5 items.

Full rung table at §10 of canonical doc.

## The 15 questions

Each has a recommended answer in **bold**. Answer with `ack` / `ack but <change>` / `no, <alt>` / `defer`. Where you want context, the canonical doc's section reference is in parentheses.

### Q1. FlexRadio MAC OUI prefix(es) for ARP filter (Rung 1.5a)

Empirical data needed. **Recommend: ask Don to run `arp -a > arp.txt` on his Windows machine and drop the result in his Dropbox folder. 10-second ask. Without confirmed OUI bytes, Rung 1.5a is broken.** Your 8600 is still boxed; Don is the practical source.

ACK?

### Q2. SmartSDR `SSDR.settings` schema — empirical data needed

**Recommend: ask Don to also send the contents of his `%APPDATA%\FlexRadio Systems\SSDR.settings` file (zipped if convenient).** Letting us write a schema-aware parser instead of regex-only improves Rung 1.7 hit rate and trace cleanliness.

ACK?

### Q3. Background watchdog default — foreground-only, always-on, or user-configurable?

**Recommend: foreground-only default with toggle in Settings → Connection → Discovery.** Battery-friendly, friction-tax-friendly, opt-in to always-on for power users.

ACK?

### Q4. Third-party config scrape consent UX — first-run prompt, blanket on-by-default, or opt-in only?

**Recommend: on-by-default with first-run disclosure paragraph.** Per friction-tax principle. Per-app toggles in Settings for granular control.

ACK?

### Q5. NLM gating of cached-IP rungs — strict or soft?

**Recommend: strict (skip cached IP if NLM GUID differs from when it was cached).** Strict avoids wasted probes on coffee-shop hotspots. Empirical NLM-stability validation needed but expected fine.

ACK?

### Q6. Rung 1c N-deep history — how many IPs per radio?

**Recommend: 5 with LRU eviction.**

ACK?

### Q7. Subnet-probe consent dialog on Public networks — every time, once per network, or never?

**Recommend: once per new network.** Per S5 §3.13.

ACK?

### Q8. Rung 4 SmartLink-as-fallback-row UX — show as cascade runs (Pattern A) or only after exhaustion?

**Recommend: as the cascade runs (Pattern A).** Stream 4's single biggest UX-win finding.

ACK?

### Q9. Rung 4b SmartLink-as-metadata-provider — require empirical handle-strict-vs-informational test before building, or build constrained-case now?

**Recommend: build constrained-case now, upgrade to best-case after live test.** Empirical test is cheap (~5 min on your radio when unboxed) but doesn't gate today's build.

ACK?

### Q10. Manifest-driven third-party app paths via Data Provider — defer to 4.2.x or fold into 4.2.0 Rung 1.7?

**Recommend: defer to 4.2.x.** Hardcoded list is fine for v1; manifest is a nice 4.2.x upgrade.

ACK?

### Q11. Adaptive timeout via gateway-RTT estimate — opt-in or default?

**Recommend: default with skip-on-fast-network heuristic** (skip the estimate if the first cached-IP probe succeeds in <50ms).

ACK?

### Q12. Rung 1.7 walk priority — alphabetical, configurable, or hardcoded SmartSDR-first?

**Recommend: hardcoded SmartSDR-first** (highest hit rate per Stream 3).

ACK?

### Q13. Surface "found via" attribution in the radio chooser?

"Found via SmartSDR config" / "found via your network." **Recommend: yes.** Reinforces the cascade-worked-harder-than-SmartSDR narrative. Light cost.

ACK?

### Q14. Per-network learned state — privacy review

SSIDs and gateway-MACs accumulate locally over time. They're LOCAL ONLY (never exported per `project_no_silent_phone_home.md`) but they exist. **Recommend: ack the principle. Add to support-package redaction list when that format is finalized.**

ACK?

### Q15. Default cascade ceiling — 30s, 60s, or longer?

**Recommend: 30s default with stuck-modal-escape's 5-minute upper bound.** Round 2 didn't specify; v3 closes that gap.

ACK?

## After ACK

Once you've answered (or revised) the 15 questions:

1. I update the canonical v3 memo with your answers locked in.
2. Spawn `track/discovery-chain-full-buildout` against the locked spec.
3. Round 4 update folds in your hardware findings (Q1, Q2, Q9 empirical bits) and any Stream 2 surprises.

## Confirm or revise

Add `**** ACK` next to each question's recommendation, OR `**** <alternative>`, OR `**** defer` for ones you want to think about later. The build track can spawn against any subset that has a clear answer; deferred questions just stay open and don't block parallel rung work.
