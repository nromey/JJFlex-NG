# JJ Flex Research & Work Queue

**Working dashboard.** Distinct from `docs/planning/vision/JJFlex-TODO.md` (long-lived strategic backlog) — this file tracks what's actually queued, in flight, blocked, or waiting for Noel's read **right now**.

**Last updated:** 2026-05-04 — Claude updates this whenever items move between states. If the timestamp drifts more than a session, flag it.

**How to use:** Noel scans the sections below to pick what to fire off, or asks Claude to recommend based on what's available. Claude is expected to keep this current.

---

## In flight (running now)

- **R5 diagnostic build for FlexLib 4.2.18 silent-discovery** — Build complete (2026-05-04 14:17, FileVersion 4.1.16.0). MmcssPipelineScheduler.Instance redirected to TaskScheduler.Default. **Now SUPERSEDED by R6 plan** — R5 patch will ship as part of R6 (combined MMCSS test + Rung 1a + Rung 1b). R6 holds for SmartLink auth research outcome before building. → memory: `project_flexlib_4218_discovery_investigation.md`


## Queued — agent-ready (fire whenever)

These are bounded research tasks suitable for background agents. Each produces a memo and updates a memory entry.

- **AetherSDR CW implementation review** — Spelunk `c:\dev\aether*` for their CW path; identify FlexLib API usage; note borrowable patterns. ~30 min agent. → memory: `project_cw_keying_design.md`

- **WinKey protocol study** — Web research on WinKey hardware/protocol; assess JJF integration feasibility and value. ~30-60 min agent. → memory: `project_cw_keying_design.md`

- **CW decode roadmap survey** — Survey FLDIGI / MRP40 / open-source CW decoders; recommend integration approach (in-process / out-of-process / external tool). ~30 min agent. → memory: `project_cw_keying_design.md`

- **Computer-side keying-input device survey** — Beyond keyboard/gamepad/touchscreen: MIDI controllers, custom HID, foot pedals. Inform input-device abstraction design. ~30 min agent. → memory: `project_cw_keying_design.md`

- **Tier 2 GregorR `.rnnn` format compatibility test** (~30 min spike, can be human-loop with the v0.2 runtime + sample model files). Verify 2018-era GregorR models load against Xiph v0.2's changed binary weights format. If they load, Tier 2 has content. If not, Tier 2 ships empty at 4.2.0 and the path forward is training our own. → memory: `project_dsp_controls_design.md`

- **HF SSB listening tests with Don and Justin** (human-loop, post-build). Validate Tier 2 candidates against real ham audio. Tier 2 ranking is a paper guess until subjective tests confirm. Both testers are SmartSDR Plus per memory. → memory: `project_dsp_controls_design.md`

- **Training workflow design (REFRAMED 2026-05-04)** — split into two scoped questions: (a) capture-side UX in JJ Flex on Windows, reusing partial SUB-capture infrastructure; (b) Mac training utility on Apple Silicon GPU. Clean handoff format between them. Audio sources identified: 75m for clean voice, 20m DX for weak-signal training, both bands contribute characteristic noise. ~45-60 min total when broken into two memos. → memory: `project_dsp_controls_design.md`


- **Cross-radio favorites format spec** — Brief design memo on portable favorites exchange format (item-name keying vs EX-number keying for cross-model portability). ~20 min, can be agent or human-loop. → memory: `project_ts590_menu_favorites_design.md`

## Build-authorized — code work waiting

- **TS-590 metadata catalog Phase 1** — Hand-curate ~70-80 EX-menu items per model (TS-590S + TS-590SG) from Kenwood manuals into JSON files at `radios/kenwood-ts590s.json` and `radios/kenwood-ts590sg.json`. Build-now-ship-later authorized. ~2-4 hours agent time (lookup-heavy). → memory: `project_ts590_menu_favorites_design.md`

- **Discovery fallback chain implementation** — **TRACK SPUN UP 2026-05-04.** Worktree at `C:\dev\jjflex-discovery-chain` on branch `track/discovery-fallback-chain` (branched from `track/flexlib-42`). TRACK-INSTRUCTIONS.md in worktree root. Noel runs implementation in a separate CLI window. R6 deliverable: FlexLib vendor patch + Phase 1 (Rung 1a — cached LAN IP) + Phase 1.5 (Rung 1b — cached WAN IP, constrained case) + trace instrumentation. Design doc canonical at `docs/planning/design/discovery-fallback-chain.md`.

- **Stuck-modal escape implementation** — **TRACK SPUN UP 2026-05-04, REBASED to sprint28/home-key-qsk.** Worktree at `C:\dev\jjflex-stuck-modal` on branch `track/stuck-modal-escape` (branched from `sprint28/home-key-qsk` — pre-4.2.0 baseline, FlexLib 4.1.5). TRACK-INSTRUCTIONS.md in worktree root. **Merge target: sprint28/home-key-qsk → main as pre-4.2.0 foundation drop**, NOT track/flexlib-42. Design memo: `memory/project_stuck_modal_escape_design.md` (~275 LOC across 5 files). Includes the bonus 73-Morse-twice fix.

- **Sprint 28 bug bundle** — **CLI SESSION COMPLETE 2026-05-04.** All 7 bugs shipped on `track/sprint28-bug-bundle` (commits `6752cafa` through `564e9333`). Awaiting orchestrator merge to `sprint28/home-key-qsk` (same pattern as stuck-modal merge earlier today). PlayCwSK ee re-verify auto-resolved during the session — bug WAS still reproducible, fixed in `564e9333`. Triage doc: `docs/planning/active/sprint28-bug-bundle-triage.md`. **Merge target: sprint28/home-key-qsk → main as pre-4.2.0 foundation drop**, alongside stuck-modal.

## Awaiting Noel input (read-and-respond)


- **R5 ship authorization** — Build complete, ready to stage. Noel's "ship it" needed before Dropbox publish to Don.

- **docs/principles.md** — Created earlier today, uncommitted. Noel can commit when convenient (or as part of end-of-day seal).

- **Bug-bundle DESIGN follow-up — Q2 of for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md.** Two DESIGN entries deferred from the just-finished bundle CLI session: (a) `RunsWithoutRadio` per-command opt-out flag on `KeyTableEntry` (lets SetFreq dialog open with no radio for easter-egg input), and (b) action-aware no-radio announcement using `KeyTableEntry.ShortActionLabel`. Awaiting Noel's yes/no/partial answer. **Path forward when ACK'd:** restart CLI session in existing `C:\dev\jjflex-bug-bundle` worktree with new TRACK-INSTRUCTIONS scoped to just the chosen DESIGN items (~30-100 LOC). Not blocking the 7 polish bugs from merging — those go to main now, DESIGN items follow as a sibling commit on the same branch (or 4.2.0.x).

- **4.2.0 release execution plan — for-noel/2026-05-04-42-release-execution-plan-pull.md.** Strategic plan + Phase 0 runbook (Cloudflare R2 + DNS + rarbox). 5 open questions in the doc; user signaled tomorrow afternoon prep work (Azure signup + Cloudflare R2 enable) addresses Q2 + Section B prerequisite already.

## Blocked

- **R5 outcome interpretation** — Blocked on Don's trace. ETA: 1-2 days. Surgery 2026-05-05 overlaps; realistic read window Wednesday-Thursday afternoon/evening.

- **Phase D firmware update implementation** — Blocked on R5 outcome AND discovery fallback chain ACK. Both unblock different aspects: R5 settles whether MMCSS is the bug; fallback chain settles whether Phase D needs to use the broken UDP discovery path or can use direct-IP.

- **Verbosity channels track** — HOLD per Noel's 2026-05-04 decision. Unblocks when Sprint 30+ formally opens.

- **CW live-paddle work (Phase 1)** — Sprint 30+ scope per 2026-05-04 decision. Not currently blocking anything.

- **8600 unbox** — Trigger condition (firmware drop) is met (SmartSDR 4.2.18 installed today), but personal capacity blocked by surgery. Unblocks post-recovery week. → memory: `project_8600_unbox_firmware_trigger.md`

## Done today (clears at end-of-day seal)

- **Stuck-modal track spun up** — `track/stuck-modal-escape` worktree at `C:\dev\jjflex-stuck-modal`, branched from `track/flexlib-42`. TRACK-INSTRUCTIONS.md replaces the inherited stale Track B (flexlib-42) text. Ready for Noel to start a fresh CLI session with `Start track/stuck-modal-escape from TRACK-INSTRUCTIONS.md`.

- **Sprint 28 bug bundle triage** — wrote `docs/planning/active/sprint28-bug-bundle-triage.md`. 14 entries from the 2026-04-27/28 morning briefing categorized: 3 already FIXED (KEY CONFLICT, F2 prefix, Help Escape), 2 IN-FLIGHT (collapsed into stuck-modal track), 2 OPEN connect-time investigation (Sprint 29 with trace persistence), 7 OPEN polish bugs proposed as `track/sprint28-bug-bundle` (~195 LOC). Three open questions for Noel before track spin-up.

- **for-claude queue ingestion** — 4 docs processed (verbosity / CW keying / DSP controls / TS-590 favorites). 1 memory update + 3 new memory entries + Hamlib spike note + index update. → all four for-claude copies deleted.

- **MMCSS suspect identification** — Background agent + Noel's controlled-experiment falsification narrowed FlexLib 4.2.18 silent-discovery suspect set from 3 candidates + 2 backup theories down to essentially MmcssPipelineScheduler. → memo: `docs/planning/active/don-flexlib-4218-discovery/smartsdr-decompile-analysis.md`

- **R5 patch + clean Debug build** — MmcssPipelineScheduler.Instance redirected to TaskScheduler.Default. Build verified fresh.

- **docs/principles.md created** — Four-principle public design doc at top-level docs path.

- **for-noel/2026-05-04-discovery-fallback-chain-pull.md drafted** — Architectural design memo for chain-not-gate discovery model.

- **research-queue.md** (this file) created.

- **RNN library survey** — Recommendation: **Option B (P/Invoke Xiph rnnoise v0.2 upstream)**. Critical finding: Xiph v0.2 (April 2024) added native model-loading APIs (`rnnoise_model_from_filename` etc.) — model selection is no longer fork-only. rnnoise-nu confirmed dead. DeepFilterNet flagged for watchlist (model-selection-capable but ONNX runtime + tens-of-MB models clash with friction-tax). Decision matrix favored Xiph 44/50. Memo: `docs/planning/research/rnn-library-survey.md`. DSP controls memory updated with locked recommendation.

- **kenwoodtalk + Kenwood RPC UX pattern study** — "kenwoodtalk" doesn't appear to be a real public product. Real prior art identified: Kenwood ARCP-590/590G (vendor's official software), RT Systems KRS-590, Hamlib, and **the radio's own built-in Quick Menu feature**. Most ham software (HRD, N1MM, fldigi, etc.) doesn't expose full TS-590 EX menu — JJ Flex is filling a real gap. Recommendation: mirror the radio's Quick Menu pattern (single-key in-place toggle) + ARCP-590G's dual-view (flat numeric + category). Memo: `docs/planning/research/kenwood-menu-favorites-ux-survey.md`. Cleaned up one anti-pattern-naming slip (BlindCat → "the project's accessibility anti-pattern checklist") per `feedback_dont_name_competitor_in_repo.md`.

- **SmartLink direct-connect auth feasibility** — Verdict: **constrained case**. TLS handshake is anonymous; first command after connect is `wan validate handle=<H>` with H being an opaque per-attempt server-issued token. Handle cannot be cached locally. Rung 1b saves ~500ms (radio-list refresh) + variable NAT savings, NOT the full SmartLink round-trip. **Testable upgrade path:** is the handle-check strictly enforced or informational-only? Live test on a real radio (~5 min) would resolve; if informational-only, verdict flips to best case. Memo: `docs/planning/research/smartlink-auth-direct-connect-feasibility.md`. Discovery doc round 2 updated with verdict.

- **Discovery fallback chain design — round 2 ACK'd, doc moved to permanent home, track spun up.** Round 1 + round 2 reviewed. Round 1.5 ARP-table-pre-filter folded into Rung 2 per Q1 ACK. R6 wait-for-Rung-1b-research confirmed. Design doc moved from `for-noel/` to `docs/planning/design/discovery-fallback-chain.md`. Worktree created at `C:\dev\jjflex-discovery-chain` on `track/discovery-fallback-chain`. TRACK-INSTRUCTIONS.md written for the parallel CLI session. for-claude copy deleted.

- **RNN+SUB multi-threading evaluation** — Verdict: **sequential is right, do not parallelize.** Combined per-frame compute (~0.25-0.35ms) is well under the 10ms audio frame budget; splitting stages across cores adds ~10ms of ring-buffer latency at each split point — 50-100x latency penalty for a sub-millisecond compute win. SUB-then-RNN topology confirmed published-best (Wiener-post-filter pattern). CPU underrun risk near zero on 2017+ hardware. Concrete combined-on preset recommended: SpectralStrength=0.45 / SpectralFloor=0.04 / RnnStrength=0.65 (re-balanced from single-stage defaults). Strategic finding for waterfall sprint: real multi-core opportunity is fan-out stages (FFT tap, recording tap), not splitting inline NR. Memo: `docs/planning/research/rnn-sub-multithreading-evaluation.md`. DSP controls memory updated.

- **Community RNN models survey** — Tier 1 (installer default): Xiph upstream v0.2 stock (only BSD-3-Clause + v0.2-format-compatible candidate). Tier 2 (downloadable pack): three GregorR models — `bd` quiet-shack-voice, `lq` noisy-shack-voice, `sh` speech-only-nets. **Two open risks queued:** (1) format-compatibility test for GregorR `.rnnn` files against v0.2 runtime — they may not load (~30-min spike); (2) listening tests on HF SSB with Don/Justin to validate Tier 2 ranking is a paper guess. **Strategic finding:** no ham-specific RNN model exists in public circulation. JJF could become the first source via sidecar training tool — differentiation opportunity natural-paired with JJ Flex Mac + Apple Silicon GPU training. Memo: `docs/planning/research/community-rnn-models-survey.md`. DSP controls memory updated with Tier 1/2 selection.

---

## Conventions for maintenance

- **Item shape:** `**Title** — Status (one phrase). What it produces. Scope. → cross-ref.` Keep terse — single bullet per item, no nested structure.
- **Move items between sections** as state changes. "Queued" → "In flight" when started. "In flight" → "Done today" when complete.
- **Done-today section** clears each end-of-day seal — items that landed today, useful for the seal commit message and the AAR.
- **Blocked items** include WHAT they're blocked on. If the blocker resolves, move them to Queued or Build-authorized.
- **Awaiting Noel input** is the highest-attention section — items here cost wall-clock time per day they wait.
- **Cross-references** are mandatory. Every item points at a memory entry, a for-noel doc, or a research output path.
