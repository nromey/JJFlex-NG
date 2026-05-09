---
type: sprint plan
sprint: 29
target version: 4.2.0
date authored: 2026-05-09
status: DRAFT — for Noel review and sign-off before track spawning
depends on:
  - track/flexlib-42 (FlexLib 4.2.18 baseline)
  - main HEAD post-foundation-drop (4.1.16.242)
  - data.jjflexible.radio LIVE (Phase 0 B-E complete 2026-05-08)
  - crashes.jjflexible.radio LIVE (Phase 0 F-G complete 2026-05-08)
ship-gates closed by this sprint:
  - "firmware can be safely copied to radios" (Phase D)
  - "crash reporter is wired up" (DispatcherUnhandledException + bundle + POST)
  - "update plan is operational" (app-updater with channels)
out of scope (Sprint 30+):
  - Waterfall (post-foundation flagship)
  - Customize Home (4.2.1 release-headline)
  - Hamlib / multi-radio (track/multi-radio)
  - BrailleElement primitive v1 (track/braille-research)
  - Verbosity channels architectural redesign
---

# Sprint 29 — Pileup Skip Elmer

**Theme: Make sure radios can find the radio, then ship the things that prove the radio is yours to update.**

This is the sprint that closes the three 4.2.0 ship gates from `memory/project_main_branch_41_posture.md`. Each gate maps to substantive code work. We do them in dependency order, not arbitrary order, and we don't merge to main until every gate is verifiably closed.

The sprint also pulls forward several long-deferred quality-of-life items (trace persistence, Short Action Labels expansion, RIT/XIT scale-adjust, tuning unity) because their build-time enables faster diagnostic iteration on the gate items. Trace persistence in particular is *foundation* — every networking diagnostic (cascade rung-failure tracing, crash-reporter context, firmware-update progress logging) gets faster after we land it.

---

## In scope — sequenced by dependency

The order below is the **build order**. Items lower in the list depend on items higher up.

### Tier 1 — Foundation (must land first, in order)

1. **Trace persistence** — manifest-driven archive of every connection's trace, LZMA2-compressed, JSON manifest with outcome enum, 30-day auto-prune, local-first with optional NAS mirror.

   - **Why first:** unlocks faster diagnostic iteration for *every* downstream item. Without it, networking issues during cascade buildout, firmware-update beta testing, and crash-reporter forensics each cost 30-60 min of trace re-creation. With it, you bisect the trace archive in seconds.
   - **Spec:** `memory/project_trace_persistence_design.md`.
   - **Estimated LOC:** ~250-350 across 3-4 files (TraceArchive.cs, TraceManifest.cs, TracingPanel.vb integration, Settings → Diagnostics tab wiring).
   - **Success criteria:** every connect/disconnect/AS-retry produces a manifested trace; old traces auto-prune at 30 days; user can browse archive via Settings → Diagnostics and copy a specific trace zip path to clipboard for crash-bundle attachment.

2. **Short Action Labels vocabulary expansion** — populate `ShortActionLabel` for the remaining ~80 commands beyond the 16 that landed in sprint28-design-followup. This is the second pillar of the cross-channel accessibility primitive.

   - **Why second:** load-bearing for braille output (Sprint 30), CW navigation announcements, future help-text generation, and Phase D firmware-update progress speech. Build-once, use-across-many.
   - **Spec:** `memory/project_short_action_labels_vocabulary.md`.
   - **Estimated LOC:** ~80-120 lines across `Radios/KeyCommandTypes.cs` and the command-registration sites in `JJFlexWpf/KeyCommands.cs` and `globals.vb`. Mostly mechanical filling-in; design pattern already proven in design-followup track.
   - **Success criteria:** every bound command has a non-empty `ShortActionLabel`; action-aware no-radio announcements speak the label for any command; vocabulary is self-consistent (verb-led, 3-6 words, no internal jargon).

### Tier 2 — Discovery cascade full buildout (parallel-capable, **highest user-facing priority**)

3. **Discovery cascade — Phase 1 hardening (Rungs 1, 1.5a, 1c)** — extend the current R6 cache-reader with: persistent N-deep IP history per radio (5 IPs LRU per Q6 ACK), MAC-OUI filter rung (Rung 1.5a per Q1 ACK), and the strict-but-don't-block-updating gate (Q5 ACK).

   - **Why now:** R6 already ships rung 1a (cached LAN IP) and proved at 296 ms on Don. Phase 1 hardening makes that resilient to networks beyond Don's specific case.
   - **Spec:** `docs/planning/design/discovery-fallback-chain-v3.md` §3-§5.
   - **Estimated LOC:** ~400-500 across `Radios/DiscoveryChain/` (extend `RadioConnectionCache.cs`, new `MacOuiFilter.cs`, new `NetworkIdentity.cs` for NLM gating).
   - **Success criteria:** Don's 6300 still connects in <500 ms via Rung 1a. New tester scenarios (different network, IP changed, etc.) fall through to Rung 1.5a and connect.

4. **Discovery cascade — Phase 2 (Rungs 1.7 third-party scrape + Rung 4 SmartLink-as-fallback-row)** — scrape SmartSDR / other Flex-app config files for known radios; surface SmartLink rows as the cascade runs (not after exhaustion, per Q8 ACK Pattern A).

   - **Why second-tier:** these are the rungs that recover users who've never run JJFlex on this network before but have run SmartSDR.
   - **Spec:** `docs/planning/design/discovery-fallback-chain-v3.md` §6-§7. Per Q12 ACK, walk priority is SmartSDR-first then alphabetical fallback (auto-progression, never user-picked).
   - **Estimated LOC:** ~600-800. The scraper has the most surface area (regex paths into multiple Flex-app config formats); SmartLink-as-row UI integration is moderate.
   - **Success criteria:** new-network user with SmartSDR installed has their radio surface in chooser within <2s. SmartLink row appears alongside cascade results, not as a separate post-cascade step.

5. **Discovery cascade — Phase 3 (Rung 5 subnet probe with consent + Rung 6 manual fallback)** — last-resort rungs.

   - **Why last:** consent-gated (Q7), bandwidth-heavy, and only fires when earlier rungs failed. Lower priority because earlier rungs catch >95% of cases.
   - **Spec:** `docs/planning/design/discovery-fallback-chain-v3.md` §8-§9.
   - **Estimated LOC:** ~400-500 (subnet probe parallelization + manual-IP-entry dialog + Q14 view/delete-collected-data UI).
   - **Success criteria:** user on a public network gets a consent dialog *only* if cascade reaches the subnet rung; manual fallback dialog accepts IP+serial and connects; collected-data viewer renders human-readable JSON in WebView2.

### Tier 3 — App-updater + crash reporter client

6. **App-updater (channels: stable / beta / nightly)** — checks `data.jjflexible.radio/jjflex-app-manifest.json` on launch and on demand; downloads installer; offers chained-updater consent if a dependent update needs it.

   - **Why before firmware:** firmware-update Phase D depends on the chained-updater pattern, and the chained-updater pattern can't be tested without the app-updater half existing.
   - **Spec:** `memory/project_chained_updater_pattern.md` + `memory/project_sprint29_updater_vision.md`. Channel naming: `stable / beta / nightly` per the `daily → nightly` rename and the `debug → beta` user-facing channel rename.
   - **Estimated LOC:** ~500-700 across `JJFlexUpdater/` (new project), Settings → Updates tab wiring, manifest-fetch path, signed-download verification (when Trusted Signing lands).
   - **Success criteria:** `Updates → Check now` produces "you're up to date" or "version X.Y available, what to do?". Channel selector switches between stable/beta/nightly. First-time nightly selection produces a "you may need to pick up the pieces" consent.

7. **Crash reporter client (WPF DispatcherUnhandledException handler)** — the client half of the crash-reporter pipeline. Server-side already LIVE on `crashes.jjflexible.radio`.

   - **Why parallel-capable:** doesn't depend on app-updater. Could land independently.
   - **Spec:** `memory/project_sprint29_crash_reporter_vision.md` + `memory/project_crash_triage_bundle_flow.md`. Bundle format must be designed for the triage agent from day 1 (not retrofit).
   - **Estimated LOC:** ~400-600 across `App.xaml.vb` (handler), `JJCrashReporter/` (new project for bundling), `Settings → Diagnostics` tab integration.
   - **Success criteria:** synthetic crash (test-mode menu item) produces a bundle, displays consent UI showing exactly what's in the bundle, POSTs to crashes.jjflexible.radio on user OK, surfaces success/failure with actionable speech. Bundle includes: exception, stack, last-N-traces (from trace persistence!), system info, JJF version, crash-time UTC.

### Tier 4 — Firmware update Phase D (depends on app-updater)

8. **Firmware update — chained-updater + .ssdr fetch + SendUpdateFile relay** — the headline 4.2.0 deliverable. Per `memory/project_firmware_update_transport_protocol.md`, JJF needs no crypto: fetch versioned `.ssdr` from `data.jjflexible.radio`, then existing FlexLib `SendUpdateFile` path uploads to radio.

   - **Why last:** depends on app-updater (chained-updater consent flows through it), depends on Tier 1+2 (cascade must successfully reach the radio for the update to apply), benefits from crash-reporter wiring (if firmware update fails mid-stream, the crash bundle captures state).
   - **Spec:** `memory/project_firmware_update_transport_protocol.md` + `memory/project_firmware_distribution_decision.md` + `memory/project_chained_updater_pattern.md` + `memory/project_firmware_install_dependency_strategy.md`.
   - **Estimated LOC:** ~600-900. Most of the surface is UX (consent flow, progress dialog with speech, post-flash verification). The actual transport is short — `file filename` + `file upload N update` + raw bytes via existing FlexLib path.
   - **Success criteria:** Don's 6300 (older firmware) successfully fetches latest `.ssdr` from data.jjflexible.radio, uploads via JJF, radio reboots, JJF re-discovers via cascade, version shows new firmware. End-to-end without SmartSDR.

### Tier 5 — UI consolidation + tuning unity (post-implementation polish)

9. **Diagnostics + Updates settings tabs** — UX consolidation per `memory/project_sprint29_diagnostics_settings_tab.md`. Replaces today's Tools→Tracing menu entry plus three scattered "Test" buttons in Settings/About.

   - **Why post-implementation:** tabs absorb features built in earlier tiers (trace browser, crash-reporter status, update channel selector, diagnostic test buttons).
   - **Estimated LOC:** ~300-500 (XAML + ViewModel + cross-feature wiring).

10. **Tuning unity** — non-modal tune (Up/Down coarse + Shift+Up/Down fine, retire `C` toggle), all audio gain into Audio expander.

    - **Spec:** `memory/project_sprint29_tune_redesign.md`. Don-driven; ships with 4.2.0.
    - **Estimated LOC:** ~200-300 across `Radios/KeyCommandTypes.cs`, `JJFlexWpf/KeyCommands.cs`, Audio expander XAML.

11. **RIT/XIT scale-adjust mode** — numbers 1-4 enter quasi-modal scale-adjust; focus-bound exit. Third application of the sticky-but-announced modal pattern.

    - **Spec:** `memory/project_sprint29_rit_xit_adjust_mode.md`.
    - **Estimated LOC:** ~150-250.

---

## Track decomposition for parallel execution

Per CLAUDE.md sprint-lifecycle rules, max 6 concurrent tracks. With the dependency graph above, here's a suggested track plan:

### Phase 1 (serial, foundation)
- **Track A (orchestrator):** trace persistence (item 1) + Short Action Labels expansion (item 2). Both substantively foundational; Track A merges first because every other track wants to build against persisted traces.

### Phase 2 (parallel, after Phase 1 merges)
- **Track B:** Discovery cascade Phase 1 hardening (item 3).
- **Track C:** Crash reporter client (item 7) — parallel to discovery work since they don't share files.
- **Track D:** App-updater foundation (item 6 minus chained-updater consent integration, which lands later in Track E).

### Phase 3 (parallel, after Phase 2 merges)
- **Track B continues:** Discovery cascade Phase 2 (item 4) + Phase 3 (item 5).
- **Track E:** Firmware update Phase D (item 8) — depends on Track D's app-updater being in main.
- **Track F:** Tuning unity + RIT/XIT scale-adjust (items 10 + 11) — UI work, independent of cascade/firmware.

### Phase 4 (orchestrator-only, post-Phase-3)
- **Track A returns:** Diagnostics + Updates settings tabs (item 9) — absorbs features from B+C+D+E. Lands as final polish before 4.2.0 cut.

---

## Merge order

1. Track A's foundation work merges first (trace persistence + Short Action Labels).
2. Tracks B/C/D merge in parallel after Phase 1, in any order. Track A merges any conflicts.
3. Track E merges after Track D (firmware depends on app-updater).
4. Track F merges any time after Phase 1 (independent).
5. Track A's Phase 4 polish merges last.
6. Final merge to main only after every ship gate is verifiably closed.

---

## What 4.2.0 ships with (when this sprint completes)

- Cascade-driven discovery that recovers the user from the largest plausible network conditions.
- Server-and-client crash reporter pipeline; user crashes reach Noel within minutes with full context.
- App-updater with stable/beta/nightly channels; users get small bundles fast, big bundles deliberately.
- Firmware update Phase D — the headline. Older-firmware radios update through JJF, no SmartSDR required.
- Trace persistence; networking diagnostics across the user base become solvable rather than guessable.
- Short Action Labels across the full vocabulary; foundation laid for Sprint 30 braille / CW nav.
- Diagnostics + Updates settings tabs; user-facing single-pane-of-glass for support.
- Tuning unity + RIT/XIT scale-adjust; Don's specific UX pain resolved.

---

## What 4.2.0 deliberately does NOT ship with

- Waterfall (Sprint 30+, signature flagship).
- Customize Home (4.2.1 release-headline per `memory/project_customize_home_vision.md`).
- Hamlib / multi-radio (track/multi-radio; release pacing TBD per `memory/project_multi_radio_capability_discovery.md`).
- BrailleElement primitive (track/braille-research; Sprint 30+ deliverable for Jamie Teh).
- DSP RNN model pack distribution (4.2.x; depends on data.jjflexible.radio + chained-updater both shipping in 4.2.0).
- SWIG-based Hamlib integration (Sprint 30+ when Hamlib track formally opens).

---

## Test matrix (anticipation — full matrix when merge is imminent)

Discovery cascade tests need Don, plus ideally a fresh-machine tester for Phase 2 third-party-scrape rungs. Crash reporter tests need synthetic crash mode + real crash reproduction. Firmware update tests need a tester with reverting-firmware tolerance — Don's 6300 is the natural candidate; a separate "we're going to push older firmware to your radio so you can update again" runbook is required because radios can't go backward via the same protocol.

The full matrix lands at `docs/planning/agile/sprint29-test-matrix.md` when implementation is ~80% complete.

---

## Memory entries this sprint will create or update

- `project_discovery_cascade_implementation.md` — NEW. Per-rung implementation notes that emerge from build (deferred questions resolved, edge cases discovered).
- `project_app_updater_implementation.md` — NEW. Manifest schema, channel switching mechanics, signed-download verification approach.
- `project_crash_bundle_format.md` — NEW. Exact schema of the bundle the client produces and the server triage agent expects.
- `project_firmware_update_implementation.md` — NEW. Phase D learnings, post-flash verification approach, edge cases (interrupted upload, version-skew, post-update cascade re-resolve).
- `project_main_branch_41_posture.md` — UPDATE. When 4.2.0 cuts, this memory self-prunes per its own self-prune rule.
- `project_foundation_phase.md` — UPDATE. Foundation phase concludes; capture which items shipped in 4.2.0 vs deferred to 4.2.1.

---

## Open questions for Noel before track spawn

These are things that would change the plan if answered differently than I'm assuming:

1. **Channel naming user-facing.** Plan assumes `stable / beta / nightly`. Per `memory/project_chained_updater_pattern.md` and the daily→nightly + debug→beta renames, that's the direction. Confirm.

2. **First-time-nightly consent text.** The "you may need to pick up the pieces" framing is direct and honest. Does it match the tone you want, or should it lean more standard ("nightly builds may contain bugs; you accept this risk by selecting this channel")?

3. **Test pass for Phase D firmware update.** Don's 6300 is the natural reproducer (older firmware = upgrade target). The risk: if the upgrade succeeds, we lose the only known reproducer for the FlexLib 4.2.18 silent-discovery bug (per `memory/project_flexlib_4218_discovery_investigation.md`). Mitigation: capture exhaustive pre-upgrade trace + .ssdr archive; don't upgrade until cascade Phase 1+2 are validated as the discovery mechanism that doesn't depend on the bug being present.

4. **Bundle format binding.** The crash bundle schema is set at Track C build-time and changes are expensive after testers start submitting bundles. Want to lock the schema as a separate planning pass before Track C spawns, or trust the implementation to surface a schema you ACK during track execution?

5. **Trusted Signing dependency.** App-updater verifies signed downloads. If Trusted Signing isn't operational by Phase 2 of this sprint, app-updater ships with signature verification stubbed (warning if unsigned). Acceptable, or hold app-updater behind Trusted Signing?

---

## After this sprint

Sprint 30 picks up: waterfall (signature flagship), BrailleElement v1 (track/braille-research deliverable for Jamie Teh), DSP RNN model pack distribution (using the chained-updater pattern shipped here), Hamlib/multi-radio depth (TS-2000 first per `memory/project_ts2000_cross_class_testbed.md`).

4.2.1 release headline: Customize Home (per `memory/project_customize_home_vision.md`).

---

## Cross-references

- `docs/planning/design/discovery-fallback-chain-v3.md` — cascade build spec (ACK locked 2026-05-08)
- `docs/planning/active/2026-05-08-foundation-phasing-agenda.md` — context that motivated this sprint
- `memory/project_main_branch_41_posture.md` — the three ship gates this sprint closes
- `memory/project_sprint29_updater_vision.md` — original Sprint 29 hoped scope
- `memory/project_sprint29_crash_reporter_vision.md` — crash reporter vision
- `memory/project_chained_updater_pattern.md` — pattern that interlocks app-updater and firmware-updater
- `memory/project_firmware_update_transport_protocol.md` — what JJF must implement vs. what FlexLib already has
- `memory/project_trace_persistence_design.md` — trace persistence design
- `memory/project_short_action_labels_vocabulary.md` — Short Action Labels vocabulary system
- `memory/project_sprint29_diagnostics_settings_tab.md` — Diagnostics + Updates tabs UX
- `memory/project_sprint29_tune_redesign.md` — tuning unity scope
- `memory/project_sprint29_rit_xit_adjust_mode.md` — RIT/XIT scale-adjust scope
- `memory/feedback_no_vendor_derivative_commits.md` — vendor-IP handling rule applies to firmware research artifacts
